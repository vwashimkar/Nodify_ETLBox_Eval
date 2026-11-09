using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.Mvvm;
using EtlDesigner.UI.Services;

namespace EtlDesigner.UI.ViewModels;

/// <summary>One container in the Intake Studio list.</summary>
public sealed record IntakeContainerRow(
    int Number,
    string Part,
    string Lines,
    string Confidence,
    ContainerStatus Status,
    string StatusText,
    string IssueSummary,
    ContainerInfo Info,
    ContainerValidation? Validation);

/// <summary>One line of the details panel, for example "Delimiter" and "Comma".</summary>
public sealed record IntakeDetailRow(string Label, string Value);

/// <summary>
/// Intake Studio: the settings of a File intake node, a scan through <see cref="IntakeScanService"/>
/// off the UI thread, the list of containers, the details and the preview of the selected one. No
/// window types; the window binds to it. The file is opened only while a scan or a preview read
/// runs, read-only.
/// </summary>
public sealed partial class IntakeStudioViewModel : ObservableObject
{
    public const string ScanCancelledText = "Scan cancelled";
    public const string ScanFailedText = "The scan stopped. See the message below.";
    public const int PreviewMaxLines = 200;
    public const int PreviewMaxChars = 500;
    public const int CancelDelayMs = 500;

    private readonly Labels _labels;
    private readonly Func<string, IntakeItem> _createItem;
    private readonly Func<string?, string?>? _pickFile;
    private readonly Action<FileIntakeNodeSettings>? _save;
    private readonly Func<UnsavedChangesChoice>? _askUnsaved;
    private readonly SynchronizationContext? _context;

    private FileIntakeNodeSettings _savedSettings;
    private string _filePath;
    private string _mode;
    private string _encoding;
    private int _maxFileSizeMb;
    private bool _isScanning;
    private bool _isCancelVisible;
    private int _progressPercent;
    private string _progressText = string.Empty;
    private string? _message;
    private string? _encodingNotice;
    private string? _totalsText;
    private IntakeContainerRow? _selectedContainer;
    private string? _previewNote;
    private string _previewText = string.Empty;
    private int _scanVersion;
    private int _previewVersion;
    private CancellationTokenSource? _scanCancel;
    private IntakeItem? _scannedItem;
    private string? _scannedEncoding;
    private bool _closeConfirmed;
    private ObservableCollection<IntakeContainerRow> _containers = new();

