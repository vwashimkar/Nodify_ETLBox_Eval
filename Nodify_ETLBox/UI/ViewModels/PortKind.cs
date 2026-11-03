namespace EtlDesigner.UI.ViewModels;

public enum PortKind
{
    Rows,
    Json,
    Records,

    /// <summary>Rows produced by ParseValue (one per component). Not accepted by the legacy nodes.</summary>
    ParsedRows
}
