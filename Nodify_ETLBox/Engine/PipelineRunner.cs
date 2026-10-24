using EtlDesigner.Spike;

namespace EtlDesigner.Engine;

public static class PipelineRunner
{
    public static PipelineResult Run(PipelineSettings settings)
    {
        var core = PipelineCore.Run(settings, string.Empty, string.Empty);
        return new PipelineResult(core.FilteredJson, core.ParsedJson, core.SkippedMalformedCount, core.ErrorMessage);
    }

    public static PipelineRunResult RunWithStats(PipelineSettings settings, string csvPath, string country)
    {
        var core = PipelineCore.Run(settings, csvPath, country);
        var result = new PipelineResult(core.FilteredJson, core.ParsedJson, core.SkippedMalformedCount, core.ErrorMessage);
        return new PipelineRunResult(result, core.Stats, core.FailureNode);
    }

    public static PipelineRunResult RunWithStats(PipelineSettingsWithSource settings)
    {
        var coreSettings = new PipelineSettings(
            settings.Delimiter,
            settings.LocationColumn,
            settings.Separator,
            settings.ParseFields);

        return RunWithStats(coreSettings, settings.CsvFilePath, settings.Country ?? string.Empty);
    }

    public static PipelineResult Run(PipelineSettingsWithSource settings)
        => RunWithStats(settings).Result;

    public static CountryListResult ReadDistinctCountries(PipelineSettingsWithSource settings)
    {
        var coreSettings = new PipelineSettings(
            settings.Delimiter,
            settings.LocationColumn,
            settings.Separator,
            settings.ParseFields);

        return PipelineCore.ReadDistinctCountries(coreSettings, settings.CsvFilePath);
    }
}

public record PipelineSettingsWithSource(
    string CsvFilePath,
    string Delimiter,
    string LocationColumn,
    string Separator,
    string? Country,
    IReadOnlyList<string> ParseFields)
{
    public PipelineSettings ToCoreSettings()
        => new(Delimiter, LocationColumn, Separator, ParseFields);
}
