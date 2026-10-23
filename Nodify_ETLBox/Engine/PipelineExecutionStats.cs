namespace EtlDesigner.Engine;

/// <summary>
/// Runtime counters captured during pipeline execution.
/// </summary>
public record PipelineExecutionStats(
    int RowsRead,
    int RowsMatched,
    int RowsNotMatched,
    int RowsMalformedSkipped,
    int ParsedRecordCount);
