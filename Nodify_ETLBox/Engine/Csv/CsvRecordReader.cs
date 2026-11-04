using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Text;
using ETLBox;
using ETLBox.Csv;
using ETLBox.DataFlow;

namespace EtlDesigner.Engine.Csv;

/// <summary>
/// One data record. <see cref="RecordNumber"/> is 1-based from the first data record: header and
/// skipped lines are not counted, and a record over two lines counts once (spec section 0).
/// <see cref="Columns"/> is the same list for every record of a read.
/// </summary>
public sealed record CsvRecord(int RecordNumber, IReadOnlyList<string> Columns, IReadOnlyList<string> Values);

/// <summary>A record ETLBox could not parse (LinkErrorTo). <see cref="RecordNumber"/> is 0 when the text has no row number.</summary>
public sealed record CsvStructuralError(int RecordNumber, string ErrorText);

/// <summary>How a read ended.</summary>
public sealed record CsvReadOutcome(
    IReadOnlyList<string> Columns,
    int RecordsRead,
    IReadOnlyList<CsvStructuralError> StructuralErrors,
    string? SettingsError,
    Exception? Failure,
    bool LicenceLimitReached,
    bool Cancelled,
    long ElapsedMs);

/// <summary>
/// Reads a CSV file through ETLBox and hands each record to a callback, one at a time, without
/// keeping the records. The preview (<see cref="CsvPreviewReader"/>) and the full run both read
/// through this class, so there is one CSV code path.
/// </summary>
public sealed class CsvRecordReader
{
    /// <param name="onRecord">Called for each record on an ETLBox thread. Return false to stop reading.</param>
    public async Task<CsvReadOutcome> ReadAsync(CsvFileSettings settings, Func<CsvRecord, bool> onRecord, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(onRecord);

        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<string> columns = Array.Empty<string>();
        var recordsRead = 0;
        var structuralErrors = new List<CsvStructuralError>();

        CsvReadOutcome Outcome(string? settingsError = null, Exception? failure = null, bool licence = false, bool cancelled = false)
            => new(columns, recordsRead, structuralErrors, settingsError, failure, licence, cancelled, stopwatch.ElapsedMilliseconds);

        if (token.IsCancellationRequested)
        {
            return Outcome(cancelled: true);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(settings.Path))
            {
                return Outcome("File path is empty. Choose a CSV file and try again.");
            }

            if (!File.Exists(settings.Path))
            {
                return Outcome("File was not found. Check the path and try again.");
            }

            var delimiter = ResolveDelimiter(settings.Separator, out var delimiterError);
            if (delimiterError != null)
            {
                return Outcome(delimiterError);
            }

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var encodingName = string.IsNullOrWhiteSpace(settings.EncodingName) ? "utf-8" : settings.EncodingName;
            Encoding encoding;
            try
            {
                encoding = Encoding.GetEncoding(encodingName);
            }
            catch (Exception)
            {
                return Outcome("Encoding name is unknown. Enter a valid encoding like utf-8 or windows-1252.");
            }

            var errors = new MemoryDestination<ETLBoxError>();
            var internalCancel = false;
            List<string>? rawKeys = null;

            // The caller's token is never handed to ETLBox. A cancel that lands while the
            // network is starting made ExecuteAsync wait forever (reproduced 2026-11-03,
            // CsvPreviewCancellationTests). ETLBox only sees our own token, cancelled from
            // inside the row step, which is the path proven in spec section 0. The caller's
            // cancel is checked before the start and on every row.
            using var stopCts = new CancellationTokenSource();

            var source = new CsvSource<ExpandoObject>(settings.Path)
            {
                Encoding = encoding
            };

            source.Configuration.Delimiter = delimiter;
            source.Configuration.HasHeaderRecord = settings.HasHeader;
            source.Configuration.ShouldSkipRecord = args =>
            {
                if (settings.SkipLines <= 0)
                {
                    return false;
                }

                return args.Row.Parser.Row <= settings.SkipLines;
            };

            // The record counter (spec section 0). Returning null drops the row, so nothing
            // collects in the sink and the whole file is never held in memory.
            var step = new RowTransformation(row =>
            {
                if (internalCancel || row is not IDictionary<string, object?> dict)
                {
                    return null!;
                }

                if (rawKeys is null)
                {
                    rawKeys = dict.Keys.ToList();
                    columns = NormalizeColumns(rawKeys, settings.HasHeader);
                }

                var values = new string[rawKeys.Count];
                for (var i = 0; i < rawKeys.Count; i++)
                {
                    values[i] = dict.TryGetValue(rawKeys[i], out var value) ? value?.ToString() ?? string.Empty : string.Empty;
                }

                recordsRead++;
                var keepGoing = onRecord(new CsvRecord(recordsRead, columns, values));

                if (!keepGoing || token.IsCancellationRequested)
                {
                    internalCancel = true;
                    stopCts.Cancel();
                }

                return null!;
            });

            var sink = new MemoryDestination<ExpandoObject>();

            source.LinkTo(step);
            step.LinkTo(sink);
            source.LinkErrorTo(errors);

            var network = new Network(source, step, sink);

            try
            {
                await network.ExecuteAsync(stopCts.Token);
            }
            catch (OperationCanceledException) when (internalCancel)
            {
                // Expected: the callback or the caller asked to stop.
            }
            catch (Exception ex) when (IsLicenceError(ex))
            {
                return Outcome(licence: true);
            }

            foreach (var error in errors.Data)
            {
                if (IsLicenceText(error.ErrorText))
                {
                    return Outcome(licence: true);
                }

                structuralErrors.Add(new CsvStructuralError(RecordNumberOf(error.ErrorText, settings), error.ErrorText ?? string.Empty));
            }

            return Outcome(cancelled: token.IsCancellationRequested);
        }
        catch (Exception ex) when (IsLicenceError(ex))
        {
            return Outcome(licence: true);
        }
        catch (Exception ex)
        {
            return Outcome(failure: ex);
        }
    }

    /// <summary>
    /// The record number of a structural error. ETLBox's "Row: N" counts the header as row 1
    /// (spec section 0, [P] for a header and no skipped lines). Skipped lines are assumed to be
    /// counted in N too, as <c>ShouldSkipRecord</c> sees them in <c>Parser.Row</c> [U].
    /// </summary>
    internal static int RecordNumberOf(string? errorText, CsvFileSettings settings)
    {
        if (!CsvErrorText.TryGetRowNumber(errorText, out var row))
        {
            return 0;
        }

        var number = row - (settings.HasHeader ? 1 : 0) - Math.Max(0, settings.SkipLines);
        return number < 1 ? 0 : number;
    }

    /// <summary>
    /// The free-edition stop at 5,000 rows read and an expired trial key [P: wording seen on
    /// 2026-11-02, "The registered trial key expired"]. The free-edition wording at 5,001 rows is
    /// not recorded in this repo, so any text about a licence or key counts [U]. The exception
    /// text itself is never shown, so a key cannot leak through it.
    /// </summary>
    internal static bool IsLicenceError(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (IsLicenceText(e.Message) || e.GetType().Name.Contains("Licen", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (e is AggregateException aggregate && aggregate.InnerExceptions.Any(IsLicenceError))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLicenceText(string? text)
        => !string.IsNullOrEmpty(text)
           && (text.Contains("licen", StringComparison.OrdinalIgnoreCase)
               || text.Contains("trial key", StringComparison.OrdinalIgnoreCase));

    internal static string ResolveDelimiter(string separator, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(separator))
        {
            return ",";
        }

        var trimmed = separator.Trim();
        if (string.Equals(trimmed, "tab", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "\\t", StringComparison.Ordinal))
        {
            return "\t";
        }

        if (trimmed.Length != 1)
        {
            error = "Separator must be a single character or 'tab'/'\\t'.";
            return ",";
        }

        return trimmed;
    }

    internal static IReadOnlyList<string> NormalizeColumns(IReadOnlyList<string> rawColumns, bool hasHeader)
    {
        if (!hasHeader)
        {
            return Enumerable.Range(1, rawColumns.Count)
                .Select(index => $"Col{index}")
                .ToList();
        }

        var names = new List<string>(rawColumns.Count);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < rawColumns.Count; i++)
        {
            var baseName = string.IsNullOrWhiteSpace(rawColumns[i]) ? $"Col{i + 1}" : rawColumns[i].Trim();

            if (!seen.TryGetValue(baseName, out var count))
            {
                seen[baseName] = 1;
                names.Add(baseName);
                continue;
            }

            count++;
            seen[baseName] = count;
            names.Add($"{baseName}_{count}");
        }

        return names;
    }
}
