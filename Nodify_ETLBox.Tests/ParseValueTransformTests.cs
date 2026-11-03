using EtlDesigner.Engine.Parsing;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class ParseValueTransformTests
{
    private static readonly string[] Columns = { "id", "sample", "a", "b", "c", "d", "e", "note" };

    private static ParseValueRecord Record(int number, string sample, string id = "1")
        => new(number, new Dictionary<string, string?>
        {
            ["id"] = id, ["sample"] = sample,
            ["a"] = "A", ["b"] = "B", ["c"] = "C", ["d"] = "D", ["e"] = "E", ["note"] = "N"
        });

    private static ParseValueSettings Settings(params Slot[] slots) => new() { SourceColumn = "sample", Slots = slots };

    [Fact]
    public void ComponentKUsesSlotK_WithIrregularSkippingSlots()
    {
        var settings = Settings(new Slot("S1", new[] { "e", "a" }), new Slot("S2", new[] { "c" }));

        var result = ParseValueTransform.Run(Columns, new[] { Record(1, "K:x|y") }, settings);

        Assert.Equal(new[] { "sample_id", "component", "value_a", "value_b" }, result.OutputColumns);
        Assert.Equal(new[] { "K", "x", "E", "A" }, result.Rows[0]);
        Assert.Equal(new[] { "K", "y", "C", "" }, result.Rows[1]);
    }

    [Fact]
    public void FewerComponentsThanSlots_IsNormal()
    {
        var settings = Settings(new Slot("S1", new[] { "a" }), new Slot("S2", new[] { "b" }), new Slot("S3", new[] { "c" }));

        var result = ParseValueTransform.Run(Columns, new[] { Record(1, "K:x") }, settings);

        Assert.Single(result.Rows);
        Assert.Empty(result.Rejects);
        Assert.Empty(result.Flagged);
    }

    [Fact]
    public void MoreComponentsThanSlots_RejectPolicy_RejectsWithReason()
    {
        var settings = Settings(new Slot("S1", new[] { "a" }), new Slot("S2", new[] { "b" }));

        var result = ParseValueTransform.Run(Columns, new[] { Record(7, "K:x|y|z") }, settings);

        Assert.Empty(result.Rows);
        Assert.Equal(new RecordIssue(7, "component count 3 exceeds 2 column groups"), Assert.Single(result.Rejects));
    }

    [Fact]
    public void MoreComponentsThanSlots_PairWhatFits_KeepsFirstSlotsAndFlags()
    {
        var settings = Settings(new Slot("S1", new[] { "a" }), new Slot("S2", new[] { "b" })) with { ExcessPolicy = ExcessPolicy.PairWhatFits };

        var result = ParseValueTransform.Run(Columns, new[] { Record(3, "K:x|y|z") }, settings);

        Assert.Equal(new[] { "x", "y" }, result.Rows.Select(r => r[1]));
        Assert.Equal(new RecordIssue(3, "component count 3 exceeds 2 column groups (paired what fits)"), Assert.Single(result.Flagged));
        Assert.Equal(1, result.RowsFlagged);
    }

    [Fact]
    public void CellErrors_AreRejectedWithTheParserReason()
    {
        var settings = Settings(new Slot("S1", new[] { "a" }));

        var result = ParseValueTransform.Run(Columns, new[] { Record(1, "Kx"), Record(2, ""), Record(3, ":x") }, settings);

        Assert.Equal(new[] { "no key delimiter", "empty value", "empty key" }, result.Rejects.Select(r => r.Reason));
        Assert.Equal(new[] { 1, 2, 3 }, result.Rejects.Select(r => r.RecordNumber));
        Assert.Equal(3, result.RowsIn);
        Assert.Equal(0, result.RowsOut);
    }

    [Theory]
    [InlineData(DuplicatePolicy.Allow, 2, 0, 0)]
    [InlineData(DuplicatePolicy.Flag, 2, 0, 1)]
    [InlineData(DuplicatePolicy.Reject, 0, 1, 0)]
    public void DuplicateComponents_FollowThePolicy(DuplicatePolicy policy, int rowsOut, int rejected, int flagged)
    {
        var settings = Settings(new Slot("S1", new[] { "a" }), new Slot("S2", new[] { "b" })) with { DuplicatePolicy = policy };

        var result = ParseValueTransform.Run(Columns, new[] { Record(4, "K:x|x") }, settings);

        Assert.Equal(rowsOut, result.RowsOut);
        Assert.Equal(rejected, result.RowsRejected);
        Assert.Equal(flagged, result.RowsFlagged);
        Assert.All(result.Rejects.Concat(result.Flagged), issue => Assert.Equal("duplicate component ids", issue.Reason));
    }

    [Fact]
    public void CarryAll_TakesEveryColumnThatIsNotSourceOrValue_InFileOrder()
    {
        var settings = Settings(new Slot("S1", new[] { "a", "b" }), new Slot("S2", new[] { "d" })) with { CarryColumns = CarryColumns.All };

        var result = ParseValueTransform.Run(Columns, new[] { Record(1, "K:x", id: "9") }, settings);

        Assert.Equal(new[] { "id", "c", "e", "note", "sample_id", "component", "value_a", "value_b" }, result.OutputColumns);
        Assert.Equal(new[] { "9", "C", "E", "N", "K", "x", "A", "B" }, result.Rows[0]);
    }

    [Fact]
    public void CarryList_ComesFirstInListOrder()
    {
        var settings = Settings(new Slot("S1", new[] { "a" })) with { CarryColumns = CarryColumns.Of("note", "id") };

        var result = ParseValueTransform.Run(Columns, new[] { Record(1, "K:x") }, settings);

        Assert.Equal(new[] { "note", "id", "sample_id", "component", "value_a" }, result.OutputColumns);
    }

    [Fact]
    public void EachLevel_AddsSubPartColumnBeforeValues()
    {
        var levels = new[]
        {
            new DelimiterLevel(":", SplitMode.Once),
            new DelimiterLevel("|", SplitMode.All),
            new DelimiterLevel("/", SplitMode.Each, "revision")
        };
        var settings = Settings(new Slot("S1", new[] { "a" }), new Slot("S2", new[] { "b" })) with { DelimiterLevels = levels };

        var result = ParseValueTransform.Run(Columns, new[] { Record(1, "K:x/r2|y") }, settings);

        Assert.Equal(new[] { "sample_id", "component", "revision", "value_a" }, result.OutputColumns);
        Assert.Equal(new[] { "K", "x", "r2", "A" }, result.Rows[0]);
        Assert.Equal(new[] { "K", "y", "", "B" }, result.Rows[1]);
    }

    [Fact]
    public void OutputKeepsRecordOrderThenComponentOrder()
    {
        var settings = Settings(new Slot("S1", new[] { "a" }), new Slot("S2", new[] { "b" }));

        var result = ParseValueTransform.Run(Columns, new[] { Record(1, "K1:x|y"), Record(2, "K2:z") }, settings);

        Assert.Equal(new[] { "K1/x", "K1/y", "K2/z" }, result.Rows.Select(r => $"{r[0]}/{r[1]}"));
    }

    [Fact]
    public void UnknownColumnInSlot_ThrowsWithClearMessage()
    {
        var settings = Settings(new Slot("S1", new[] { "missing" }));

        var ex = Assert.Throws<ArgumentException>(() => ParseValueTransform.Run(Columns, Array.Empty<ParseValueRecord>(), settings));
        Assert.Contains("'missing'", ex.Message);
    }

    [Fact]
    public void WideShape_IsNotSupportedYet()
    {
        var settings = Settings(new Slot("S1", new[] { "a" })) with { OutputShape = OutputShape.Wide };

        Assert.Throws<NotSupportedException>(() => ParseValueTransform.Run(Columns, Array.Empty<ParseValueRecord>(), settings));
    }
}
