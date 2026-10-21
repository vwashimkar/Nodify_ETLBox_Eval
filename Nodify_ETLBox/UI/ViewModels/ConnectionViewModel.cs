using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

public class ConnectionViewModel : ObservableObject
{
    public ConnectionViewModel(ConnectorViewModel source, ConnectorViewModel target)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public ConnectorViewModel Source { get; }

    public ConnectorViewModel Target { get; }
}
