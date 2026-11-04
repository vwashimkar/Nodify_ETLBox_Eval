using System.Diagnostics;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;

namespace EtlDesigner.Engine.Pipeline;

/// <summary>Counts so far. Flagged counts records, not reasons.</summary>
public sealed record PipelineCounts(int Read, int Parsed, int Rejected, int Flagged, int Out);

/// <summary>Reported about every 250 ms during a run, and once at the end.</summary>
public sealed record PipelineProgress(PipelineCounts Counts, long ElapsedMs);

/// <summary>
/// The result of a full run. <see cref="Rows"/> is the .NET object of the DotNetObject node
/// (List&lt;Dictionary&lt;string, string&gt;&gt;); <see cref="Columns"/> gives the column order,
/// because a dictionary does not keep one. <see cref="Error"/> stops the run with a message for
/// the user; with <see cref="LicenceLimitReached"/> the rows read before the stop are kept.
/// </summary>
public sealed record PipelineResult(
    IReadOnlyList<string> Columns,
    List<Dictionary<string, string>> Rows,
    IReadOnlyList<RecordIssue> Rejects,
    IReadOnlyList<RecordIssue> Flagged,
    PipelineCounts Counts,
    bool Cancelled,
    long ElapsedMs,
    string? Error = null,
    bool LicenceLimitReached = false);

/// <summary>
/// Runs CsvFile -> ParseValue -> DotNetObject over a whole file. The file is read through
/// <see cref="CsvRecordReader"/>, so the caller's token never reaches ETLBox.
/// </summary>
public static class PipelineRunner
{
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    public static string LicenceLimitMessage(int rowsRead)
        => $"The ETLBox licence limit was reached after {rowsRead:N0} rows. Rows so far are kept. A valid licence is needed to read the full file.";

    /// <summary>Reason of a reject for a record ETLBox could not parse [U] wording chosen.</summary>
    public static string StructuralReason(string errorText)
        => $"the CSV structure of this record is broken (check the quotes and the separator). ETLBox: {errorText}";

    /// <param name="filePath">The file to run; null or empty uses the CsvFile path of the definition.</param>
    public static async Task<PipelineResult> RunAsync(
        PipelineDefinition definition,
        string? filePath,
        IProgress<PipelineProgress>? progress,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var stopwatch = Stopwatch.StartNew();
        var settings = string.IsNullOrWhiteSpace(filePath) ? definition.CsvFile : definition.CsvFile with { Path = filePath };
        var selection = definition.Columns;
        var kind = definition.Output.Kind;

        var rows = new List<Dictionary<string, string>>();
        var rejects = new List<RecordIssue>();
        var flagged = new List<RecordIssue>();
        IReadOnlyList<string> outputColumns = Array.Empty<string>();
        int read = 0, parsed = 0, flaggedRecords = 0;
        ParseValueStep? step = null;
        int[]? keptIndexes = null;
        IReadOnlyList<string>? keptColumns = null;
        string? setupError = null;
        var lastReport = TimeSpan.Zero;

        PipelineCounts Counts() => new(read, parsed, rejects.Count, flaggedRecords, rows.Count);

        PipelineResult Result(string? error = null, bool licence = false, bool cancelled = false)
        {
            var counts = Counts();
            progress?.Report(new PipelineProgress(counts, stopwatch.ElapsedMilliseconds));
            return new PipelineResult(outputColumns, rows, rejects.OrderBy(r => r.RecordNumber).ToList(), flagged, counts,
                cancelled, stopwatch.ElapsedMilliseconds, error, licence);
        }

        var outputErrors = OutputSettings.Validate(definition.Output);
        if (outputErrors.Count > 0)
        {
            return Result(outputErrors[0]);
        }

        var outcome = await new CsvRecordReader().ReadAsync(settings, record =>
        {
            if (step is null)
            {
                try
                {
                    keptColumns = selection.KeptColumns(record.Columns);
                    var position = record.Columns.Select((c, i) => (c, i)).GroupBy(p => p.c).ToDictionary(g => g.Key, g => g.First().i);
                    keptIndexes = keptColumns.Select(c => position[c]).ToArray();
                    step = ParseValueTransform.Prepare(keptColumns, definition.ParseValue);
                    outputColumns = step.OutputColumns;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    setupError = ex.Message;
                    return false;
                }
            }

            read++;
            var values = new Dictionary<string, string?>(keptIndexes!.Length, StringComparer.Ordinal);
            for (var i = 0; i < keptIndexes.Length; i++)
            {
                values[keptColumns![i]] = record.Values[keptIndexes[i]];
            }

            var result = step.Process(new ParseValueRecord(record.RecordNumber, values));
            if (result.Reject is not null)
            {
                rejects.Add(result.Reject);
            }
            else
            {
                parsed++;
            }

            if (result.Flags.Count > 0)
            {
                flagged.AddRange(result.Flags);
                flaggedRecords++;
            }

            foreach (var row in result.Rows)
            {
                rows.Add(Shape(kind, outputColumns, row));
            }

            if (progress is not null && stopwatch.Elapsed - lastReport >= ProgressInterval)
            {
                lastReport = stopwatch.Elapsed;
                progress.Report(new PipelineProgress(Counts(), stopwatch.ElapsedMilliseconds));
            }

            return true;
        }, token);

        foreach (var error in outcome.StructuralErrors)
        {
            read++;
            rejects.Add(new RecordIssue(error.RecordNumber, StructuralReason(error.ErrorText)));
        }

        if (outcome.Cancelled)
        {
            return Result(cancelled: true);
        }

        if (outcome.SettingsError is not null)
        {
            return Result(outcome.SettingsError);
        }

        if (setupError is not null)
        {
            return Result($"The file does not match the ParseValue settings: {setupError}");
        }

        if (outcome.LicenceLimitReached)
        {
            return Result(LicenceLimitMessage(outcome.RecordsRead), licence: true);
        }

        if (outcome.Failure is not null)
        {
            return Result($"Could not read the file. It may be open in another program, or the settings may not fit it. Close it and press Run again. Details: {outcome.Failure.Message}");
        }

        return Result();
    }

    /// <summary>One output row as the chosen .NET object kind. Only ListOfStringDictionary is built.</summary>
    private static Dictionary<string, string> Shape(ObjectKind kind, IReadOnlyList<string> columns, IReadOnlyList<string> row)
    {
        if (kind != ObjectKind.ListOfStringDictionary)
        {
            throw new NotSupportedException(ObjectKinds.NotYetSupported);
        }

        // A column name used twice keeps the last value [U]; the validator does not check it yet.
        var dictionary = new Dictionary<string, string>(columns.Count, StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++)
        {
            dictionary[columns[i]] = i < row.Count ? row[i] ?? string.Empty : string.Empty;
        }

        return dictionary;
    }
}
