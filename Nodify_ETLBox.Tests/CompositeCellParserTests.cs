using EtlDesigner.Engine.Parsing;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class CompositeCellParserTests
{
    private static readonly IReadOnlyList<DelimiterLevel> TwoLevels = ParseValueSettings.DefaultLevels;

    private static readonly IReadOnlyList<DelimiterLevel> ThreeLevels = new[]
    {
        new DelimiterLevel(":", SplitMode.Once),
        new DelimiterLevel("|", SplitMode.All),
        new DelimiterLevel("/", SplitMode.Each, "revision")
    };

    [Fact]
    public void WorkedExample_SplitsKeyAndSixComponents_AndTrims()
    {
        var cell = CompositeCellParser.Parse("S100:compid1|compid2|compid3| compid4|compid5|compid6", TwoLevels, trim: true, dropEmpty: true);

        Assert.True(cell.IsValid);
        Assert.Equal("S100", cell.Key);
        Assert.Equal(new[] { "compid1", "compid2", "compid3", "compid4", "compid5", "compid6" }, cell.Components.Select(c => c.Name));
    }

    [Fact]
    public void NoKeyDelimiter_IsRejected()
    {
        var cell = CompositeCellParser.Parse("S401compid1|compid2", TwoLevels, true, true);

        Assert.False(cell.IsValid);
        Assert.Equal("no key delimiter", cell.Error);
    }

    [Fact]
    public void EmptyKey_IsRejected()
    {
        var cell = CompositeCellParser.Parse(":compid1|compid2", TwoLevels, true, true);

        Assert.Equal("empty key", cell.Error);
    }

    [Fact]
    public void KeyOfOnlySpaces_IsEmptyKeyWhenTrimming()
    {
        Assert.Equal("empty key", CompositeCellParser.Parse("  :compid1", TwoLevels, true, true).Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void EmptyCell_IsRejected(string? text)
    {
        Assert.Equal("empty value", CompositeCellParser.Parse(text, TwoLevels, true, true).Error);
    }

    [Fact]
    public void Trim_RemovesSpacesAroundKeyAndItems()
    {
        var cell = CompositeCellParser.Parse(" S203 : compid1 | compid2 ", TwoLevels, true, true);

        Assert.Equal("S203", cell.Key);
        Assert.Equal(new[] { "compid1", "compid2" }, cell.Components.Select(c => c.Name));
    }

    [Fact]
    public void TrimOff_KeepsSpaces()
    {
        var cell = CompositeCellParser.Parse("S1: a |b", TwoLevels, trim: false, dropEmpty: true);

        Assert.Equal(new[] { " a ", "b" }, cell.Components.Select(c => c.Name));
    }

    [Fact]
    public void DropEmpty_HandlesTrailingPipeAndDoublePipe()
    {
        var cell = CompositeCellParser.Parse("S1:a||b|", TwoLevels, true, dropEmpty: true);

        Assert.Equal(new[] { "a", "b" }, cell.Components.Select(c => c.Name));
    }

    [Fact]
    public void DropEmptyOff_KeepsEmptyItems()
    {
        var cell = CompositeCellParser.Parse("S1:a||b", TwoLevels, true, dropEmpty: false);

        Assert.Equal(new[] { "a", "", "b" }, cell.Components.Select(c => c.Name));
    }

    [Fact]
    public void KeyWithoutComponents_IsRejected()
    {
        Assert.Equal("no components", CompositeCellParser.Parse("S1:", TwoLevels, true, true).Error);
    }

    [Fact]
    public void OnlyFirstKeyDelimiterSplits()
    {
        var cell = CompositeCellParser.Parse("S1:a:x|b", TwoLevels, true, true);

        Assert.Equal("S1", cell.Key);
        Assert.Equal(new[] { "a:x", "b" }, cell.Components.Select(c => c.Name));
    }

    [Fact]
    public void EachLevel_SplitsSubPart()
    {
        var cell = CompositeCellParser.Parse("S900:compid1/rev2|compid2/rev1", ThreeLevels, true, true);

        Assert.Equal("compid1", cell.Components[0].Name);
        Assert.Equal(new[] { "rev2" }, cell.Components[0].SubParts);
    }

    [Fact]
    public void EachLevel_MissingSubPart_GivesEmptyValueNotReject()
    {
        var cell = CompositeCellParser.Parse("S902:compid3|compid4/rev3", ThreeLevels, true, true);

        Assert.True(cell.IsValid);
        Assert.Equal("compid3", cell.Components[0].Name);
        Assert.Equal(new[] { "" }, cell.Components[0].SubParts);
    }

    [Fact]
    public void InvalidLevels_Throw()
    {
        var levels = new[] { new DelimiterLevel("|", SplitMode.All), new DelimiterLevel(":", SplitMode.Once) };

        Assert.Throws<ArgumentException>(() => CompositeCellParser.Parse("S1:a", levels, true, true));
    }

    [Fact]
    public void EachLevelWithoutColumnName_Throws()
    {
        var levels = new[] { TwoLevels[0], TwoLevels[1], new DelimiterLevel("/", SplitMode.Each) };

        Assert.Throws<ArgumentException>(() => CompositeCellParser.Parse("S1:a", levels, true, true));
    }
}
