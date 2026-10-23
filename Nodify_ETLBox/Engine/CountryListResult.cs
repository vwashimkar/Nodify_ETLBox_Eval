namespace EtlDesigner.Engine;

public record CountryListResult(
    IReadOnlyList<string> Countries,
    int RowsRead,
    int RowsSkippedMalformed,
    string? ErrorMessage);
