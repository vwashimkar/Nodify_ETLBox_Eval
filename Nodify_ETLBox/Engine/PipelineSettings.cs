namespace EtlDesigner.Engine;

/// <summary>
/// Settings used by the ETL pipeline, sourced from graph nodes.
/// </summary>
public record PipelineSettings(
    string Delimiter,
    string LocationColumn,
    string Separator,
    IReadOnlyList<string> ParseFields);
