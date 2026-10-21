using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

/// <summary>
/// Represents a connection being drawn from a source connector.
/// PreviewTarget is of type object (Connector or ItemContainer per Nodify API).
/// Source and Target are updated by Nodify as the user drags.
/// </summary>
public class PendingConnectionViewModel : ObservableObject
{
    private ConnectorViewModel? _source;
    private ConnectorViewModel? _target;
    private object? _previewTarget;
    private bool _isValid;

    public PendingConnectionViewModel(ConnectorViewModel source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _target = null;
        _previewTarget = null;
        _isValid = false;
    }

    public ConnectorViewModel? Source
    {
        get => _source;
        set => SetProperty(ref _source, value);
    }

    public ConnectorViewModel? Target
    {
        get => _target;
        set
        {
            if (SetProperty(ref _target, value))
            {
                // When target changes, update IsValid
                UpdateIsValid();
            }
        }
    }

    /// <summary>
    /// The Nodify Connector or ItemContainer being previewed as the potential target.
    /// Type is object per Nodify.PendingConnection.PreviewTarget API.
    /// </summary>
    public object? PreviewTarget
    {
        get => _previewTarget;
        set
        {
            if (SetProperty(ref _previewTarget, value))
            {
                // When PreviewTarget changes, compute IsValid
                UpdateIsValid();
            }
        }
    }

    public bool IsValid
    {
        get => _isValid;
        set => SetProperty(ref _isValid, value);
    }

    private void UpdateIsValid()
    {
        IsValid = _source != null && _target != null;
    }
}
