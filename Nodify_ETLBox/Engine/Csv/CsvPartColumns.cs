using EtlDesigner.Engine.Intake;

namespace EtlDesigner.Engine.Csv;

/// <summary>
/// The outcome of <see cref="CsvPartColumns.ReadAsync"/>: the located part and its columns, or one plain
/// sentence. <see cref="Columns"/> is empty when the part was not found or has no data row.
/// </summary>
public sealed record CsvPartColumnsResult(CsvPartLocation Location, IReadOnlyList<string> Columns, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Design-time columns of a CSV part (task G6c step 0b). They come from the same reader as a run
/// (<see cref="CsvPartLocator.LocateAsync"/>, then <see cref="CsvRecordReader"/> on the located part), never
/// from <see cref="CsvPartLocation.ColumnNames"/>: the selector drops trailing empty fields and writes an
/// empty header cell as '', while ETLBox keeps those columns and names them Col&lt;N&gt; [P, G6b].
/// So ParseValue and Data Studio see exactly the columns a run produces.
/// </summary>
public static class CsvPartColumns
{
    public const string NoDataRowMessage = "The CSV part has no data rows, so its columns are not known; choose a part with at least one row.";

    /// <summary>Locates the part in <paramref name="path"/> and reads its first record only.</summary>
    public static async Task<CsvPartColumnsResult> ReadAsync(string path, CsvPartSelector selector, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var location = await CsvPartLocator.LocateAsync(path, selector, token);
        if (!location.Ok || location.Part is not { } part)
        {
            return new CsvPartColumnsResult(location, Array.Empty<string>(), location.Message);
        }

        var outcome = await new CsvRecordReader().ReadAsync(part, _ => false, token);
        if (outcome.Cancelled)
        {
            return new CsvPartColumnsResult(location, Array.Empty<string>(), CsvPartLocator.CancelledMessage);
        }

        // The preview's sentences for a failed read; a bad record further down does not change the columns.
        if (CsvPreviewReader.ErrorPreview(outcome with { StructuralErrors = [] }) is { Error: { } error })
        {
            return new CsvPartColumnsResult(location, Array.Empty<string>(), error);
        }

        return outcome.Columns.Count == 0
            ? new CsvPartColumnsResult(location, Array.Empty<string>(), NoDataRowMessage)
            : new CsvPartColumnsResult(location, outcome.Columns, null);
    }
}
