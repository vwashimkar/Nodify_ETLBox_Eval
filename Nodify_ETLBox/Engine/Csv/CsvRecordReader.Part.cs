using EtlDesigner.Engine.Intake;

namespace EtlDesigner.Engine.Csv;

/// <summary>Reading one csv or tsv part of a larger file (task G6a).</summary>
public sealed partial class CsvRecordReader
{
    /// <summary>
    /// Reads one part of a file (<see cref="CsvPartSource"/>) through the same code as a whole file; only
    /// the source differs: a read-only <see cref="PartStream"/> over the part's bytes, decoded with the
    /// part's encoding (no BOM detection). Records and structural errors carry their first file line.
    /// </summary>
    /// <param name="onRecord">Called for each record on an ETLBox thread. Return false to stop reading.</param>
    public Task<CsvReadOutcome> ReadAsync(CsvPartSource part, Func<CsvRecord, bool> onRecord, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(onRecord);
        if (part.StartByte < 0 || part.Length < 0)
        {
            return Task.FromResult(new CsvReadOutcome([], 0, [], PartChangedMessage, null, false, false, 0));
        }

        var settings = new CsvFileSettings(part.Path, part.Delimiter, part.HasHeader, part.SkipLines, part.EncodingName);
        var read = new PartRead(() => new PartStream(part.Path, part.StartByte, part.Length), part.StartByte + part.Length, part.FirstFileLine);
        return ReadCoreAsync(settings, read, onRecord, token);
    }
}
