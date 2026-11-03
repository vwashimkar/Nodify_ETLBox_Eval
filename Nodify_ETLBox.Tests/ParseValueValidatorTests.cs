using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.UI.Services;
using EtlDesigner.UI.ViewModels;
using Xunit;
using V = EtlDesigner.Engine.Parsing.ParseValueValidator;

namespace Nodify_ETLBox.Tests;

public class ParseValueValidatorTests
{
    private static readonly string[] Kept = ["id", "name", "sample", "r01", "r02", "r03", "r04"];

    private static ParseValueSettings Valid() => new()
    {
        SourceColumn = "sample",
        Slots = [new Slot("S1", ["r01", "r02"]), new Slot("S2", ["r03", "r04"])],
        CarryColumns = CarryColumns.Of("id")
    };

    [Fact]
    public void ValidSettings_NoMessages()
        => Assert.Empty(V.Validate(Valid(), Kept));

    [Fact]
    public void SourceColumnEmpty()
        => Assert.Equal([V.NoSourceColumn], V.Validate(Valid() with { SourceColumn = " " }, Kept));

    [Fact]
    public void SourceColumnNotKept()
        => Assert.Equal([V.SourceNotKept("extra_01")], V.Validate(Valid() with { SourceColumn = "extra_01" }, Kept));

    [Fact]
    public void NoDelimiterLevels()
        => Assert.Equal([V.NoDelimiterLevels], V.Validate(Valid() with { DelimiterLevels = [] }, Kept));

    [Fact]
    public void EmptyDelimiterText()
    {
        var settings = Valid() with { DelimiterLevels = [new DelimiterLevel(":", SplitMode.Once), new DelimiterLevel("", SplitMode.All)] };
        Assert.Equal([V.EmptyDelimiter(2)], V.Validate(settings, Kept));
    }

    [Fact]
    public void EachLevelWithoutColumnName()
    {
        var settings = Valid() with
        {
            DelimiterLevels = [new DelimiterLevel(":", SplitMode.Once), new DelimiterLevel("|", SplitMode.All), new DelimiterLevel("/", SplitMode.Each, " ")]
        };
        Assert.Equal([V.EachWithoutColumn(3)], V.Validate(settings, Kept));
    }

    [Fact]
    public void LevelModesInWrongOrder()
    {
        var settings = Valid() with { DelimiterLevels = [new DelimiterLevel("|", SplitMode.All), new DelimiterLevel(":", SplitMode.Once)] };
        Assert.Equal([V.WrongLevelOrder], V.Validate(settings, Kept));
    }

    [Fact]
    public void NoSlots()
        => Assert.Equal([V.NoSlots], V.Validate(Valid() with { Slots = [] }, Kept));

    [Fact]
    public void SlotWithoutColumns()
    {
        var settings = Valid() with { Slots = [new Slot("S1", ["r01"]), new Slot("S2", [])] };
        Assert.Equal([V.SlotWithoutColumns("S2")], V.Validate(settings, Kept));
    }

    [Fact]
    public void SlotColumnNotKept()
    {
        var settings = Valid() with { Slots = [new Slot("S1", ["r01", "zz_new"])] };
        Assert.Equal([V.SlotColumnNotKept("S1", "zz_new")], V.Validate(settings, Kept));
    }

    [Fact]
    public void TwoSlotsWithTheSameName()
    {
        var settings = Valid() with { Slots = [new Slot("S1", ["r01"]), new Slot("S1", ["r02"])] };
        Assert.Equal([V.DuplicateSlotName("S1")], V.Validate(settings, Kept));
    }

    [Fact]
    public void CarryColumnNotKept()
        => Assert.Equal([V.CarryNotKept("batch")], V.Validate(Valid() with { CarryColumns = CarryColumns.Of("id", "batch") }, Kept));

    [Fact]
    public void ColumnsUnknown_SkipsColumnChecksOnly()
    {
        var settings = Valid() with { SourceColumn = "nowhere", Slots = [new Slot("S1", [])] };
        Assert.Equal([V.SlotWithoutColumns("S1")], V.Validate(settings, keptColumns: null));
    }

    [Fact]
    public void Editor_NewNode_IssuesOncePerProblem_WithNodeName_StatusNotConfigured()
    {
        var editor = new EditorViewModel();
        var parse = ParseValueConnectionTests.Add(editor, NodeType.ParseValue);

        editor.Validate();
        editor.Validate();

        var expected = new List<string>
        {
            "ParseValue: " + ParseValueUpstreamResolver.NotConnectedMessage,
            "ParseValue: " + V.NoSourceColumn
        };
        expected.AddRange(Enumerable.Range(1, 6).Select(i => "ParseValue: " + V.SlotWithoutColumns($"S{i}")));
        Assert.Equal(expected, editor.Issues);
        Assert.Equal("Not configured", parse.ConfigurationStatus);
    }

    [Fact]
    public async Task Editor_ConfiguredUpstreamAndSettings_StatusConfigured_NoParseValueIssues()
    {
        var (editor, csv, parse) = ParseValueUpstreamTests.ConfiguredChain("c01_basic.csv");
        await TestTimeout.Run(() => ParseValueUpstreamResolver.RefreshColumnsAsync(csv, new CsvPreviewReader(), CancellationToken.None));
        parse.ParseValueSettings = parse.ParseValueSettings! with
        {
            SourceColumn = "sample",
            Slots = ParseValueSettings.SlotsFromGroups(Enumerable.Range(1, 18).Select(i => $"r{i:00}").ToList(), 3)
        };

        editor.Validate();

        Assert.Equal("Configured", parse.ConfigurationStatus);
        Assert.Equal(["No issues found."], editor.Issues);
    }
}
