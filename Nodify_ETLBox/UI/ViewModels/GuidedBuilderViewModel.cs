using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Generation;
using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.Mvvm;
using EtlDesigner.UI.Services;

namespace EtlDesigner.UI.ViewModels;

/// <summary>The five steps of the guided builder.</summary>
public enum GuidedStep
{
    File = 1,
    Table = 2,
    Columns = 3,
    Try = 4,
    Save = 5
}

/// <summary>One step in the step bar.</summary>
public sealed record GuidedStepItem(GuidedStep Step, string Title, bool IsCurrent, bool IsDone)
{
    public string Text => $"{(int)Step} {Title}";
}

/// <summary>
/// One table of the file. <see cref="ReadsCleanly"/> is false when the reader stopped on it; then
/// <see cref="Problem"/> says why in plain words and, when a detected table that starts on the same line reads
/// cleanly, <see cref="Suggestion"/> names it and <see cref="SplitBefore"/> is where to cut.
/// </summary>
public sealed record GuidedTable(int Number, string Title, string Lines, string RowsText, bool ReadsCleanly, string? Problem, string? Suggestion,
    long? SplitBefore, string? Details)
{
    public string Summary => $"{Title} · {Lines} · {RowsText}";
}

/// <summary>
/// "Build a reader" (task G7a): file, table, columns, try it, save as DLL, for a lab user. It builds the same
/// chain as the canvas (File intake -> CSV part -> DotNetObject, see <see cref="MainViewModel.ApplyGuidedChain"/>)
/// and reuses the intake scan, the edit session, the selector, the run's reader, the pipeline runner and the DLL
/// generator. No window types; the window only binds. No node, port or JSON words in its texts.
/// </summary>
public sealed partial class GuidedBuilderViewModel : ObservableObject
{
    public const string ChooseFileText = "Choose the file your instrument exported. Browse, or drop it here.";
    public const string NoTableText = "No table was found in this file. Choose another file, or open Intake Studio (Advanced) to mark the table.";
    public const string ScanFailedText = "The file could not be read. Check that it exists and is not open in another program, then choose it again.";
    public const string TableQuestion = "Is this your data?";
    public const string NeighbourText = "This table includes lines from another table. Pick {0} instead.";
    public const string GuidedNotePrefix = "Guided table: part ";
    public const int TablePreviewRows = 20;
    public const int CancelDelayMs = 500;

    private readonly MainViewModel _main;
    private readonly ILineageLog _lineage;
    private readonly ITemplateStore _templates;
    private readonly TemplateSettings _templateSettings;
    private readonly Func<string?, string?>? _pickFile;
    private readonly Func<string?, string?>? _pickFolder;
    private readonly Func<string, bool> _confirm;
    private readonly Action<IntakeStudioViewModel>? _showStudio;
    private readonly Func<CsvPipelineDllRequest, CsvPipelineDllResult>? _generate;
    private readonly Func<string, IntakeItem> _createItem;
    private readonly CsvPreviewReader _reader = new();
    private readonly Dictionary<int, CsvPreview> _previews = new();
    private readonly string _guidedId = Guid.NewGuid().ToString("D");

    private GuidedStep _step = GuidedStep.File;
    private string _filePath = string.Empty;
    private bool _isScanning;
    private bool _isCancelVisible;
    private int _progressPercent;
    private string _progressText = string.Empty;
    private string? _fileMessage = ChooseFileText;
    private string? _fileDetails;
    private string? _knownLayoutText;
    private int? _preferredTable;
    private bool _isCheckingTables;
    private GuidedTable? _selectedTable;
    private DataView? _tablePreview;
    private string? _tableMessage;
    private int _scanVersion;
    private CancellationTokenSource? _scanCancel;
    private IntakeEditSession? _session;
    private FileIntakeNodeSettings _settings = FileIntakeNodeSettings.Default;

