using System.Collections.ObjectModel;
using System.Windows;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Intake;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.Engine.Project;
using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

public enum NodeType
{
    CsvSource,
    CsvFile,
    FilterByCountry,
    ToJson,
    ParseJson,
    ResultJson,
    ParseValue,
    DotNetObject,

    /// <summary>Reads a file and finds its parts (containers). Its "Parts" output feeds a CSV part node (G6c); Run ignores it.</summary>
    FileIntake,

    /// <summary>One csv or tsv part of the File intake node's file, chosen by a selector (G6c). Feeds ParseValue like CsvFile.</summary>
    CsvPart,

    /// <summary>A placeholder for a node in a project file that this version cannot use. Never in the palette.</summary>
    Unknown
}

public enum NodeState
{
    Idle,
    Running,
    Succeeded,
    Failed,
    NotRun
}

public class NodeViewModel : ObservableObject
{
    private readonly Func<CsvFileSettings, bool>? _loadPreviewAction;

    private Point _location;
    private Point _anchor;
    private NodeState _state;
    private long _rowCount;
    private string? _errorMessage;
    private string? _errorDetail;
    private bool _isSelected;
    private System.Windows.Size _size;
    private string _configurationStatus;
    private ParseValueSettings? _parseValueSettings;
    private string _title;
    private bool _isRefreshing;
    private string? _intakeSummary;
    private string? _intakePlanJson;
    private bool _intakePlanReadable;
    private IReadOnlyList<string>? _knownColumns;
    private string? _csvPartSelectorJson;
    private CsvPartSelector? _csvPartSelector;
    private string? _csvPartSelectorError;
    private string? _csvPartStatus;

    public NodeViewModel(Guid id, string title, NodeType nodeType, Func<CsvFileSettings, bool>? loadPreviewAction = null)
    {
        Id = id;
        _title = title ?? throw new ArgumentNullException(nameof(title));
        NodeType = nodeType;
        _loadPreviewAction = loadPreviewAction;
        _location = new Point();
        _anchor = new Point();
        _size = new System.Windows.Size();
        _state = NodeState.Idle;
        _rowCount = 0;
        _errorMessage = null;
        _errorDetail = null;
        _isSelected = false;
        _configurationStatus = "Configured";

        Inputs = new ObservableCollection<ConnectorViewModel>();
        Outputs = new ObservableCollection<ConnectorViewModel>();
        Settings = new ObservableCollection<SettingViewModel>();

        LoadPreviewCommand = new RelayCommand(_ => LoadPreview(), _ => CanLoadPreview());
    }

