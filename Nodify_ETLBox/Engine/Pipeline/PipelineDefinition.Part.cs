using System.Text.Json;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Intake;
using EtlDesigner.Engine.Parsing;

namespace EtlDesigner.Engine.Pipeline;

/// <summary>
/// CSV part pipelines (task G6b): CsvPart -> ParseValue -> DotNetObject. Kept apart from PipelineDefinition.cs,
/// because a CsvFile DLL compiles that file without Engine/Intake; without this file the hooks do nothing.
/// </summary>
public sealed partial record PipelineDefinition
{
    /// <summary>
    /// The part of a larger file to read, located by its structure (task G6a). Null for a CsvFile pipeline.
    /// When set, <see cref="CsvFile"/> is only the example: its Path is the file a run uses when it is given
    /// none, and its other fields mirror the part (<see cref="ForCsvPart"/>). The run reads the located part
    /// with the part's own delimiter, header and skip lines.
    /// </summary>
    public CsvPartSelector? CsvPart { get; init; }

    /// <summary>A CSV part pipeline over <paramref name="exampleFile"/>, the file the part was chosen in.</summary>
    public static PipelineDefinition ForCsvPart(string exampleFile, CsvPartSelector csvPart, CsvColumnSelection columns,
        ParseValueSettings parseValue, OutputSettings output)
    {
        ArgumentNullException.ThrowIfNull(exampleFile);
        ArgumentNullException.ThrowIfNull(csvPart);
        var mirror = new CsvFileSettings(exampleFile, csvPart.Expected.Delimiter, csvPart.Expected.HasHeader, csvPart.Expected.SkipLines, csvPart.Encoding);
        return new PipelineDefinition(mirror, columns, parseValue, output) { CsvPart = csvPart };
    }

    /// <summary>
    /// A guided pipeline (task G7a): CSV part -> DotNetObject without ParseValue, with an optional column split. Null for
    /// every other pipeline. When set, <see cref="ParseValue"/> is <see cref="GuidedSteps.UnusedParseValue"/> and is not run.
    /// </summary>
    public GuidedSteps? Guided { get; init; }

    /// <summary>A guided pipeline: the part's kept columns, split by <paramref name="split"/> when given, straight to the output.</summary>
    public static PipelineDefinition ForGuided(string exampleFile, CsvPartSelector csvPart, CsvColumnSelection columns,
        ColumnSplitSettings? split, OutputSettings output)
        => ForCsvPart(exampleFile, csvPart, columns, GuidedSteps.UnusedParseValue, output) with { Guided = new GuidedSteps(split) };

    partial void PartEquals(PipelineDefinition other, ref bool equal)
        => equal = (CsvPart is null
                       ? other.CsvPart is null
                       : other.CsvPart is not null && CsvPartSelectorJson.Serialize(CsvPart) == CsvPartSelectorJson.Serialize(other.CsvPart))
                   && (Guided is null
                       ? other.Guided is null
                       : other.Guided is not null && ColumnSplit.Serialize(Guided) == ColumnSplit.Serialize(other.Guided));
}

public static partial class PipelineDefinitionValidator
{
    public const string WrongPartShape = "Pipeline: the pipeline needs exactly CSV part -> ParseValue -> DotNetObject.";

    /// <summary>The node types of a CSV part pipeline; the File intake node in front of the CSV part is allowed.</summary>
    public static readonly IReadOnlyList<string> ExpectedPartChain = new[] { "CsvPart", "ParseValue", "DotNetObject" };

    public const string WrongGuidedShape = "Pipeline: the pipeline needs exactly CSV part -> DotNetObject.";

    /// <summary>The node types of a guided pipeline (task G7a): no ParseValue.</summary>
    public static readonly IReadOnlyList<string> ExpectedGuidedChain = new[] { "CsvPart", "DotNetObject" };

