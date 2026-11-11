namespace EtlDesigner.UI.ViewModels;

public enum PortKind
{
    Rows,
    Json,
    Records,

    /// <summary>Rows produced by ParseValue (one per component). Not accepted by the legacy nodes.</summary>
    ParsedRows,

    /// <summary>The parts of a File intake file. Only a CSV part node takes them (G6c).</summary>
    Parts
}
