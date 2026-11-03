using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Text;
using ETLBox;
using ETLBox.Csv;
using ETLBox.DataFlow;

namespace EtlDesigner.Engine.Csv;

public sealed record CsvPreview(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    bool HasMoreRows,
    string? Error,
    long ElapsedMs);

public sealed class CsvPreviewReader
{
    public async Task<CsvPreview> ReadAsync(CsvFileSettings settings, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (string.IsNullOrWhiteSpace(settings.Path))
            {
                return CreateError("File path is empty. Choose a CSV file and try again.", stopwatch.ElapsedMilliseconds);
            }

            if (!File.Exists(settings.Path))
            {
                return CreateError("File was not found. Check the path and try again.", stopwatch.ElapsedMilliseconds);
            }

            var delimiter = ResolveDelimiter(settings.Separator, out var delimiterError);
            if (delimiterError != null)
            {
                return CreateError(delimiterError, stopwatch.ElapsedMilliseconds);
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
                return CreateError("Encoding name is unknown. Enter a valid encoding like utf-8 or windows-1252.", stopwatch.ElapsedMilliseconds);
            }

            var previewRows = settings.PreviewRows < 1 ? 1 : settings.PreviewRows;
            var rowsToRead = previewRows + 1;

            var collectedRows = new List<IReadOnlyDictionary<string, object?>>(rowsToRead);
            var errors = new MemoryDestination<ETLBoxError>();
            var internalCancel = false;

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);

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

            var collector = new RowTransformation(row =>
            {
                if (row is IDictionary<string, object?> dict)
                {
                    var copy = new Dictionary<string, object?>(dict.Count, StringComparer.Ordinal);
                    foreach (var pair in dict)
                    {
                        copy[pair.Key] = pair.Value;
                    }

                    collectedRows.Add(copy);

                    if (collectedRows.Count >= rowsToRead)
                    {
                        internalCancel = true;
                        linkedCts.Cancel();
                    }
                }

                return row;
            });

            var sink = new MemoryDestination<ExpandoObject>();

            source.LinkTo(collector);
            collector.LinkTo(sink);
            source.LinkErrorTo(errors);

            var network = new Network(source, collector, sink);

            try
            {
                await network.ExecuteAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (internalCancel || token.IsCancellationRequested)
            {
                // Expected cancellation path for preview row limit or caller cancellation.
            }

            if (errors.Data.Count > 0)
            {
                var firstError = errors.Data.First();
                var message = BuildStructuralErrorMessage(firstError.ErrorText);
                return new CsvPreview(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>(), false, message, stopwatch.ElapsedMilliseconds);
            }

            var hasMoreRows = collectedRows.Count > previewRows;
            var rows = collectedRows.Take(previewRows).ToList();

            if (rows.Count == 0)
            {
                return new CsvPreview(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>(), false, null, stopwatch.ElapsedMilliseconds);
            }

            var rawColumns = rows[0].Keys.ToList();
            var normalizedColumns = NormalizeColumns(rawColumns, settings.HasHeader);

            var valueRows = new List<IReadOnlyList<string>>(rows.Count);
            foreach (var row in rows)
            {
                var values = new List<string>(rawColumns.Count);
                foreach (var key in rawColumns)
                {
                    row.TryGetValue(key, out var value);
                    values.Add(value?.ToString() ?? string.Empty);
                }

                valueRows.Add(values);
            }

            return new CsvPreview(normalizedColumns, valueRows, hasMoreRows, null, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return CreateError($"Could not read the CSV preview. Check separator/header/encoding and try again. Details: {ex.Message}", stopwatch.ElapsedMilliseconds);
        }
    }

    private static CsvPreview CreateError(string error, long elapsedMs)
        => new(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>(), false, error, elapsedMs);

    private static string ResolveDelimiter(string separator, out string? error)
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

    private static IReadOnlyList<string> NormalizeColumns(IReadOnlyList<string> rawColumns, bool hasHeader)
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

    private static string BuildStructuralErrorMessage(string? errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText))
        {
            return "Could not parse the CSV structure. Check quoting and delimiter settings, then try again.";
        }

        if (CsvErrorText.TryGetRowNumber(errorText, out var rowNumber))
        {
            return $"Could not parse the CSV structure near row {rowNumber}. Check the quoting/delimiter in that record and reload. Details: {errorText}";
        }

        return $"Could not parse the CSV structure. Check quoting and delimiter settings, then reload. Details: {errorText}";
    }
}
