using System.Diagnostics;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Intake;
using EtlDesigner.Engine.Parsing;

namespace EtlDesigner.Engine.Pipeline;

/// <summary>Running a CSV part pipeline (task G6b). Kept apart because a CsvFile DLL compiles PipelineRunner.cs without Engine/Intake.</summary>
public static partial class PipelineRunner
{
    static partial void RunCsvPart(PipelineDefinition definition, string? filePath, IProgress<PipelineProgress>? progress, CancellationToken token,
        ref Task<PipelineResult>? run)
    {
        if (definition.CsvPart is { } selector)
        {
            run = RunCsvPartAsync(definition, selector, string.IsNullOrWhiteSpace(filePath) ? definition.CsvFile.Path : filePath, progress, token);
        }
    }

    /// <summary>
    /// Finds the part (<see cref="CsvPartLocator"/>), then reads exactly its bytes and runs the same steps as a
    /// whole file. A part that is not found stops the run with the locator's one sentence, in the same shape as
    /// a missing file; a cancel during the search gives the normal cancelled result.
    /// </summary>
    private static async Task<PipelineResult> RunCsvPartAsync(PipelineDefinition definition, CsvPartSelector selector, string path,
        IProgress<PipelineProgress>? progress, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        var location = await CsvPartLocator.LocateAsync(path, selector, token);
        var facts = new CsvPartRunFacts(location.FileName, location.Sha256, location.TemplateId, location.TemplateName, location.Similarity,
            location.Match?.ToString(), location.PartNumber, location.FirstLine, location.LastLine, location.ColumnNames);

        if (!location.Ok || location.Part is not { } part)
        {
            var cancelled = location.Reason == CsvPartFailure.Cancelled;
            var counts = new PipelineCounts(0, 0, 0, 0, 0);
            progress?.Report(new PipelineProgress(counts, stopwatch.ElapsedMilliseconds));
            return new PipelineResult(Array.Empty<string>(), new List<Dictionary<string, string>>(), Array.Empty<RecordIssue>(), Array.Empty<RecordIssue>(),
                counts, cancelled, stopwatch.ElapsedMilliseconds, cancelled ? null : location.Message, false, facts);
        }

        var result = await RunStepsAsync(definition, onRecord => new CsvRecordReader().ReadAsync(part, onRecord, token), progress, stopwatch);
        return result with { CsvPart = facts };
    }
}
