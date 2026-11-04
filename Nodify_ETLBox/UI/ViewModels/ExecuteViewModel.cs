using System.Collections.ObjectModel;
using System.Data;
using System.Text;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

/// <summary>
/// The Execute dialog: runs CsvFile -> ParseValue -> DotNetObject over a whole file off the UI
/// thread, shows progress and counts, then the summary, rejects, flagged records and the first
/// output rows. The full output stays in <see cref="Result"/>.
/// </summary>
public sealed class ExecuteViewModel : ObservableObject
{
    public const int ShownRows = 200;
    public const string NoRejectsText = "No rejected rows.";
    public const string NoFlaggedText = "No flagged rows.";
    public const string NoRowsText = "The run gave no output rows.";

    private readonly PipelineDefinition _definition;
    private readonly Func<string?, string?>? _pickFile;
    private readonly Action<string>? _copyText;
    private CancellationTokenSource? _runCts;
    private string _filePath;
    private bool _isRunning;
    private bool _showCancel;
    private bool _hasRun;
    private PipelineCounts _counts = new(0, 0, 0, 0, 0);
    private string? _message;
    private bool _isLicenceMessage;
    private string _summary = "Press Run to read the whole file.";
    private string? _cancelledText;
    private string? _outputNote;
    private DataView? _outputRowsView;
    private PipelineResult? _result;

    /// <param name="pickFile">Shows a file dialog, starting at the given path; null when the user cancels.</param>
    /// <param name="copyText">Puts text on the clipboard.</param>
    public ExecuteViewModel(PipelineDefinition definition, Func<string?, string?>? pickFile = null, Action<string>? copyText = null)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _pickFile = pickFile;
        _copyText = copyText;
        _filePath = definition.CsvFile.Path;