    public GuidedBuilderViewModel(
        MainViewModel main,
        ILineageLog lineage,
        ITemplateStore templates,
        TemplateSettings? templateSettings = null,
        Func<string?, string?>? pickFile = null,
        Func<string?, string?>? pickFolder = null,
        Func<string, bool>? confirm = null,
        Action<IntakeStudioViewModel>? showStudio = null,
        Func<CsvPipelineDllRequest, CsvPipelineDllResult>? generate = null,
        Func<string, IntakeItem>? createItem = null)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _lineage = lineage ?? throw new ArgumentNullException(nameof(lineage));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _templateSettings = templateSettings ?? new TemplateSettings();
        _pickFile = pickFile;
        _pickFolder = pickFolder;
        _confirm = confirm ?? (_ => false);
        _showStudio = showStudio;
        _generate = generate;
        _createItem = createItem ?? (path => new FileIntakeFactory().CreateItem(path));

        NextCommand = new RelayCommand(_ => GoNext(), _ => NextProblem is null);
        BackCommand = new RelayCommand(_ => GoBack(), _ => Step != GuidedStep.File && !IsBusy);
        BrowseCommand = new RelayCommand(_ => Browse(), _ => !IsScanning && _pickFile is not null);
        CancelCommand = new RelayCommand(_ => CancelWork(), _ => IsBusy);
        UseSuggestionCommand = new RelayCommand(_ => UseSuggestion(), _ => SelectedTable?.SplitBefore is not null && !IsBusy);
        AdvancedCommand = new RelayCommand(_ => OpenIntakeStudio(), _ => _session is not null && _showStudio is not null && !IsBusy);
        DropFileCommand = new RelayCommand(p =>
        {
            if (p is string path && !string.IsNullOrWhiteSpace(path))
            {
                FilePath = path;
            }
        }, _ => !IsScanning);
        CreateRunCommands();

