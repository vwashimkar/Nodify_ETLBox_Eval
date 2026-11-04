namespace EtlDesigner.Engine.Csv;

public sealed record CsvPreview(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    bool HasMoreRows,
    string? Error,
    long ElapsedMs,
    bool Cancelled = false);

/// <summary>
/// Reads the first rows of a file for the preview, through <see cref="CsvRecordReader"/> (the
/// same ETLBox code path as the full run).
/// </summary>
public sealed class CsvPreviewReader
{
    /// <summary>Called with the running row count after each row is collected. Used by tests.</summary>
    internal Action<int>? RowCollected { get; set; }

    public async Task<CsvPreview> ReadAsync(CsvFileSettings settings, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var previewRows = settings.PreviewRows < 1 ? 1 : settings.PreviewRows;
        var rowsToRead = previewRows + 1;
        var collectedRows = new List<IReadOnlyList<string>>(rowsToRead);
        IReadOnlyList<string> columns = Array.Empty<string>();

        var outcome = await new CsvRecordReader().ReadAsync(settings, record =>
        {
            columns = record.Columns;
            collectedRows.Add(record.Values);
            RowCollected?.Invoke(collectedRows.Count);
            return collectedRows.Count < rowsToRead;
        }, token);

        if (outcome.Cancelled)
        {
            return CreateCancelled(outcome.ElapsedMs);
        }

        if (outcome.SettingsError is not null)
        {
            return CreateError(outcome.SettingsError, outcome.ElapsedMs);
        }

        if (outcome.LicenceLimitReached)
        {
            return CreateError("The ETLBox licence stopped the preview. A valid licence is needed to read this file.", outcome.ElapsedMs);
        }

        if (outcome.Failure is not null)
        {
            return CreateError($"Could not read the CSV preview. Check separator/header/encoding and try again. Details: {outcome.Failure.Message}", outcome.ElapsedMs);
        }

        if (outcome.StructuralErrors.Count > 0)
        {
            var message = BuildStructuralErrorMessage(outcome.StructuralErrors[0].ErrorText);
            return new CsvPreview(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>(), false, message, outcome.ElapsedMs);
        }

        if (collectedRows.Count == 0)
        {
            return new CsvPreview(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>(), false, null, outcome.ElapsedMs);
        }

        var hasMoreRows = collectedRows.Count > previewRows;
        var rows = collectedRows.Take(previewRows).ToList();
        return new CsvPreview(columns, rows, hasMoreRows, null, outcome.ElapsedMs);
    }

    private static CsvPreview CreateError(string error, long elapsedMs)
        => new(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>(), false, error, elapsedMs);

    private static CsvPreview CreateCancelled(long elapsedMs)
        => new(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>(), false, null, elapsedMs, Cancelled: true);

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
