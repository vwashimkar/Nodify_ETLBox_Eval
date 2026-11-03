using EtlDesigner.Engine.Parsing;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class ParseValueSettingsTests
{
    [Fact]
    public void SlotsFromGroups_CutsOrderedNamesIntoGroups()
    {
        var columns = Enumerable.Range(1, 18).Select(i => $"r{i:00}").ToList();

        var slots = ParseValueSettings.SlotsFromGroups(columns, 3);

        Assert.Equal(6, slots.Count);
        Assert.Equal("S1", slots[0].Name);
        Assert.Equal(new[] { "r01", "r02", "r03" }, slots[0].Columns);
        Assert.Equal(new[] { "r16", "r17", "r18" }, slots[5].Columns);
    }

    [Fact]
    public void SlotsFromGroups_LastGroupMayBeShorter()
    {
        var slots = ParseValueSettings.SlotsFromGroups(new[] { "a", "b", "c", "d", "e" }, 2);

        Assert.Equal(3, slots.Count);
        Assert.Equal(new[] { "e" }, slots[2].Columns);
    }

    [Fact]
    public void SlotsFromGroups_RejectsGroupSizeBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ParseValueSettings.SlotsFromGroups(new[] { "a" }, 0));
    }

    [Theory]
    [InlineData(0, "value_a")]
    [InlineData(1, "value_b")]
    [InlineData(25, "value_z")]
    [InlineData(26, "value_aa")]
    public void ValueColumnName_UsesLetters(int index, string expected)
    {
        Assert.Equal(expected, ParseValueSettings.ValueColumnName(index));
    }

    [Fact]
    public void Defaults_MatchTheSpec()
    {
        var settings = new ParseValueSettings { SourceColumn = "sample", Slots = Array.Empty<Slot>() };

        Assert.True(settings.Trim);
        Assert.True(settings.DropEmpty);
        Assert.Equal(ExcessPolicy.Reject, settings.ExcessPolicy);
        Assert.Equal(DuplicatePolicy.Allow, settings.DuplicatePolicy);
        Assert.Equal(OutputShape.Long, settings.OutputShape);
        Assert.False(settings.CarryColumns.AllOtherColumns);
        Assert.Empty(settings.CarryColumns.Names);
        Assert.Equal(":", settings.DelimiterLevels[0].Delimiter);
        Assert.Equal(SplitMode.Once, settings.DelimiterLevels[0].Mode);
        Assert.Equal("|", settings.DelimiterLevels[1].Delimiter);
        Assert.Equal(SplitMode.All, settings.DelimiterLevels[1].Mode);
    }
}