    static partial void ValidatePart(PipelineDefinition? definition, IReadOnlyList<string>? keptColumns, IReadOnlyList<string>? graphChain,
        List<string> messages, ref bool handled)
    {
        if (definition?.CsvPart is not { } part)
        {
            return;
        }

        handled = true;
        var chain = graphChain is { Count: > 0 } && graphChain[0] == "FileIntake" ? graphChain.Skip(1).ToList() : graphChain;
        var guided = definition.Guided;
        if (chain is not null && !chain.SequenceEqual(guided is null ? ExpectedPartChain : ExpectedGuidedChain, StringComparer.Ordinal))
        {
            messages.Add(guided is null ? WrongPartShape : WrongGuidedShape);
        }

        messages.AddRange(CsvPartMessages(part, definition.CsvFile.Path).Select(m => $"CSV part: {m}"));
        if (guided is not null)
        {
            if (guided.Split is { } split)
            {
                messages.AddRange(ColumnSplit.Validate(split, keptColumns).Select(m => $"CSV part: {m}"));
            }
        }
        else
        {
            messages.AddRange(ParseValueValidator.Validate(definition.ParseValue, keptColumns).Select(m => $"ParseValue: {m}"));
        }

        messages.AddRange(OutputSettings.Validate(definition.Output).Select(m => $"DotNetObject: {m}"));
    }

    /// <summary>One plain sentence per missing or broken piece of a selector; empty when it can be run.</summary>
    public static IReadOnlyList<string> CsvPartMessages(CsvPartSelector part, string? exampleFile)
    {
        ArgumentNullException.ThrowIfNull(part);
        var messages = new List<string>();
        if (part.FormatVersion > CsvPartSelector.CurrentFormatVersion)
        {
            messages.Add(CsvPartSelectorJson.NewerMessage);
            return messages;
        }

        if (string.IsNullOrWhiteSpace(exampleFile))
        {
            messages.Add("No example file is set; choose the file in the File intake node.");
        }

        if (part.Template is null || part.Template.Parts is not { Count: > 0 })
        {
            messages.Add("No layout is stored for this part; choose the part in Intake Studio again.");
            return messages;
        }

        if (part.PartIndex < 0 || part.PartIndex >= part.Template.Parts.Count)
        {
            messages.Add("The chosen part is not in the stored layout; choose the part in Intake Studio again.");
        }
        else if (part.Template.Parts[part.PartIndex].Format is not ("csv" or "tsv"))
        {
            messages.Add("The chosen part is not a table; choose a CSV or TSV part in Intake Studio.");
        }

        if (part.Expected is null || part.Expected.ColumnCount < 1 || part.Expected.ColumnNames is null
            || part.Expected.ColumnNames.Count != part.Expected.ColumnCount)
        {
            messages.Add("The expected columns of the part are missing; choose the part in Intake Studio again.");
        }
        else if (string.IsNullOrEmpty(part.Expected.Delimiter))
        {
            messages.Add("The part has no delimiter; set its delimiter in Intake Studio first.");
        }
        else if (part.Expected.SkipLines < 0)
        {
            messages.Add("The part's skip lines cannot be below 0; choose the part in Intake Studio again.");
        }

        if (string.IsNullOrWhiteSpace(part.ExampleSha256))
        {
            messages.Add("The example file of the part is not known; choose the part in Intake Studio again.");
        }

        return messages;
    }
}

public static partial class SettingsJson
{
    static partial void WritePart(PipelineDefinition definition, ref JsonElement? part)
    {
        if (definition.CsvPart is null)
        {
            return;
        }

        using var document = JsonDocument.Parse(CsvPartSelectorJson.Serialize(definition.CsvPart));
        part = document.RootElement.Clone();
    }

    static partial void ReadPart(JsonElement part, ref PipelineDefinition definition)
    {
        var selector = CsvPartSelectorJson.Deserialize(part.GetRawText(), out var error)
                       ?? throw new JsonException(error ?? CsvPartSelectorJson.UnreadableMessage);
        definition = definition with { CsvPart = selector };
    }

    static partial void WriteGuided(PipelineDefinition definition, ref JsonElement? guided)
    {
        if (definition.Guided is null)
        {
            return;
        }

        using var document = JsonDocument.Parse(ColumnSplit.Serialize(definition.Guided));
        guided = document.RootElement.Clone();
    }

    static partial void ReadGuided(JsonElement guided, CsvFileSettings csvFile, CsvColumnSelection columns, OutputSettings output,
        ref PipelineDefinition? definition)
        => definition = new PipelineDefinition(csvFile, columns, GuidedSteps.UnusedParseValue, output) { Guided = ColumnSplit.Deserialize(guided.GetRawText()) };
}
