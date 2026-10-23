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
            CsvFilePath: csvPath,
            Delimiter: delimiter,
            LocationColumn: "location",
            Separator: "|",
            Country: country,
            ParseFields: new[] { "first_name", "last_name", "email" });

        return PipelineRunner.Run(settings);
    }
}
