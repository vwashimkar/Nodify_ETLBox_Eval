using System.Collections.ObjectModel;
using System.Windows;
using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

public enum NodeType
{
    CsvSource,
    FilterByCountry,
    ToJson,
    ParseJson,
    ResultJson
}

public enum NodeState
{
    Idle,
    Running,
    Completed,
    Failed
}

public class NodeViewModel : ObservableObject
{
    private Point _location;
    private Point _anchor;
    private NodeState _state;
    private long _rowCount;
    private string? _errorMessage;
    private bool _isSelected;
    private System.Windows.Size _size;

    public NodeViewModel(Guid id, string title, NodeType nodeType)
    {
        Id = id;
        Title = title ?? throw new ArgumentNullException(nameof(title));
        NodeType = nodeType;
        _location = new Point();
        _anchor = new Point();
        _size = new System.Windows.Size();
        _state = NodeState.Idle;
        _rowCount = 0;
        _errorMessage = null;
        _isSelected = false;

        Inputs = new ObservableCollection<ConnectorViewModel>();
        Outputs = new ObservableCollection<ConnectorViewModel>();
        Settings = new ObservableCollection<SettingViewModel>();
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

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ObservableCollection<ConnectorViewModel> Inputs { get; }

    public ObservableCollection<ConnectorViewModel> Outputs { get; }

    public ObservableCollection<SettingViewModel> Settings { get; }
}
