namespace EtlDesigner.Engine.Csv;

public sealed record CsvFileSettings(
    string Path,
    string Separator,
    bool HasHeader,
    int SkipLines,
    string EncodingName,
    int PreviewRows = 5);