    public IntakeStudioViewModel(
        FileIntakeNodeSettings settings,
        Labels? labels = null,
        Func<string?, string?>? pickFile = null,
        Action<FileIntakeNodeSettings>? save = null,
        Func<UnsavedChangesChoice>? askUnsaved = null,
        Func<string, IntakeItem>? createItem = null,
        ILineageLog? lineage = null,
        string? savedPlanJson = null,
        Action<string?>? savePlan = null,
        Func<string, bool>? confirm = null,
        Func<string, bool>? copyText = null)
    {
        _savedSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        _labels = labels ?? Labels.Empty;
        _pickFile = pickFile;
        _save = save;
        _askUnsaved = askUnsaved;
        _createItem = createItem ?? (path => new FileIntakeFactory().CreateItem(path));
        _context = SynchronizationContext.Current;
        _lineage = lineage;
        _savedPlanJson = savedPlanJson;
        _savePlan = savePlan;
        _confirm = confirm;
        _copyText = copyText ?? TextClipboard.TrySetText;

        _filePath = settings.Path;
        _mode = settings.Mode;
        _encoding = settings.Encoding;
        _maxFileSizeMb = settings.MaxFileSizeMb;

        Modes =
        [
            new SettingChoice(FileIntakeNodeSettings.AutoDetect, "Auto-detect", true),
            new SettingChoice(FileIntakeNodeSettings.MarkersMode, "Markers", true)
        ];
        Encodings = IntakeEncodings.Names.Select(n => new SettingChoice(n, NodeFactory.IntakeEncodingLabel(n), true)).ToList();

        ScanCommand = new RelayCommand(_ => ScanAgain(), _ => !IsScanning && !string.IsNullOrWhiteSpace(FilePath));
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsScanning);
        BrowseCommand = new RelayCommand(_ => Browse(), _ => !IsScanning && _pickFile is not null);
        SaveCommand = new RelayCommand(_ => Save(), _ => HasUnsavedChanges);
        CloseCommand = new RelayCommand(_ => RequestClose());
        CreateEditCommands();
        EscapeCommand = new RelayCommand(_ =>
        {
            if (IsScanning)
            {
                Cancel();
            }
            else
            {
                RequestClose();
            }
        });
    }

    public IReadOnlyList<SettingChoice> Modes { get; }

    public IReadOnlyList<SettingChoice> Encodings { get; }

    public RelayCommand ScanCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand BrowseCommand { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand CloseCommand { get; }

    /// <summary>Esc: cancels a running scan, else closes (asking about unsaved settings).</summary>
    public RelayCommand EscapeCommand { get; }

    /// <summary>The last scan started by <see cref="ScanCommand"/> (for tests and the window).</summary>
    public Task? ScanTask { get; private set; }

    /// <summary>The last preview read (for tests).</summary>
    public Task? PreviewTask { get; private set; }

    /// <summary>The window closes when this is raised. A close that was confirmed does not ask again.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>A change that would discard unsaved layout edits asks first; on No the old path stays.</summary>
    public string FilePath
    {
        get => _filePath;
        set
        {
            value ??= string.Empty;
            if (value == _filePath)
            {
                return;
            }

            if (!ConfirmDiscard(DiscardReasonFile))
            {
                KeepShownValue(nameof(FilePath));
                return;
            }

            SetProperty(ref _filePath, value);
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(FileToolTip));
            SettingChanged();
        }
    }

    /// <summary>The file name only; the full path is in <see cref="FileToolTip"/>.</summary>
    public string FileName => string.IsNullOrWhiteSpace(FilePath) ? NodeViewModel.NoFileChosenText : Path.GetFileName(FilePath.Trim());

    public string? FileToolTip => string.IsNullOrWhiteSpace(FilePath) ? null : FilePath;

    /// <summary>A change that would discard unsaved layout edits asks first; on No the old value stays.</summary>
    public string Mode
    {
        get => _mode;
        set
        {
            value ??= FileIntakeNodeSettings.AutoDetect;
            if (value == _mode)
            {
                return;
            }

            if (!ConfirmDiscard(DiscardReasonMode))
            {
                KeepShownValue(nameof(Mode));
                return;
            }

            SetProperty(ref _mode, value);
            SettingChanged();
        }
    }

    /// <summary>A change that would discard unsaved layout edits asks first; on No the old value stays.</summary>
    public string Encoding
    {
        get => _encoding;
        set
        {
            value ??= IntakeEncodings.Auto;
            if (value == _encoding)
            {
                return;
            }

            if (!ConfirmDiscard(DiscardReasonEncoding))
            {
                KeepShownValue(nameof(Encoding));
                return;
            }

            SetProperty(ref _encoding, value);
            SettingChanged();
        }
    }

    public int MaxFileSizeMb
    {
        get => _maxFileSizeMb;
        set
        {
            if (SetProperty(ref _maxFileSizeMb, Math.Max(1, value)))
            {
                SettingChanged();
            }
        }
    }

    /// <summary>The settings as the dialog shows them now.</summary>
    public FileIntakeNodeSettings CurrentSettings => new()
    {
        Path = FilePath.Trim(),
        Mode = Mode,
        Encoding = Encoding,
        MaxFileSizeMb = MaxFileSizeMb
    };

    /// <summary>The settings differ from the node's, or the layout differs from the one last saved (or opened).</summary>
    public bool HasUnsavedChanges => CurrentSettings != _savedSettings || _layoutDirty;

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                BrowseCommand.RaiseCanExecuteChanged();
                RaiseEditStates();
            }
        }
    }

    /// <summary>True half a second after a scan starts, until it ends (the Cancel button shows then).</summary>
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

    /// <summary>A plain sentence when the scan could not run or was cancelled; null otherwise.</summary>
    public string? Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    /// <summary>Set when Auto guessed the encoding: what was chosen, why, and how to choose another.</summary>
    public string? EncodingNotice
    {
        get => _encodingNotice;
        private set => SetProperty(ref _encodingNotice, value);
    }

    /// <summary>For example "27 containers, 0 errors, 9 warnings". Null until a scan finished.</summary>
    public string? TotalsText
    {
        get => _totalsText;
        private set => SetProperty(ref _totalsText, value);
    }

    /// <summary>For the node on the canvas, for example "27 containers, 1 warning". Null until a scan finished.</summary>
    public string? Summary { get; private set; }

    /// <summary>The settings of the last finished scan, or null.</summary>
    public FileIntakeNodeSettings? ScannedSettings { get; private set; }

    /// <summary>The rows of the list. A large change replaces the whole collection (one reset instead of thousands of events).</summary>
    public ObservableCollection<IntakeContainerRow> Containers
    {
        get => _containers;
        private set => SetProperty(ref _containers, value);
    }

    public IntakeContainerRow? SelectedContainer
    {
        get => _selectedContainer;
        set
        {
            if (SetProperty(ref _selectedContainer, value))
            {
                ShowSelected();
            }
        }
    }

    public ObservableCollection<IntakeDetailRow> Details { get; } = new();

    /// <summary>The reasons the format was chosen, one sentence each.</summary>
    public ObservableCollection<string> Reasons { get; } = new();

    /// <summary>The checks of the selected container, for example "Warning, line 97: ...".</summary>
    public ObservableCollection<string> IssueLines { get; } = new();

    /// <summary>The first lines of the selected container, with FILE line numbers.</summary>
    public ObservableCollection<PreviewLine> PreviewLines { get; } = new();

    /// <summary>"More lines not shown", "Long lines shortened", or the preview's error. Null when there is nothing to say.</summary>
    public string? PreviewNote
    {
        get => _previewNote;
        private set => SetProperty(ref _previewNote, value);
    }

    /// <summary>The preview as one read-only text: each line starts with its file line number.</summary>
    public string PreviewText
    {
        get => _previewText;
        private set => SetProperty(ref _previewText, value);
    }

    /// <summary>Starts a scan with the settings shown. A running scan is never doubled.</summary>
    public async Task ScanAsync()
    {
        if (IsScanning || string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        ClearResult();
        var version = ++_scanVersion;
        var settings = CurrentSettings;
        var cancel = new CancellationTokenSource();
        _scanCancel = cancel;
        IsScanning = true;
        ProgressPercent = 0;
        ProgressText = "Starting";
        StartCancelDelay(version);

        var progress = new Progress<IntakeScanProgress>(p =>
        {
            // A report can arrive after the scan ended or after a newer scan started.
            if (version == _scanVersion && IsScanning)
            {
                ProgressPercent = p.Percent;
                ProgressText = p.Text;
            }
        });

        IntakeItem? item = null;
        IntakeScanResult result;
        LayoutStart? start = null;
        try
        {
            item = _createItem(settings.Path);
            var intakeId = Guid.NewGuid().ToString("D");
            result = await new IntakeScanService(settings.ToIntakeSettings(), _lineage).ScanAsync(item, progress, cancel.Token, intakeId);
            if (result is { Error: null, Cancelled: false, Intake: { File: not null, Error: null, Cancelled: false } } && version == _scanVersion)
            {
                // A saved layout is checked against the whole file, so the session is made off the UI thread.
                var scannedItem = item;
                start = await Task.Run(() => StartSession(scannedItem, result, settings, intakeId));
            }
        }
        catch (Exception ex)
        {
            TryLog(ex);
            result = new IntakeScanResult(null, null, null, false, 0, false,
                "The file could not be scanned. Check that it exists and is not open in another program, then press Scan again.");
        }
        finally
        {
            if (ReferenceEquals(_scanCancel, cancel))
            {
                _scanCancel = null;
            }

            cancel.Dispose();
        }

        if (version != _scanVersion)
        {
            // A setting changed during the scan: its result is not shown.
            IsScanning = false;
            IsCancelVisible = false;
            return;
        }

        IsScanning = false;
        IsCancelVisible = false;
        Apply(result, item, settings, start);
    }

    public void Cancel() => _scanCancel?.Cancel();

    /// <summary>Writes the settings to the node. Only a change is written.</summary>
    public void Save()
    {
        var settings = CurrentSettings;
        if (settings != _savedSettings)
        {
            _save?.Invoke(settings);
            _savedSettings = settings;
        }

        if (_layoutDirty)
        {
            SaveLayout();
        }

        RaiseUnsavedChanged();
    }

    /// <summary>
    /// Asks about unsaved settings once and closes when the answer allows it. A running scan is
    /// cancelled first. True when the window may close.
    /// </summary>
    public bool ConfirmClose()
    {
        if (_closeConfirmed)
        {
            return true;
        }

        if (HasUnsavedChanges && _askUnsaved is not null)
        {
            switch (_askUnsaved())
            {
                case UnsavedChangesChoice.Save:
                    Save();
                    break;
                case UnsavedChangesChoice.DontSave:
                    break;
                default:
                    return false;
            }
        }

        Cancel();
        _closeConfirmed = true;
        return true;
    }

    private void RequestClose()
    {
        if (ConfirmClose())
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Browse()
    {
        var picked = _pickFile?.Invoke(string.IsNullOrWhiteSpace(FilePath) ? null : FilePath);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            FilePath = picked;
        }
    }

    private void SettingChanged()
    {
        // The old result belongs to the old settings, and a running scan is stopped.
        if (IsScanning)
        {
            _scanVersion++;
            Cancel();
        }

        ClearResult();
        ProgressText = string.Empty;
        ProgressPercent = 0;
        ScanCommand.RaiseCanExecuteChanged();
        RaiseUnsavedChanged();
    }

    private void RaiseUnsavedChanged()
    {
        _layoutDirty = _session is not null && !SameLayout(_session.Containers, _savedLayout);
        OnPropertyChanged(nameof(HasUnsavedChanges));
        SaveCommand.RaiseCanExecuteChanged();
    }

    private void ClearResult()
    {
        _previewVersion++;
        SelectedContainer = null;
        Containers = new ObservableCollection<IntakeContainerRow>();
        ClearSession();
        Details.Clear();
        Reasons.Clear();
        IssueLines.Clear();
        PreviewLines.Clear();
        PreviewRows.Clear();
        PreviewNote = null;
        PreviewText = string.Empty;
        Message = null;
        EncodingNotice = null;
        TotalsText = null;
        Summary = null;
        ScannedSettings = null;
        _scannedItem = null;
        _scannedEncoding = null;
    }

    private void Apply(IntakeScanResult result, IntakeItem? item, FileIntakeNodeSettings settings, LayoutStart? start)
    {
        if (result.Cancelled)
        {
            ProgressText = ScanCancelledText;
            Message = ScanCancelledText;
            return;
        }

        if (result.Error is not null || result.Intake is null || result.Validation is null)
        {
            ProgressText = ScanFailedText;
            Message = result.Error ?? "The scan did not finish. Press Scan again.";
            return;
        }

        _errors = result.Validation.Errors;
        _warnings = result.Validation.Warnings;
        if (start is { } layout)
        {
            ShowSession(layout);
        }
        else
        {
            var validations = result.Validation.Containers.ToDictionary(v => v.Order);
            Containers = new ObservableCollection<IntakeContainerRow>(
                result.Intake.Containers.Select(info => ToRow(info, validations.GetValueOrDefault(info.Order))));
            UpdateTotals();
        }

        var count = IntakeText.Count(Containers.Count, "container");

        if (result.EncodingGuessed)
        {
            EncodingNotice = $"{result.Intake.EncodingReason ?? IntakeEncodings.GuessReason} "
                + "If the text looks wrong, choose another encoding and press Scan.";
        }

        var fileWarnings = result.Intake.Warnings;
        if (fileWarnings.Count > 0)
        {
            Message = string.Join(Environment.NewLine, fileWarnings.Select(w => w.Line is { } line ? $"Warning, line {line}: {w.Message}" : $"Warning: {w.Message}"));
        }

        ProgressPercent = 100;
        ProgressText = $"Finished: {count} found";
        ScannedSettings = settings;
        _scannedItem = item;
        _scannedEncoding = result.EncodingUsed ?? IntakeEncodings.Utf8;
    }

    private IntakeContainerRow ToRow(ContainerInfo info, ContainerValidation? validation)
    {
        var status = (ContainerStatus)Math.Max((int)info.Status, (int)(validation?.Status ?? ContainerStatus.Ok));
        var part = ContainerDisplay.Format(info, _labels);
        if (!string.IsNullOrWhiteSpace(info.Name))
        {
            part += ": " + info.Name;
        }

        var issues = validation?.Issues ?? [];
        var summary = issues.Count switch
        {
            0 when !string.IsNullOrWhiteSpace(info.Message) => info.Message,
            0 => string.Empty,
            1 => issues[0].Message,
            _ => $"{IntakeText.Count(validation!.TotalIssues, "issue")}. First: {issues[0].Message}"
        };

        return new IntakeContainerRow(info.Order, part, LinesText(info), info.Confidence.ToString(), status, StatusText(status), summary, info, validation);
    }

    /// <summary>"21 to 36", "21" for one line, "empty" for a container without lines.</summary>
    public static string LinesText(ContainerInfo info) => (info.ContentFirstLine, info.ContentLastLine) switch
    {
        ({ } first, { } last) when first == last => first.ToString(CultureInfo.InvariantCulture),
        ({ } first, { } last) => $"{first.ToString(CultureInfo.InvariantCulture)} to {last.ToString(CultureInfo.InvariantCulture)}",
        _ => "empty"
    };

    /// <summary>A word with a symbol, so the status does not depend on colour.</summary>
    public static string StatusText(ContainerStatus status) => status switch
    {
        ContainerStatus.Error => "X Error",
        ContainerStatus.Warning => "! Warning",
        _ => "OK"
    };

    private void ShowSelected()
    {
        Details.Clear();
        Reasons.Clear();
        IssueLines.Clear();
        PreviewLines.Clear();
        PreviewRows.Clear();
        PreviewNote = null;
        PreviewText = string.Empty;
        var version = ++_previewVersion;
        FillEditFields();

        if (SelectedContainer is not { } row)
        {
            return;
        }

        var info = row.Info;
        Details.Add(new IntakeDetailRow("Format", ContainerDisplay.Format(info, _labels)));
        Details.Add(new IntakeDetailRow("Lines", row.Lines));
        Details.Add(new IntakeDetailRow("Confidence", row.Confidence));
        if (info.Delimiter is { } delimiter)
        {
            Details.Add(new IntakeDetailRow("Delimiter", DelimiterName(delimiter)));
        }

        if (info.HeaderGuess is { } header)
        {
            Details.Add(new IntakeDetailRow("Header", header switch
            {
                HeaderGuess.Yes => "Yes (guessed)",
                HeaderGuess.No => "No (guessed)",
                _ => "Not known"
            }));
        }

        if (info.SkipLines > 0 || info.Delimiter is not null)
        {
            Details.Add(new IntakeDetailRow("Skip lines", info.SkipLines.ToString(CultureInfo.InvariantCulture)));
        }

        if (info.KeyValueForm is { } form)
        {
            Details.Add(new IntakeDetailRow("Key-value form", KeyValueFormName(form)));
        }

        if (!string.IsNullOrWhiteSpace(info.Notes))
        {
            Details.Add(new IntakeDetailRow("Notes", info.Notes));
        }

        if (info.ConfirmedByUser)
        {
            Details.Add(new IntakeDetailRow("Layout", SetByYouText));
        }

        foreach (var reason in info.Reasons ?? [])
        {
            Reasons.Add(Sentence(reason));
        }

        if (!string.IsNullOrWhiteSpace(info.Message))
        {
            IssueLines.Add($"{info.Status}: {Sentence(info.Message)}");
        }

        foreach (var issue in row.Validation?.Issues ?? [])
        {
            IssueLines.Add(IssueLine(issue));
        }

        if (row.Validation is { MoreNotShown: true } validation)
        {
            IssueLines.Add($"{IntakeText.Count(validation.TotalIssues - validation.Issues.Count, "more issue")} not shown.");
        }

        if (row.Validation is { Checked: false })
        {
            IssueLines.Add("This format has no checks yet.");
        }

        if (_scannedItem is { } item && _scannedEncoding is { } encoding)
        {
            PreviewTask = LoadPreviewAsync(item, info, encoding, version);
        }
    }

    private async Task LoadPreviewAsync(IntakeItem item, ContainerInfo info, string encoding, int version)
    {
        IntakePreviewResult preview;
        try
        {
            preview = await Task.Run(() => IntakePreview.Read(item, info, encoding, PreviewMaxLines, PreviewMaxChars, CancellationToken.None));
        }
        catch (Exception ex)
        {
            TryLog(ex);
            preview = new IntakePreviewResult([], false, false, "The preview could not be read. Check that the file is still there, then scan again.");
        }

        // The await came back to the calling (UI) thread; a newer selection wins.
        if (version != _previewVersion)
        {
            return;
        }

        var width = preview.Lines.Count == 0 ? 1 : preview.Lines[^1].LineNumber.ToString(CultureInfo.InvariantCulture).Length;
        foreach (var line in preview.Lines)
        {
            PreviewLines.Add(line);
            PreviewRows.Add(new IntakePreviewRow(line.LineNumber, line.LineNumber.ToString(CultureInfo.InvariantCulture).PadLeft(width) + "  " + line.Text));
        }

        PreviewText = string.Join(Environment.NewLine, PreviewRows.Select(r => r.Text));
        SyncPreviewSelection();

        var notes = new List<string>();
        if (preview.Error is not null)
        {
            notes.Add(preview.Error);
        }

        if (preview.Truncated)
        {
            notes.Add(PreviewShortText);
        }

        if (preview.LinesCut)
        {
            notes.Add($"Long lines are shortened to {PreviewMaxChars} characters.");
        }

        PreviewNote = notes.Count == 0 ? null : string.Join(" ", notes);
    }

    /// <summary>"Warning, line 97: ..." or "Warning: ..." when the issue has no line.</summary>
    public static string IssueLine(ValidationIssue issue)
        => issue.Line is { } line
            ? $"{issue.Severity}, line {line.ToString(CultureInfo.InvariantCulture)}: {issue.Message}"
            : $"{issue.Severity}: {issue.Message}";

    public static string DelimiterName(string delimiter) => delimiter switch
    {
        "," => "Comma",
        ";" => "Semicolon",
        "\t" => "Tab",
        "|" => "Pipe",
        " " => "Space",
        _ => $"\"{delimiter}\""
    };

    private static string KeyValueFormName(KeyValueForm form) => form switch
    {
        KeyValueForm.Colon => "Name: value",
        KeyValueForm.EqualsSign => "Name = value",
        KeyValueForm.Quoted => "\"Name\" value (quoted)",
        KeyValueForm.Tab => "Name, tab, value",
        KeyValueForm.Pairs => "Pairs on one line",
        KeyValueForm.Delimited => "Name and value in delimited columns",
        _ => form.ToString()
    };

    /// <summary>Starts with a capital letter and ends with a full stop.</summary>
    public static string Sentence(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        trimmed = char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        return trimmed[^1] is '.' or '!' or '?' ? trimmed : trimmed + ".";
    }

    private void StartCancelDelay(int version)
    {
        _ = Task.Delay(CancelDelayMs).ContinueWith(_ => Post(() =>
        {
            if (version == _scanVersion && IsScanning)
            {
                IsCancelVisible = true;
            }
        }), TaskScheduler.Default);
    }

    private void Post(Action action)
    {
        if (_context is null)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }

    private static void TryLog(Exception ex)
    {
        try
        {
            ErrorLog.Write("Intake Studio", ex);
        }
        catch (IOException)
        {
            // The log is a help, never a reason to fail.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
