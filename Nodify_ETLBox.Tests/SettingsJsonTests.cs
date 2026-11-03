using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class SettingsJsonTests
{
    [Fact]
    public void CsvFileSettings_RoundTrip()
    {
        var settings = new CsvFileSettings(@"C:\data\c06_semicolon.csv", ";", false, 3, "windows-1252", 12);

        var json = SettingsJson.Serialize(settings);
        var back = SettingsJson.DeserializeCsvFileSettings(json);

        Assert.Equal(settings, back);
        Assert.Equal(json, SettingsJson.Serialize(back));
    }

    [Theory]
    [InlineData(CsvColumnSelectionMode.Ignore, "Ignore")]
    [InlineData(CsvColumnSelectionMode.KeepOnly, "KeepOnly")]
    public void ColumnSelection_RoundTrip_WritesModeAsName(CsvColumnSelectionMode mode, string expectedText)
    {
        var selection = new CsvColumnSelection { Mode = mode };
        selection.Names.AddRange(["extra_01", "extra_02", "name"]);

        var json = SettingsJson.Serialize(selection);
        var back = SettingsJson.DeserializeColumnSelection(json);

        Assert.Contains($"\"mode\": \"{expectedText}\"", json);
        Assert.Equal(mode, back.Mode);
        Assert.Equal(selection.Names, back.Names);
    }

    [Fact]
    public void ParseValueSettings_RoundTrip_IrregularSlots_EachLevel_CarryList()
    {
        var settings = new ParseValueSettings
        {
            SourceColumn = "sample",
            DelimiterLevels =
            [
                new DelimiterLevel(":", SplitMode.Once),
                new DelimiterLevel("|", SplitMode.All),
                new DelimiterLevel("/", SplitMode.Each, "revision")
            ],
            Trim = false,
            DropEmpty = false,
            Slots =
            [
                new Slot("S1", ["m01", "m02"]),
                new Slot("S2", ["m03", "m04", "m05", "m06"]),
                new Slot("S3", ["m07"]),
                new Slot("S4", ["m09", "m11", "m13"])
            ],
            ExcessPolicy = ExcessPolicy.PairWhatFits,
            DuplicatePolicy = DuplicatePolicy.Flag,
            CarryColumns = CarryColumns.Of("id", "note")
        };

        var json = SettingsJson.Serialize(settings);
        var back = SettingsJson.DeserializeParseValueSettings(json);

        AssertSame(settings, back);
        Assert.Equal(json, SettingsJson.Serialize(back));
        Assert.Contains("\"mode\": \"Each\"", json);
        Assert.Contains("\"subPartColumn\": \"revision\"", json);
        Assert.Contains("\"excessPolicy\": \"PairWhatFits\"", json);
        Assert.Contains("\"mode\": \"List\"", json);
    }

    [Fact]
    public void ParseValueSettings_RoundTrip_CarryAll()
    {
        var settings = new ParseValueSettings { SourceColumn = "sample", Slots = [new Slot("S1", ["r01"])], CarryColumns = CarryColumns.All };

        var json = SettingsJson.Serialize(settings);
        var back = SettingsJson.DeserializeParseValueSettings(json);

        Assert.True(back.CarryColumns.AllOtherColumns);
        Assert.Contains("\"mode\": \"All\"", json);
        AssertSame(settings, back);
    }

    [Fact]
    public void ParseValueSettings_RoundTrip_CarryNone_DefaultsKept()
    {
        var settings = new ParseValueSettings { SourceColumn = "sample", Slots = [], CarryColumns = CarryColumns.None };

        var json = SettingsJson.Serialize(settings);
        var back = SettingsJson.DeserializeParseValueSettings(json);

        Assert.False(back.CarryColumns.AllOtherColumns);
        Assert.Empty(back.CarryColumns.Names);
        Assert.Contains("\"mode\": \"None\"", json);
        Assert.Contains("\"outputShape\": \"Long\"", json);
        Assert.DoesNotContain("subPartColumn", json);
        AssertSame(settings, back);
    }

    private static void AssertSame(ParseValueSettings expected, ParseValueSettings actual)
    {
        Assert.Equal(expected.SourceColumn, actual.SourceColumn);
        Assert.Equal(expected.DelimiterLevels, actual.DelimiterLevels);
        Assert.Equal(expected.Trim, actual.Trim);
        Assert.Equal(expected.DropEmpty, actual.DropEmpty);
        Assert.Equal(expected.Slots.Count, actual.Slots.Count);
        for (var i = 0; i < expected.Slots.Count; i++)
        {
            Assert.Equal(expected.Slots[i].Name, actual.Slots[i].Name);
            Assert.Equal(expected.Slots[i].Columns, actual.Slots[i].Columns);
        }

        Assert.Equal(expected.ExcessPolicy, actual.ExcessPolicy);
        Assert.Equal(expected.DuplicatePolicy, actual.DuplicatePolicy);
        Assert.Equal(expected.CarryColumns.AllOtherColumns, actual.CarryColumns.AllOtherColumns);
        Assert.Equal(expected.CarryColumns.Names, actual.CarryColumns.Names);
        Assert.Equal(expected.OutputShape, actual.OutputShape);
    }
}