    public Guid Id { get; internal set; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value ?? string.Empty);
    }

    public NodeType NodeType { get; }

    public Point Location
    {
        get => _location;
        set => SetProperty(ref _location, value);
    }

    public Point Anchor
    {
        get => _anchor;
        set => SetProperty(ref _anchor, value);
    }

    public System.Windows.Size Size
    {
        get => _size;
        set => SetProperty(ref _size, value);
    }

    public NodeState State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }

    public long RowCount
    {
        get => _rowCount;
        set => SetProperty(ref _rowCount, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public string? ErrorDetail
    {
        get => _errorDetail;
        set => SetProperty(ref _errorDetail, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string ConfigurationStatus
    {
        get => _configurationStatus;
        set
        {
            if (SetProperty(ref _configurationStatus, string.IsNullOrWhiteSpace(value) ? "Not configured" : value))
            {
                LoadPreviewCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True while the node reads from the file system in the background; shows a spinner.</summary>
    public bool IsRefreshing
    {
        get => _isRefreshing;
        set => SetProperty(ref _isRefreshing, value);
    }

    /// <summary>A short line under the title (File intake). Null for nodes without one.</summary>
    public string? Subtitle { get; init; }

    /// <summary>
    /// File intake only: the totals of the last scan in this session, for example "27 containers,
    /// 1 warning". Never saved; a changed setting clears it.
    /// </summary>
    public string? IntakeSummary
    {
        get => _intakeSummary;
        set
        {
            if (SetProperty(ref _intakeSummary, value))
            {
                OnPropertyChanged(nameof(CanvasNote));
            }
        }
    }

    public const string NoFileChosenText = "No file chosen";

    public const string LayoutEditedText = "Layout: edited";

    /// <summary>
    /// File intake only: the saved layout (an <see cref="IntakePlan"/> as JSON), or null for the
    /// automatic layout. Saved in the project; a text that cannot be read is kept as it is.
    /// </summary>
    public string? IntakePlanJson
    {
        get => _intakePlanJson;
        set
        {
            if (SetProperty(ref _intakePlanJson, value))
            {
                // Read once here: a large layout is not parsed again for every canvas refresh.
                _intakePlanReadable = value is not null && EtlDesigner.Engine.Intake.IntakePlanJson.Deserialize(value, out _) is not null;
                OnPropertyChanged(nameof(IntakeLayoutText));
            }
        }
    }

    /// <summary>File intake only: "Layout: edited" when a readable saved layout exists, else null (the canvas hides it).</summary>
    public string? IntakeLayoutText => NodeType == NodeType.FileIntake && _intakePlanReadable ? LayoutEditedText : null;

    /// <summary>File intake only: the file name (or "No file chosen") and the scan summary, shown on the canvas.</summary>
    public string? CanvasNote
    {
        get
        {
            if (NodeType != NodeType.FileIntake)
            {
                return null;
            }

            var path = GetFileIntakeSettings().Path;
            var name = string.IsNullOrWhiteSpace(path) ? NoFileChosenText : System.IO.Path.GetFileName(path.Trim());
            return IntakeSummary is null ? name : name + Environment.NewLine + IntakeSummary;
        }
    }

    /// <summary>File intake only: the file name, or "No file chosen" when the path is empty. Null for the other nodes.</summary>
    public string? IntakeFileText
    {
        get
        {
            if (NodeType != NodeType.FileIntake)
            {
                return null;
            }

            var path = GetFileIntakeSettings().Path;
            return string.IsNullOrWhiteSpace(path) ? NoFileChosenText : System.IO.Path.GetFileName(path.Trim());
        }
    }

    /// <summary>File intake only: the full path, for the tooltip of <see cref="CanvasNote"/>.</summary>
    public string? CanvasNoteToolTip
    {
        get
        {
            var path = NodeType == NodeType.FileIntake ? GetFileIntakeSettings().Path : null;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
    }

    /// <summary>Tells the canvas that a File intake setting changed.</summary>
    internal void NotifyCanvasNoteChanged()
    {
        OnPropertyChanged(nameof(CanvasNote));
        OnPropertyChanged(nameof(IntakeFileText));
        OnPropertyChanged(nameof(CanvasNoteToolTip));
    }

    /// <summary>The node as it came from the project file, kept for what this version does not read.</summary>
    internal ProjectNode? LoadedFrom { get; set; }

    /// <summary>Unknown nodes only: the Issues line.</summary>
    internal string? UnknownNodeMessage { get; set; }

    /// <summary>True while saved values are written to the setting rows, so that nothing is suggested from the file.</summary>
    internal bool IsRestoring { get; set; }

    public RelayCommand LoadPreviewCommand { get; }

    public ObservableCollection<ConnectorViewModel> Inputs { get; }

    public ObservableCollection<ConnectorViewModel> Outputs { get; }

    public ObservableCollection<SettingViewModel> Settings { get; }

    public CsvColumnSelection ColumnSelection { get; } = new();

    /// <summary><see cref="ColumnSelection"/> is changed in place; this tells the listeners that it was.</summary>
    public void NotifyColumnSelectionChanged() => OnPropertyChanged(nameof(ColumnSelection));

    /// <summary>
    /// CsvFile and CSV part: the column names as last read with the current settings, or null when not
    /// known (never read, or a setting changed since). A CSV part's columns come from the run's reader.
    /// </summary>
    public IReadOnlyList<string>? KnownColumns
    {
        get => _knownColumns;
        set
        {
            if (SetProperty(ref _knownColumns, value))
            {
                OnPropertyChanged(nameof(CsvPartText));
            }
        }
    }

    public const string NoPartChosenText = "No part chosen";

    /// <summary>
    /// CSV part only: the selector as <c>CsvPartSelectorJson</c> text, saved in the project as it is.
    /// Null while no part is chosen. A text that cannot be read is kept and gives <see cref="CsvPartSelectorError"/>.
    /// Setting it forgets the columns and the location of the old selector.
    /// </summary>
    public string? CsvPartSelectorJson
    {
        get => _csvPartSelectorJson;
        set
        {
            if (!SetProperty(ref _csvPartSelectorJson, value))
            {
                return;
            }

            string? error = null;
            _csvPartSelector = value is null ? null : EtlDesigner.Engine.Intake.CsvPartSelectorJson.Deserialize(value, out error);
            _csvPartSelectorError = error;
            CsvPartRead = null;
            KnownColumns = null;
            OnPropertyChanged(nameof(CsvPartSelector));
            OnPropertyChanged(nameof(CsvPartSelectorError));
            OnPropertyChanged(nameof(CsvPartText));
        }
    }

    /// <summary>CSV part only: the readable selector, or null (none chosen, or unreadable).</summary>
    public CsvPartSelector? CsvPartSelector => _csvPartSelector;

    /// <summary>CSV part only: why the saved selector cannot be used (<c>CsvPartSelectorJson</c>'s sentence), or null.</summary>
    public string? CsvPartSelectorError => _csvPartSelectorError;

    /// <summary>
    /// CSV part only: the last column read in this session (location and columns, or the locator's sentence),
    /// for the file and selector it was made with. Never saved. See <see cref="EditorViewModel.CurrentCsvPartRead"/>.
    /// </summary>
    public CsvPartRead? CsvPartRead { get; set; }

    /// <summary>CSV part only: one status line (ready, or the first problem, for example the locator's sentence). Never saved.</summary>
    public string? CsvPartStatus
    {
        get => _csvPartStatus;
        set => SetProperty(ref _csvPartStatus, value);
    }

    /// <summary>
    /// CSV part only, read-only: "Part N of the file", the template name and "Comma table, N columns"
    /// (the columns once read). "No part chosen" without a readable selector. Null for other nodes.
    /// </summary>
    public string? CsvPartText
    {
        get
        {
            if (NodeType != NodeType.CsvPart)
            {
                return null;
            }

            if (_csvPartSelector is not { } selector)
            {
                return NoPartChosenText;
            }

            var word = CsvPartLocator.DelimiterWord(selector.Expected.Delimiter);
            var table = char.ToUpperInvariant(word[0]) + word[1..] + " table";
            var columns = KnownColumns is { } known
                ? $"{known.Count} column{(known.Count == 1 ? "" : "s")}"
                : "columns not read yet";
            return $"Part {selector.PartIndex + 1} of the file" + Environment.NewLine
                + $"Template: {selector.Template.Name}" + Environment.NewLine
                + $"{table}, {columns}";
        }
    }

    /// <summary>ParseValue only: the node's settings. Null for other node types.</summary>
    public ParseValueSettings? ParseValueSettings
    {
        get => _parseValueSettings;
        set
        {
            if (SetProperty(ref _parseValueSettings, value))
            {
                OnPropertyChanged(nameof(ParseValueSettingsText));
                ParseValueSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The ParseValue settings as JSON, for the read-only placeholder dialog.</summary>
    public string ParseValueSettingsText
        => _parseValueSettings is null ? string.Empty : SettingsJson.Serialize(_parseValueSettings);

    public event EventHandler? ParseValueSettingsChanged;

    /// <summary>Opens the node's dialog (ParseValue). Null for nodes without one.</summary>
    public RelayCommand? ConfigureCommand { get; init; }

    /// <summary>The double-click on the canvas: opens Intake Studio for a File intake node, nothing for the others.</summary>
    public RelayCommand? DoubleClickCommand => NodeType == NodeType.FileIntake ? ConfigureCommand : null;

    /// <summary>DotNetObject only: the output settings from the "Output type" row (default when unreadable).</summary>
    public OutputSettings GetOutputSettings()
    {
        var value = Settings.FirstOrDefault(s => s.Key == "OutputType")?.Value;
        return Enum.TryParse<ObjectKind>(value, out var kind) ? new OutputSettings(kind) : OutputSettings.Default;
    }

    /// <summary>File intake only: the settings from the node's setting rows (defaults when unreadable).</summary>
    public FileIntakeNodeSettings GetFileIntakeSettings()
    {
        var defaults = FileIntakeNodeSettings.Default;
        string Value(string key, string fallback) => Settings.FirstOrDefault(s => s.Key == key)?.Value ?? fallback;

        return new FileIntakeNodeSettings
        {
            Path = Value("FilePath", defaults.Path),
            Mode = Value("Mode", defaults.Mode),
            Encoding = Value("Encoding", defaults.Encoding),
            MaxFileSizeMb = int.TryParse(Value("MaxFileSizeMb", ""), out var size) && size > 0 ? size : defaults.MaxFileSizeMb
        };
    }

    /// <summary>File intake only: writes settings to the setting rows. Only rows whose value differs change.</summary>
    public void SetFileIntakeSettings(FileIntakeNodeSettings settings)
    {
        void Set(string key, string value)
        {
            var row = Settings.FirstOrDefault(s => s.Key == key);
            if (row is not null && row.Value != value)
            {
                row.Value = value;
            }
        }

        Set("FilePath", settings.Path);
        Set("Mode", settings.Mode);
        Set("Encoding", settings.Encoding);
        Set("MaxFileSizeMb", settings.MaxFileSizeMb.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>CsvFile only: the reader settings from the node's setting rows.</summary>
    public CsvFileSettings GetCsvFileSettings()
    {
        var filePath = Settings.FirstOrDefault(s => s.Key == "FilePath")?.Value ?? string.Empty;
        var separator = NormalizeSeparator(Settings.FirstOrDefault(s => s.Key == "Separator")?.Value ?? ",");
        var hasHeader = Settings.FirstOrDefault(s => s.Key == "HasHeader")?.BoolValue ?? true;
        var encodingName = Settings.FirstOrDefault(s => s.Key == "EncodingName")?.Value ?? "utf-8";

        var skipLines = int.TryParse(Settings.FirstOrDefault(s => s.Key == "SkipLines")?.Value, out var parsedSkipLines) ? parsedSkipLines : 0;
        var previewRows = int.TryParse(Settings.FirstOrDefault(s => s.Key == "PreviewRows")?.Value, out var parsedPreviewRows) ? parsedPreviewRows : 5;

        return new CsvFileSettings(filePath, separator, hasHeader, skipLines, encodingName, previewRows);
    }

    private bool CanLoadPreview()
    {
        if (NodeType != NodeType.CsvFile)
        {
            return false;
        }

        var hasPath = !string.IsNullOrWhiteSpace(Settings.FirstOrDefault(s => s.Key == "FilePath")?.Value);
        return hasPath && string.Equals(ConfigurationStatus, "Configured", StringComparison.Ordinal);
    }

    private void LoadPreview()
    {
        if (_loadPreviewAction == null)
        {
            return;
        }

        var settings = GetCsvFileSettings();
        _loadPreviewAction(settings);
    }

    private static string NormalizeSeparator(string value)
    {
        if (string.Equals(value, "Comma", StringComparison.OrdinalIgnoreCase)) return ",";
        if (string.Equals(value, "Semicolon", StringComparison.OrdinalIgnoreCase)) return ";";
        if (string.Equals(value, "Tab", StringComparison.OrdinalIgnoreCase)) return "tab";
        if (string.Equals(value, "Pipe", StringComparison.OrdinalIgnoreCase)) return "|";
        return value;
    }
}