        _lineage.Write(new LineageEvent("guided.started", _guidedId, null, null, new Dictionary<string, object?>()));
    }

    public RelayCommand NextCommand { get; }

    public RelayCommand BackCommand { get; }

    public RelayCommand BrowseCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand UseSuggestionCommand { get; }

    public RelayCommand AdvancedCommand { get; }

    /// <summary>A file dropped on the window (the path as the parameter) is chosen and scanned.</summary>
    public RelayCommand DropFileCommand { get; }

    /// <summary>The window closes when this is raised ("Open in the canvas" or Close).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The last scan (for tests and the window).</summary>
    public Task? ScanTask { get; private set; }

    public GuidedStep Step
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
            {
                OnPropertyChanged(nameof(StepItems));
                OnPropertyChanged(nameof(StepNumber));
                OnPropertyChanged(nameof(StepIndex));
                OnPropertyChanged(nameof(IsLastStep));
                RaiseStates();
            }
        }
    }

    /// <summary>The step as a number, for the window's step panels.</summary>
    public int StepNumber => (int)Step;

    /// <summary>The step as a 0-based index, for the window's page.</summary>
    public int StepIndex => (int)Step - 1;

    public bool IsLastStep => Step == GuidedStep.Save;

    public IReadOnlyList<GuidedStepItem> StepItems =>
    [
        new(GuidedStep.File, "File", Step == GuidedStep.File, Step > GuidedStep.File),
        new(GuidedStep.Table, "Table", Step == GuidedStep.Table, Step > GuidedStep.Table),
        new(GuidedStep.Columns, "Columns", Step == GuidedStep.Columns, Step > GuidedStep.Columns),
        new(GuidedStep.Try, "Try it", Step == GuidedStep.Try, Step > GuidedStep.Try),
        new(GuidedStep.Save, "Save as DLL", Step == GuidedStep.Save, false)
    ];

    /// <summary>Why Next is not possible now, in one sentence; null when it is.</summary>
    public string? NextProblem => IsBusy ? "Wait until the current work has finished, or press Cancel." : Step switch
    {
        GuidedStep.File => _session is null ? "Choose a file first." : Tables.Count == 0 ? NoTableText : null,
        GuidedStep.Table => SelectedTable is null ? "Choose a table first."
            : !SelectedTable.ReadsCleanly ? SelectedTable.Problem ?? "This table cannot be read; choose another one." : null,
        GuidedStep.Columns => ColumnsProblem,
        GuidedStep.Try => TrySucceeded ? null : "Press \"Try on the sample file\" first; it must work with the settings as they are now.",
        _ => "This is the last step."
    };

    public string NextToolTip => NextProblem ?? "Go to the next step.";

    /// <summary>A scan, a table check, a try or a DLL build is running.</summary>
    public bool IsBusy => IsScanning || _isCheckingTables || IsTrying || IsTestingOther || (Dll?.IsGenerating ?? false);

    // ----- Step 1: file -----

    /// <summary>Setting a file starts the scan.</summary>
    public string FilePath
    {
        get => _filePath;
        set
        {
            value = value?.Trim() ?? string.Empty;
            if (SetProperty(ref _filePath, value))
            {
                OnPropertyChanged(nameof(FileName));
                if (value.Length > 0)
                {
                    ScanTask = ScanAsync();
                }
            }
        }
    }

    public string FileName => string.IsNullOrWhiteSpace(FilePath) ? "No file chosen" : Path.GetFileName(FilePath);

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                RaiseStates();
            }
        }
    }

    public bool IsCancelVisible
    {
        get => _isCancelVisible;
        private set => SetProperty(ref _isCancelVisible, value);
    }

    public int ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    /// <summary>What happened to the file, in plain words.</summary>
    public string? FileMessage
    {
        get => _fileMessage;
        private set => SetProperty(ref _fileMessage, value);
    }

    /// <summary>The technical text behind <see cref="FileMessage"/>, for the Details expander.</summary>
    public string? FileDetails
    {
        get => _fileDetails;
        private set => SetProperty(ref _fileDetails, value);
    }

    /// <summary>"Known layout: name" when a saved layout fits the file and was applied.</summary>
    public string? KnownLayoutText
    {
        get => _knownLayoutText;
        private set => SetProperty(ref _knownLayoutText, value);
    }

    /// <summary>The edit session of the last finished scan (the layout as it is now).</summary>
    public IntakeEditSession? Session => _session;

    private void Browse()
    {
        if (_pickFile?.Invoke(string.IsNullOrWhiteSpace(FilePath) ? null : FilePath) is { } chosen && !string.IsNullOrWhiteSpace(chosen))
        {
            FilePath = chosen;
        }
    }

    /// <summary>Scans the file (off the UI thread), makes the edit session, applies a known layout and checks every table.</summary>
    public async Task ScanAsync()
    {
        var version = ++_scanVersion;
        _scanCancel?.Cancel();
        using var cancel = new CancellationTokenSource();
        _scanCancel = cancel;
        ResetFromTable();
        _session = null;
        KnownLayoutText = null;
        FileDetails = null;
        _preferredTable = null;
        FileMessage = "Reading the file...";
        ProgressPercent = 0;
        ProgressText = string.Empty;
        IsScanning = true;
        _ = ShowCancelAfterDelayAsync(version);
        var progress = new Progress<IntakeScanProgress>(p =>
        {
            if (version == _scanVersion && IsScanning)
            {
                ProgressPercent = p.Percent;
                ProgressText = p.Text;
            }
        });

        try
        {
            _settings = FileIntakeNodeSettings.Default with { Path = FilePath };
            var settings = _settings;
            var item = _createItem(settings.Path);
            var intakeId = Guid.NewGuid().ToString("D");
            var result = await new IntakeScanService(settings.ToIntakeSettings(), _lineage).ScanAsync(item, progress, cancel.Token, intakeId);
            if (version != _scanVersion)
            {
                return;
            }

            if (result.Cancelled)
            {
                FileMessage = PlainErrorText.Cancelled;
                return;
            }

            if (result.Error is not null || result.Intake is not { File: not null, Error: null, Cancelled: false })
            {
                var plain = PlainErrorText.Map(result.Error ?? result.Intake?.Error ?? ScanFailedText);
                FileMessage = plain.Sentence;
                FileDetails = plain.Details;
                return;
            }

            var (session, known, preferred) = await Task.Run(() => StartSession(item, result, settings, intakeId));
            if (version != _scanVersion)
            {
                return;
            }

            _session = session;
            KnownLayoutText = known;
            _preferredTable = preferred;
            FileMessage = $"Read {FileName}: {session.Containers.Count} part{(session.Containers.Count == 1 ? "" : "s")} found.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            FileMessage = ScanFailedText;
            FileDetails = ex.Message;
            return;
        }
        finally
        {
            if (version == _scanVersion)
            {
                IsScanning = false;
                IsCancelVisible = false;
                if (ReferenceEquals(_scanCancel, cancel))
                {
                    _scanCancel = null;
                }
            }
        }

        await CheckTablesAsync(_preferredTable);
    }

    /// <summary>The session, and when a saved layout fits (Exact or Close), the layout applied and the table the builder saved with it.</summary>
    private (IntakeEditSession Session, string? Known, int? Preferred) StartSession(IntakeItem item, IntakeScanResult scan, FileIntakeNodeSettings settings, string intakeId)
    {
        var session = new IntakeEditSession(item, scan, _lineage, intakeId, settings.ToIntakeSettings());
        var best = session.SuggestTemplates(_templates, _templateSettings).FirstOrDefault(c => c.Match != TemplateMatch.None);
        if (best is null || !session.ApplyTemplate(best.Template, _templateSettings).Ok)
        {
            return (session, null, null);
        }

        int? preferred = best.Template.Notes is { } notes && notes.StartsWith(GuidedNotePrefix, StringComparison.Ordinal)
                         && int.TryParse(notes[GuidedNotePrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
        return (session, $"Known layout: {best.Template.Name}", preferred);
    }

    private async Task ShowCancelAfterDelayAsync(int version)
    {
        await Task.Delay(CancelDelayMs);
        if (version == _scanVersion && IsBusy)
        {
            IsCancelVisible = true;
        }
    }

    private void CancelWork()
    {
        _scanCancel?.Cancel();
        _runCancel?.Cancel();
    }

    // ----- Step 2: table -----

    public ObservableCollection<GuidedTable> Tables { get; } = new();

    /// <summary>Choosing a table shows its first rows; the later steps start again for it.</summary>
    public GuidedTable? SelectedTable
    {
        get => _selectedTable;
        set
        {
            if (SetProperty(ref _selectedTable, value))
            {
                TablePreview = value is not null && _previews.TryGetValue(value.Number, out var preview) ? ToView(preview, TablePreviewRows) : null;
                TableMessage = value is null ? null : value.ReadsCleanly ? TableQuestion : value.Suggestion ?? value.Problem;
                OnPropertyChanged(nameof(HasSuggestion));
                LoadColumns();
                RaiseStates();
            }
        }
    }

    /// <summary>The chosen table does not read, but a cleaner table starting on the same line does.</summary>
    public bool HasSuggestion => SelectedTable?.SplitBefore is not null;

    public DataView? TablePreview
    {
        get => _tablePreview;
        private set => SetProperty(ref _tablePreview, value);
    }

    /// <summary>"Is this your data?", or why the chosen table cannot be used.</summary>
    public string? TableMessage
    {
        get => _tableMessage;
        private set => SetProperty(ref _tableMessage, value);
    }

    /// <summary>The checks of the tables (for tests).</summary>
    public Task? TablesTask { get; private set; }

    /// <summary>Checks the tables again after the layout was changed outside the builder (tests edit the session directly).</summary>
    internal Task RefreshTablesAsync(int? preferNumber = null) => CheckTablesAsync(preferNumber);

    /// <summary>
    /// Reads the first rows of every csv or tsv part with the run's reader (off the UI thread), then selects
    /// <paramref name="preferNumber"/> when it reads cleanly, else the clean table with the most rows.
    /// </summary>
    private Task CheckTablesAsync(int? preferNumber)
    {
        var task = CheckTablesCoreAsync(preferNumber);
        TablesTask = task;
        return task;
    }

    private async Task CheckTablesCoreAsync(int? preferNumber)
    {
        if (_session is not { } session)
        {
            return;
        }

        _isCheckingTables = true;
        RaiseStates();
        try
        {
            var path = FilePath;
            var (tables, previews) = await Task.Run(() => ReadTables(session, path));
            if (!ReferenceEquals(session, _session))
            {
                return;
            }

            _previews.Clear();
            foreach (var (number, preview) in previews)
            {
                _previews[number] = preview;
            }

            Tables.Clear();
            foreach (var table in tables)
            {
                Tables.Add(table);
            }

            SelectedTable = Tables.FirstOrDefault(t => t.Number == preferNumber && t.ReadsCleanly)
                            ?? Tables.Where(t => t.ReadsCleanly).OrderByDescending(t => _previews[t.Number].Rows.Count).ThenBy(t => t.Number).FirstOrDefault()
                            ?? Tables.FirstOrDefault();
            if (Tables.Count == 0)
            {
                FileMessage = NoTableText;
            }
        }
        finally
        {
            _isCheckingTables = false;
            RaiseStates();
        }
    }

    /// <summary>Every table of the layout with its read, and the plain sentence (and a cleaner neighbour) when it does not read.</summary>
    private (List<GuidedTable> Tables, List<(int, CsvPreview)> Previews) ReadTables(IntakeEditSession session, string path)
    {
        var tables = new List<GuidedTable>();
        var previews = new List<(int, CsvPreview)>();
        for (var i = 0; i < session.Containers.Count; i++)
        {
            var c = session.Containers[i];
            if (c.Format is not ("csv" or "tsv"))
            {
                continue;
            }

            var number = i + 1;
            var title = TableTitle(number, c);
            var lines = LinesText(c);
            if (CsvPartSelector.FromSession(session, i, out var refused) is null)
            {
                tables.Add(new GuidedTable(number, title, lines, "rows not known", false, refused, null, null, refused));
                previews.Add((number, new CsvPreview([], [], false, refused, 0)));
                continue;
            }

            var preview = ReadPreview(path, session, c);
            previews.Add((number, preview));
            if (preview.Error is null)
            {
                tables.Add(new GuidedTable(number, title, lines, RowsText(preview), true, null, null, null, null));
                continue;
            }

            var plain = PlainErrorText.Map(preview.Error);
            var neighbour = CleanNeighbour(session, path, c);
            tables.Add(new GuidedTable(number, title, lines, "cannot be read", false, plain.Sentence,
                neighbour is { } n ? string.Format(CultureInfo.InvariantCulture, NeighbourText, n.Name) : null, neighbour?.SplitBefore, plain.Details));
        }

        return (tables, previews);
    }

    /// <summary>A detected table that starts on the same line, ends earlier and reads cleanly: the table the user most likely meant.</summary>
    private (string Name, long SplitBefore)? CleanNeighbour(IntakeEditSession session, string path, ContainerInfo bad)
    {
        foreach (var d in session.Scan.Intake!.Containers)
        {
            if (d.Format is not ("csv" or "tsv") || d.ContentFirstLine != bad.ContentFirstLine || d.ContentLastLine is not { } last
                || last >= bad.ContentLastLine || d.Delimiter is not { Length: 1 } || d.ContentStartByte is null || d.ContentEndByte is null)
            {
                continue;
            }

            if (ReadPreview(path, session, d).Error is null)
            {
                return (d.Name is { Length: > 0 } name ? $"'{name}' (lines {N(d.ContentFirstLine!.Value)} to {N(last)})" : $"the table on lines {N(d.ContentFirstLine!.Value)} to {N(last)}",
                    last + 1);
            }
        }

        return null;
    }

    /// <summary>The first rows of a part, read by the run's reader (never the whole file).</summary>
    private CsvPreview ReadPreview(string path, IntakeEditSession session, ContainerInfo c)
    {
        var source = new CsvPartSource(path, c.ContentStartByte!.Value, c.ContentEndByte!.Value - c.ContentStartByte.Value, session.EncodingName,
            c.Delimiter!, c.HeaderGuess == HeaderGuess.Yes, c.SkipLines, c.ContentFirstLine ?? 1);
        return _reader.ReadAsync(source, CsvPreviewReader.MaxPreviewRows, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Cuts the chosen table where the cleaner neighbour ends, so the user's other layout changes stay.</summary>
    private void UseSuggestion()
    {
        if (_session is not { } session || SelectedTable is not { SplitBefore: { } line } table)
        {
            return;
        }

        var result = session.Split(table.Number - 1, line);
        if (!result.Ok)
        {
            TableMessage = result.Message;
            return;
        }

        _ = CheckTablesAsync(table.Number);
    }

    /// <summary>Opens Intake Studio on the file with the layout as it is now; its layout comes back when it closes.</summary>
    private void OpenIntakeStudio()
    {
        if (_session is not { } session || _showStudio is null)
        {
            return;
        }

        var plan = session.IsModified ? IntakePlanJson.Serialize(IntakePlan.FromSession(session, session.Scan)) : null;
        var studio = new IntakeStudioViewModel(_settings, NodeFactory.Labels, lineage: _lineage, savedPlanJson: plan,
            confirm: _confirm, templates: _templates, templateSettings: _templateSettings);
        _ = studio.ScanAsync();
        _showStudio(studio);
        if (studio.Session is { } edited && studio.ScannedSettings?.Path == _settings.Path)
        {
            _session = edited;
            ResetFromTable();
            _ = CheckTablesAsync(SelectedTable?.Number);
        }
    }

    // ----- navigation -----

    private void GoNext()
    {
        if (NextProblem is not null)
        {
            return;
        }

        if (Step == GuidedStep.Table)
        {
            SaveLayoutTemplate();
        }

        if (Step == GuidedStep.Try)
        {
            PrepareSave();
        }

        Step++;
    }

    private void GoBack()
    {
        if (Step > GuidedStep.File)
        {
            Step--;
        }
    }

    /// <summary>A new file or table: the columns, the try and the DLL start again.</summary>
    private void ResetFromTable()
    {
        Tables.Clear();
        _previews.Clear();
        SelectedTable = null;
        InvalidateTry();
    }

    private void RaiseStates()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(NextProblem));
        OnPropertyChanged(nameof(NextToolTip));
        foreach (var command in new[] { NextCommand, BackCommand, BrowseCommand, CancelCommand, UseSuggestionCommand, AdvancedCommand, DropFileCommand })
        {
            command?.RaiseCanExecuteChanged();
        }

        RaiseRunStates();
    }

    // ----- texts -----

    private static string TableTitle(int number, ContainerInfo c)
    {
        var word = CsvPartLocator.DelimiterWord(c.Delimiter);
        var kind = char.ToUpperInvariant(word[0]) + word[1..] + " table";
        return c.Name is { Length: > 0 } name ? $"Table {number}: {kind} '{name}'" : $"Table {number}: {kind}";
    }

    private static string LinesText(ContainerInfo c)
        => c.ContentFirstLine is { } first && c.ContentLastLine is { } last ? $"lines {N(first)} to {N(last)}" : "no lines";

    private static string RowsText(CsvPreview preview)
        => preview.HasMoreRows ? $"more than {N(preview.Rows.Count)} rows" : $"{N(preview.Rows.Count)} row{(preview.Rows.Count == 1 ? "" : "s")}";

    internal static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A grid of the first <paramref name="max"/> rows; a column name used twice gets a number.</summary>
    internal static DataView ToView(IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<string>> rows, int max)
    {
        var table = new DataTable();
        foreach (var column in columns)
        {
            var name = column.Length == 0 ? "(no name)" : column;
            var unique = name;
            for (var k = 2; table.Columns.Contains(unique); k++)
            {
                unique = $"{name} ({k})";
            }

            table.Columns.Add(unique, typeof(string));
        }

        foreach (var row in rows.Take(max))
        {
            table.Rows.Add(Enumerable.Range(0, columns.Count).Select(i => (object)(i < row.Count ? row[i] : string.Empty)).ToArray());
        }

        return table.DefaultView;
    }

    private static DataView ToView(CsvPreview preview, int max) => ToView(preview.Columns, preview.Rows, max);
}
