namespace EtlDesigner.Engine.Csv;

public enum CsvColumnSelectionMode
{
    Ignore,
    KeepOnly
}

public sealed class CsvColumnSelection
{
    public CsvColumnSelectionMode Mode { get; set; } = CsvColumnSelectionMode.Ignore;

    public List<string> Names { get; } = [];

    public CsvColumnSelection Clone()
    {
        var copy = new CsvColumnSelection { Mode = Mode };
        copy.Names.AddRange(Names);
        return copy;
    }

    /// <summary>
    /// The columns that leave the CsvFile node, in file order. Names match without case, as in
    /// Data Studio.
    /// </summary>
    public IReadOnlyList<string> KeptColumns(IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var names = Names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Mode == CsvColumnSelectionMode.Ignore
            ? columns.Where(c => !names.Contains(c)).ToList()
            : columns.Where(c => names.Contains(c)).ToList();
    }
}
