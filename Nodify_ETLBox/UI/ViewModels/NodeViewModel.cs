using System.Collections.ObjectModel;
using System.Windows;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
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
    ParseValue
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

    public NodeViewModel(Guid id, string title, NodeType nodeType, Func<CsvFileSettings, bool>? loadPreviewAction = null)
    {
        Id = id;
        Title = title ?? throw new ArgumentNullException(nameof(title));
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

    public Guid Id { get; }

    public string Title { get; }

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

    public RelayCommand LoadPreviewCommand { get; }

    public ObservableCollection<ConnectorViewModel> Inputs { get; }

    public ObservableCollection<ConnectorViewModel> Outputs { get; }

    public ObservableCollection<SettingViewModel> Settings { get; }

    public CsvColumnSelection ColumnSelection { get; } = new();

    /// <summary>
    /// CsvFile only: the column names of the file as last read with the current settings, or
    /// null when not known (never read, or a setting changed since).
    /// </summary>
    public IReadOnlyList<string>? KnownColumns { get; set; }

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