        RunCommand = new AsyncRelayCommand(async _ => await RunAsync(), _ => !IsRunning && !string.IsNullOrWhiteSpace(FilePath));
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsRunning);
        BrowseCommand = new RelayCommand(_ => Browse(), _ => !IsRunning && _pickFile is not null);
        CopyRejectsCommand = new RelayCommand(_ => _copyText?.Invoke(RejectsAsText()), _ => Rejects.Count > 0 && _copyText is not null);
    }

    public AsyncRelayCommand RunCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand BrowseCommand { get; }

    public RelayCommand CopyRejectsCommand { get; }

    /// <summary>The file to run. Defaults to the CsvFile node's path; another export with the same layout can be chosen.</summary>
    public string FilePath
    {
        get => _filePath;
        set
        {
            if (SetProperty(ref _filePath, value ?? string.Empty))
            {
                RunCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RunCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                BrowseCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The Cancel button shows when a run has lasted half a second (spec section 7).</summary>
    public bool ShowCancel
    {
        get => _showCancel;
        private set => SetProperty(ref _showCancel, value);
    }

    public PipelineCounts Counts
    {
        get => _counts;
        private set
        {
            if (SetProperty(ref _counts, value))
            {
                OnPropertyChanged(nameof(CountsText));
            }
        }
    }

    public string CountsText => $"Read {Counts.Read:N0} · Parsed {Counts.Parsed:N0} · Rejected {Counts.Rejected:N0} · Flagged {Counts.Flagged:N0}";

    /// <summary>A message that stopped the run (missing file, columns, licence limit). Null when none.</summary>
    public string? Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public bool IsLicenceMessage
    {
        get => _isLicenceMessage;
        private set => SetProperty(ref _isLicenceMessage, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>"Cancelled after N records. Rows so far are shown." after a cancel; otherwise null.</summary>
    public string? CancelledText
    {
        get => _cancelledText;
        private set => SetProperty(ref _cancelledText, value);
    }

    /// <summary>"Showing the first 200 of N rows", "Showing all N rows", or the no-rows text.</summary>
    public string? OutputNote
    {
        get => _outputNote;
        private set => SetProperty(ref _outputNote, value);
    }

    /// <summary>The first <see cref="ShownRows"/> output rows, in the column order of the result.</summary>
    public DataView? OutputRowsView
    {
        get => _outputRowsView;
        private set => SetProperty(ref _outputRowsView, value);
    }

    public ObservableCollection<RecordIssue> Rejects { get; } = new();

    public ObservableCollection<RecordIssue> Flagged { get; } = new();

    public bool ShowNoRejects => _hasRun && Rejects.Count == 0;

    public bool ShowNoFlagged => _hasRun && Flagged.Count == 0;

    /// <summary>The last result, with every output row in memory.</summary>
    public PipelineResult? Result
    {
        get => _result;
        private set => SetProperty(ref _result, value);
    }

    public async Task RunAsync()
    {
        if (IsRunning)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _runCts = cts;
        IsRunning = true;
        ShowCancel = false;
        Message = null;
        IsLicenceMessage = false;
        CancelledText = null;
        Summary = "Running...";
        Counts = new PipelineCounts(0, 0, 0, 0, 0);
        _ = ShowCancelAfterDelayAsync(cts.Token);

        // Progress<T> posts to the UI thread when there is one; the runner reports at most every 250 ms.
        var progress = new Progress<PipelineProgress>(p =>
        {
            if (IsRunning)
            {
                Counts = p.Counts;
            }
        });

        PipelineResult result;
        try
        {
            var path = FilePath;
            result = await Task.Run(() => PipelineRunner.RunAsync(_definition, path, progress, cts.Token));
        }
        catch (Exception ex)
        {
            // The runner turns known failures into messages; this is the last safety net.
            result = new PipelineResult(Array.Empty<string>(), new(), Array.Empty<RecordIssue>(), Array.Empty<RecordIssue>(),
                new PipelineCounts(0, 0, 0, 0, 0), false, 0, $"The run stopped with an unexpected error. Check the settings and run again. Details: {ex.Message}");
        }
        finally
        {
            _runCts = null;
            IsRunning = false;
            ShowCancel = false;
        }

        Apply(result);
    }

    private void Apply(PipelineResult result)
    {
        Result = result;
        _hasRun = true;
        Counts = result.Counts;
        Message = result.Error;
        IsLicenceMessage = result.LicenceLimitReached;
        CancelledText = result.Cancelled ? $"Cancelled after {result.Counts.Read:N0} records. Rows so far are shown." : null;

        Summary = $"Read {result.Counts.Read:N0} · Parsed {result.Counts.Parsed:N0} · Rejected {result.Counts.Rejected:N0} · "
                  + $"Flagged {result.Counts.Flagged:N0} · Out {result.Counts.Out:N0} · {result.ElapsedMs:N0} ms";

        Rejects.Clear();
        foreach (var reject in result.Rejects)
        {
            Rejects.Add(reject);
        }

        Flagged.Clear();
        foreach (var flag in result.Flagged)
        {
            Flagged.Add(flag);
        }

        OutputRowsView = BuildTable(result).DefaultView;
        OutputNote = result.Rows.Count == 0 ? NoRowsText
            : result.Rows.Count > ShownRows ? $"Showing the first {ShownRows} of {result.Rows.Count:N0} rows"
            : $"Showing all {result.Rows.Count:N0} rows";

        OnPropertyChanged(nameof(ShowNoRejects));
        OnPropertyChanged(nameof(ShowNoFlagged));
        CopyRejectsCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Tab-separated: a header line, then one line per reject.</summary>
    public string RejectsAsText()
    {
        var text = new StringBuilder("record_number\treason").AppendLine();
        foreach (var reject in Rejects)
        {
            text.Append(reject.RecordNumber).Append('\t').AppendLine(reject.Reason.Replace('\t', ' ').ReplaceLineEndings(" "));
        }

        return text.ToString();
    }

    private void Cancel() => _runCts?.Cancel();

    private void Browse()
    {
        var chosen = _pickFile?.Invoke(FilePath);
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            FilePath = chosen;
        }
    }

    private async Task ShowCancelAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
            if (IsRunning)
            {
                ShowCancel = true;
            }
        }
        catch (OperationCanceledException)
        {
            // The run was cancelled before the delay ended.
        }
    }

    /// <summary>A column name used twice gets " (2)", because a DataTable needs unique names (as in ParseStudio).</summary>
    private static DataTable BuildTable(PipelineResult result)
    {
        var table = new DataTable();
        var names = new List<string>(result.Columns.Count);
        foreach (var column in result.Columns)
        {
            var name = column;
            for (var n = 2; table.Columns.Contains(name); n++)
            {
                name = $"{column} ({n})";
            }

            table.Columns.Add(name, typeof(string));
            names.Add(column);
        }

        foreach (var row in result.Rows.Take(ShownRows))
        {
            table.Rows.Add(names.Select(c => (object)(row.TryGetValue(c, out var v) ? v : string.Empty)).ToArray());
        }

        return table;
    }
}
