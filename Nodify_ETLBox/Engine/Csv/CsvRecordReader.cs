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
/// <see cref="Columns"/> is the same list for every record of a read. <see cref="FileLine"/> is the
/// record's first line in the file, set only when a part of a file is read (null for a whole file).
/// </summary>
public sealed record CsvRecord(int RecordNumber, IReadOnlyList<string> Columns, IReadOnlyList<string> Values, long? FileLine = null);

/// <summary>
/// A record ETLBox could not parse (LinkErrorTo). <see cref="RecordNumber"/> is 0 when the text has no row number.
/// <see cref="FileLine"/> is the record's first line in the file, set only when a part of a file is read.
/// </summary>
public sealed record CsvStructuralError(int RecordNumber, string ErrorText, long? FileLine = null);

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
public sealed partial class CsvRecordReader
{
    public const string PartChangedMessage = "The file is shorter than when its parts were found; read the file again.";

    /// <param name="onRecord">Called for each record on an ETLBox thread. Return false to stop reading.</param>
    public Task<CsvReadOutcome> ReadAsync(CsvFileSettings settings, Func<CsvRecord, bool> onRecord, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(onRecord);
        return ReadCoreAsync(settings, null, onRecord, token);
    }

    /// <summary>
    /// How to read a part of the file instead of the whole file: <see cref="Open"/> gives a stream over
    /// exactly the part's bytes, which end at <see cref="EndByte"/> of the file; <see cref="FirstFileLine"/>
    /// is the part's first line in the file. Kept free of other Engine folders, because the generated DLL
    /// compiles this file on its own.
    /// </summary>
    private sealed record PartRead(Func<Stream> Open, long EndByte, long FirstFileLine);

    /// <summary>The one read body for a whole file and for a part; only the source creation differs.</summary>
    private static async Task<CsvReadOutcome> ReadCoreAsync(CsvFileSettings settings, PartRead? part, Func<CsvRecord, bool> onRecord, CancellationToken token)
    {

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

        Stream? partStream = null;
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
                encoding = EncodingOf(encodingName);
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

            CsvSource<ExpandoObject> source;
            var lines = part is null ? null : new PartLines(part.FirstFileLine);
            if (part is null)
            {
                source = new CsvSource<ExpandoObject>(settings.Path)
                {
                    Encoding = encoding
                };
            }
            else
            {
                if (part.EndByte > new FileInfo(settings.Path).Length)
                {
                    return Outcome(PartChangedMessage);
                }

                // Closed in finally as well, because it is not known whether ETLBox closes it [U: probe-intake.md].
                source = new CsvSource<ExpandoObject>
                {
                    CreateStreamReader = _ =>
                    {
                        partStream?.Dispose();
                        partStream = part.Open();
                        return new StreamReader(partStream, encoding, detectEncodingFromByteOrderMarks: false);
                    }
                };
            }

            source.Configuration.Delimiter = delimiter;
            source.Configuration.HasHeaderRecord = settings.HasHeader;
            source.Configuration.ShouldSkipRecord = args =>
            {
                // Parser.Row counts records from the start of the stream, header and skipped lines
                // included; RawRow counts physical lines (reports/taskG6a-probes.md a) [P].
                lines?.Seen(args.Row.Parser.Row, args.Row.Parser.RawRow);
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
                // Exact when no bad record came before this one in the part [U: rule chosen in G6a].
                var fileLine = lines?.FileLineOf(recordsRead + (settings.HasHeader ? 1 : 0) + Math.Max(0, settings.SkipLines));
                var keepGoing = onRecord(new CsvRecord(recordsRead, columns, values, fileLine));

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

                var fileLine = lines is not null && CsvErrorText.TryGetRowNumber(error.ErrorText, out var row) ? lines.FileLineOf(row) : (long?)null;
                structuralErrors.Add(new CsvStructuralError(RecordNumberOf(error.ErrorText, settings), error.ErrorText ?? string.Empty, fileLine));
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
        finally
        {
            partStream?.Dispose();
        }
    }

    /// <summary>The encodings the intake scan names (utf-16le, utf-16be) and any name .NET knows.</summary>
    internal static Encoding EncodingOf(string name) => name.Trim().ToLowerInvariant() switch
    {
        "utf-16le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        "utf-16be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
        _ => Encoding.GetEncoding(name)
    };

    /// <summary>
    /// Turns a part-relative record number (<c>Parser.Row</c>) into its first file line. Keeps, from
    /// the <c>ShouldSkipRecord</c> calls, the extra lines (<c>RawRow - Row</c>) each time they change,
    /// that is only after a record with a quoted line break, so it stays small for any part size.
    /// </summary>
    private sealed class PartLines(long firstFileLine)
    {
        private readonly List<(int Row, int Extra)> _changes = [];
        private int _extra;

        public void Seen(int row, int rawRow)
        {
            lock (_changes)
            {
                if (rawRow - row != _extra)
                {
                    _extra = rawRow - row;
                    _changes.Add((row, _extra));
                }
            }
        }

        /// <summary>The first line of record <paramref name="row"/> is the line after the end of record row - 1.</summary>
        public long FileLineOf(int row)
        {
            var extra = 0;
            lock (_changes)
            {
                for (var i = _changes.Count - 1; i >= 0; i--)
                {
                    if (_changes[i].Row < row)
                    {
                        extra = _changes[i].Extra;
                        break;
                    }
                }
            }

            return firstFileLine + row - 1 + extra;
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

        // A tab character is white space, so it must be recognised before the empty check. Before
        // G6b it fell back to a comma; ETLBox's delimiter detection (on by default) hid that unless
        // title lines came first (reports/taskG6b-report.md) [P].
        if (separator == "\t")
        {
            return "\t";
        }

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
