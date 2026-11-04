using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;

namespace EtlDesigner.Engine.Pipeline;

/// <summary>
/// Everything needed to run CsvFile -> ParseValue -> DotNetObject without the editor.
/// Immutable: the column selection is copied in and every read of it returns a new copy,
/// because <see cref="CsvColumnSelection"/> itself is a mutable class.
/// </summary>
public sealed record PipelineDefinition
{
    private readonly CsvColumnSelection _columns;

    public PipelineDefinition(CsvFileSettings csvFile, CsvColumnSelection columns, ParseValueSettings parseValue, OutputSettings output)
    {
        ArgumentNullException.ThrowIfNull(csvFile);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(parseValue);
        ArgumentNullException.ThrowIfNull(output);

        CsvFile = csvFile;
        _columns = columns.Clone();
        ParseValue = parseValue;
        Output = output;
    }

    public CsvFileSettings CsvFile { get; init; }

    public CsvColumnSelection Columns => _columns.Clone();

    public ParseValueSettings ParseValue { get; init; }

    public OutputSettings Output { get; init; }

    public bool Equals(PipelineDefinition? other)
        => other is not null
           && CsvFile == other.CsvFile
           && _columns.Mode == other._columns.Mode
           && _columns.Names.SequenceEqual(other._columns.Names)
           && SettingsJson.Serialize(ParseValue) == SettingsJson.Serialize(other.ParseValue)
           && Output == other.Output;

    public override int GetHashCode() => HashCode.Combine(CsvFile, _columns.Mode, Output);
}

/// <summary>
/// Checks a whole pipeline before Execute. Messages come from the node validators, prefixed by
/// the node name. Pure apart from the file checks of <see cref="CsvFileValidator"/>.
/// </summary>
public static class PipelineDefinitionValidator
{
    public const string WrongShape = "Pipeline: the pipeline needs exactly CsvFile -> ParseValue -> DotNetObject.";

    public static readonly IReadOnlyList<string> ExpectedChain = new[] { "CsvFile", "ParseValue", "DotNetObject" };

    /// <param name="definition">Null when the graph could not be turned into a definition.</param>
    /// <param name="keptColumns">The columns that leave the CsvFile node, when known; enables the column checks.</param>
    /// <param name="graphChain">The node types in connection order, when the caller has a graph. Null skips the shape check.</param>
    public static IReadOnlyList<string> Validate(
        PipelineDefinition? definition,
        IReadOnlyList<string>? keptColumns = null,
        IReadOnlyList<string>? graphChain = null)
    {
        var messages = new List<string>();

        if (definition is null || (graphChain is not null && !graphChain.SequenceEqual(ExpectedChain, StringComparer.Ordinal)))
        {
            messages.Add(WrongShape);
        }

        if (definition is null)
        {
            return messages;
        }

        messages.AddRange(CsvFileValidator.Validate(definition.CsvFile).Select(m => $"CsvFile: {m}"));
        messages.AddRange(ParseValueValidator.Validate(definition.ParseValue, keptColumns).Select(m => $"ParseValue: {m}"));
        messages.AddRange(OutputSettings.Validate(definition.Output).Select(m => $"DotNetObject: {m}"));

        return messages.Distinct().ToList();
    }
}
