namespace EtlDesigner.Engine.Csv;

public sealed record CsvFileSettings(
    string Path,
    string Separator,
    bool HasHeader,
    int SkipLines,
    string EncodingName,
    int PreviewRows = 5);

/// <summary>
/// One csv or tsv part of a larger file: bytes [<see cref="StartByte"/>, <see cref="StartByte"/> + <see cref="Length"/>),
/// read with <see cref="EncodingName"/> (the encoding the intake scan used, never "auto"). <see cref="FirstFileLine"/>
/// is the part's first line in the file (1-based); it only turns part-relative numbers into file line numbers.
/// </summary>
public sealed record CsvPartSource(
    string Path,
    long StartByte,
    long Length,
    string EncodingName,
    string Delimiter,
    bool HasHeader,
    int SkipLines,
    long FirstFileLine);