using EtlDesigner.Engine;

namespace EtlDesigner.Spike;

/// <summary>
/// ETLBox pipeline harness for testing.
/// Delegates execution to Engine.PipelineRunner.
/// </summary>
public class ETLBoxSpike
{
    /// <summary>
    /// Run the pipeline: CSV → filter by country → JSON → project fields → JSON
    /// </summary>
    public static PipelineResult RunPipeline(string csvPath, string? country, string delimiter)
    {
        var settings = new PipelineSettings(
            Delimiter: delimiter,
            LocationColumn: "location",
            Separator: "|",
            ParseFields: new[] { "first_name", "last_name", "email" });

        var core = PipelineCore.Run(settings, csvPath, country ?? string.Empty);
        return new PipelineResult(core.FilteredJson, core.ParsedJson, core.SkippedMalformedCount, core.ErrorMessage);
    }
}
