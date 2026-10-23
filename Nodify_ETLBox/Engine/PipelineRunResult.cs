using EtlDesigner.Spike;

namespace EtlDesigner.Engine;

/// <summary>
/// Pipeline execution result including the legacy spike output and measured runtime counters.
/// </summary>
public record PipelineRunResult(
    PipelineResult Result,
    PipelineExecutionStats Stats,
    PipelineFailureNode FailureNode = PipelineFailureNode.Unknown);
