using System.Windows;
using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

public class ConnectorViewModel : ObservableObject
{
    private Point _anchor;
    private bool _isConnected;
    private readonly NodeViewModel _parentNode;

    public ConnectorViewModel(string name, PortKind kind, bool isInput, NodeViewModel parentNode)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Kind = kind;
        IsInput = isInput;
        _parentNode = parentNode ?? throw new ArgumentNullException(nameof(parentNode));
        _anchor = new Point();
        _isConnected = false;
    }

    public string Name { get; }

    public PortKind Kind { get; }

    public bool IsInput { get; }

    public NodeViewModel ParentNode => _parentNode;

    public Point Anchor
    {
        get => _anchor;
        set => SetProperty(ref _anchor, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set => SetProperty(ref _isConnected, value);
    }
}
