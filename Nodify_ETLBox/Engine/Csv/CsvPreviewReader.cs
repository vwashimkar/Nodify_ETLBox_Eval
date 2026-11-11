using System.IO;

namespace EtlDesigner.Engine.Csv;

public sealed record CsvPreview(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    bool HasMoreRows,
    string? Error,
    long ElapsedMs,
    bool Cancelled = false,
    bool IsFormatError = false);

/// <summary>
/// Reads the first rows of a file for the preview, through <see cref="CsvRecordReader"/> (the
/// same ETLBox code path as the full run).
/// </summary>
public sealed class CsvPreviewReader
{
    /// <summary>A preview never reads more rows than this, so it stays under the free edition's limit.</summary>
    public const int MaxPreviewRows = 1000;

    /// <summary>Called with the running row count after each row is collected. Used by tests.</summary>
    internal Action<int>? RowCollected { get; set; }

    public Task<CsvPreview> ReadAsync(CsvFileSettings settings, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ReadCoreAsync(settings.PreviewRows, onRecord => new CsvRecordReader().ReadAsync(settings, onRecord, token));
    }

    /// <summary>The first rows of one CSV part of a file (G6c), through the part overload of <see cref="CsvRecordReader"/>.</summary>
    public Task<CsvPreview> ReadAsync(CsvPartSource part, int previewRows, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(part);
        return ReadCoreAsync(previewRows, onRecord => new CsvRecordReader().ReadAsync(part, onRecord, token));
    }

    private async Task<CsvPreview> ReadCoreAsync(int requestedRows, Func<Func<CsvRecord, bool>, Task<CsvReadOutcome>> read)
    {
        var previewRows = Math.Clamp(requestedRows, 1, MaxPreviewRows);
        var rowsToRead = previewRows + 1;
        var collectedRows = new List<IReadOnlyList<string>>(rowsToRead);
        IReadOnlyList<string> columns = Array.Empty<string>();

        var outcome = await read(record =>
        {
            columns = record.Columns;
            collectedRows.Add(record.Values);
            RowCollected?.Invoke(collectedRows.Count);
            return collectedRows.Count < rowsToRead;
        });

        if (outcome.Cancelled)
        {
            return CreateCancelled(outcome.ElapsedMs);
        }

        if (ErrorPreview(outcome) is { } failed)
        {
            return failed;
        }

        if (collectedRows.Count == 0)
        {
            return new CsvPreview(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>(), false, null, outcome.ElapsedMs);
        }

        var hasMoreRows = collectedRows.Count > previewRows;
        var rows = collectedRows.Take(previewRows).ToList();
        return new CsvPreview(columns, rows, hasMoreRows, null, outcome.ElapsedMs);
    }

    /// <summary>The preview for a read that failed, or null. Only a fault in the file's CSV format is an <see cref="CsvPreview.IsFormatError"/>.</summary>
    internal static CsvPreview? ErrorPreview(CsvReadOutcome outcome)
    {
        if (outcome.SettingsError is not null)
        {
            return CreateError(outcome.SettingsError, outcome.ElapsedMs);
        }

        if (outcome.LicenceLimitReached)
        {
            return CreateError("The ETLBox licence stopped the preview. A valid licence is needed to read this file.", outcome.ElapsedMs);
        }

        if (outcome.Failure is IOException or UnauthorizedAccessException)
        {
            return CreateError($"Could not open the file. Check that it exists and that no other program has it open. Details: {outcome.Failure.Message}", outcome.ElapsedMs);
        }

        if (outcome.Failure is not null)
        {
            return CreateError($"Could not read the CSV preview. Check separator/header/encoding and try again. Details: {outcome.Failure.Message}", outcome.ElapsedMs, isFormatError: true);
        }

        if (outcome.StructuralErrors.Count > 0)
        {
            return CreateError(BuildStructuralErrorMessage(outcome.StructuralErrors[0].ErrorText), outcome.ElapsedMs, isFormatError: true);
        }

        return null;
    }

    private static CsvPreview CreateError(string error, long elapsedMs, bool isFormatError = false)
        => new(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>(), false, error, elapsedMs, IsFormatError: isFormatError);

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
