using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class PipelineDefinitionTests
{
    internal static string CompositePath(string file)
        => Path.Combine(AppContext.BaseDirectory, "SampleData", "TestData", "composite", file);

    /// <summary>The C01 manifest settings: source "sample", auto(r01..r18;3), nothing carried.</summary>
    internal static PipelineDefinition C01Definition(string? path = null, CarryColumns? carry = null, ObjectKind kind = ObjectKind.ListOfStringDictionary)
    {
        var names = Enumerable.Range(1, 18).Select(i => $"r{i:00}").ToList();
        return new PipelineDefinition(
            new CsvFileSettings(path ?? CompositePath("c01_basic.csv"), ",", true, 0, "utf-8"),
            new CsvColumnSelection(),
            new ParseValueSettings
            {
                SourceColumn = "sample",
                Slots = ParseValueSettings.SlotsFromGroups(names, 3),
                CarryColumns = carry ?? CarryColumns.None
            },
            new OutputSettings(kind));
    }

    [Fact]
    public void PipelineDefinition_RoundTrip_WritesEnumsAsNames()
    {
        var selection = new CsvColumnSelection { Mode = CsvColumnSelectionMode.KeepOnly };
        selection.Names.AddRange(["sample", "r01"]);
        var definition = C01Definition(carry: CarryColumns.Of("id", "batch")) with
        {
            ParseValue = C01Definition().ParseValue with
            {
                DelimiterLevels = [new(":", SplitMode.Once), new("|", SplitMode.All), new("/", SplitMode.Each, "revision")],
                ExcessPolicy = ExcessPolicy.PairWhatFits,
                DuplicatePolicy = DuplicatePolicy.Flag
            }
        };
        definition = new PipelineDefinition(definition.CsvFile with { SkipLines = 2, Separator = ";" }, selection, definition.ParseValue, definition.Output);

        var json = SettingsJson.Serialize(definition);
        var back = SettingsJson.DeserializePipelineDefinition(json);

        Assert.Equal(definition, back);
        Assert.Equal(json, SettingsJson.Serialize(back));
        Assert.Contains("\"kind\": \"ListOfStringDictionary\"", json);
        Assert.Contains("\"mode\": \"KeepOnly\"", json);
        Assert.Contains("\"excessPolicy\": \"PairWhatFits\"", json);
        Assert.Contains("\"version\": 1", json);
    }

    [Fact]
    public void OutputSettings_RoundTrip_EveryKind()
    {
        foreach (var info in ObjectKinds.All)
        {
            var settings = new OutputSettings(info.Kind);
            var json = SettingsJson.Serialize(settings);
            Assert.Contains($"\"{info.Kind}\"", json);
            Assert.Equal(settings, SettingsJson.DeserializeOutputSettings(json));
        }
    }

    [Fact]
    public void PipelineDefinition_ColumnSelectionIsCopied()
    {
        var selection = new CsvColumnSelection();
        selection.Names.Add("extra_01");
        var definition = new PipelineDefinition(C01Definition().CsvFile, selection, C01Definition().ParseValue, OutputSettings.Default);

        selection.Names.Add("extra_02");
        definition.Columns.Names.Add("extra_03");

        Assert.Equal(["extra_01"], definition.Columns.Names);
    }

    [Fact]
    public void Validator_ValidDefinition_GivesNoMessages()
    {
        var messages = PipelineDefinitionValidator.Validate(C01Definition(), graphChain: PipelineDefinitionValidator.ExpectedChain);

        Assert.Empty(messages);
    }

    [Fact]
    public void Validator_PrefixesMessagesWithNodeName()
    {
        var bad = C01Definition(path: CompositePath("missing.csv"), kind: ObjectKind.DataTable) with
        {
            ParseValue = C01Definition().ParseValue with { SourceColumn = "" }
        };

        var messages = PipelineDefinitionValidator.Validate(bad);

        Assert.Contains("CsvFile: File was not found. Check the path or browse to an existing file.", messages);
        Assert.Contains($"ParseValue: {ParseValueValidator.NoSourceColumn}", messages);
        Assert.Contains($"DotNetObject: {ObjectKinds.NotYetSupported}", messages);
        Assert.DoesNotContain(PipelineDefinitionValidator.WrongShape, messages);
    }

    [Fact]
    public void Validator_ChecksColumnsWhenKnown()
    {
        var messages = PipelineDefinitionValidator.Validate(C01Definition(), keptColumns: ["sample", "r01"]);

        Assert.Contains($"ParseValue: {ParseValueValidator.SlotColumnNotKept("S1", "r02")}", messages);
    }

    [Theory]
    [InlineData("CsvFile,ParseValue")]
    [InlineData("CsvFile,DotNetObject")]
    [InlineData("CsvFile,ParseValue,DotNetObject,DotNetObject")]
    [InlineData("ParseValue,CsvFile,DotNetObject")]
    public void Validator_WrongShape_GivesShapeMessage(string chain)
    {
        var messages = PipelineDefinitionValidator.Validate(C01Definition(), graphChain: chain.Split(','));

        Assert.Equal([PipelineDefinitionValidator.WrongShape], messages);
    }

    [Fact]
    public void Validator_NoDefinition_GivesOnlyShapeMessage()
    {
        Assert.Equal([PipelineDefinitionValidator.WrongShape], PipelineDefinitionValidator.Validate(null));
    }

    [Fact]
    public void ObjectKinds_OnlyListOfStringDictionaryIsSupported()
    {
        Assert.Equal([ObjectKind.ListOfStringDictionary], ObjectKinds.All.Where(k => k.IsSupported).Select(k => k.Kind));
        Assert.Equal("List<Dictionary<string, string>>", ObjectKinds.Get(ObjectKind.ListOfStringDictionary).DisplayName);
        Assert.Equal(ObjectKind.ListOfStringDictionary, OutputSettings.Default.Kind);
        Assert.Equal([ObjectKinds.NotYetSupported], OutputSettings.Validate(new OutputSettings(ObjectKind.TypedClassList)));
    }
}
