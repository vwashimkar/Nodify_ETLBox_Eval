namespace EtlDesigner.UI.ViewModels;

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
}
