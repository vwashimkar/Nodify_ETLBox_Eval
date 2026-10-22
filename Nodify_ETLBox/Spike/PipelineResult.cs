namespace EtlDesigner.Spike;

/// <summary>
/// Result of a pipeline execution.
/// </summary>
public record PipelineResult(
    string FilteredJson,
    string ParsedJson,
    int SkippedMalformedCount,
    string? ErrorMessage);
