using EtlDesigner.Engine.Intake;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>Task G4a step 0: message wording, grouped issues and the display names of formats.</summary>
public class IntakePolishTests
{
    // ---- Wording ----------------------------------------------------------------------------

    [Theory]
    [InlineData(1, "1 character")]
    [InlineData(2, "2 characters")]
    [InlineData(3, "3 characters")]
    [InlineData(4, "4 characters")]
    [InlineData(0, "0 characters")]
    public void Count_UsesThePluralFromTwo(int count, string expected)
    {
        Assert.Equal(expected, IntakeText.Count(count, "character"));
    }

    [Fact]
    public void Count_TakesAnIrregularPlural()
    {
        Assert.Equal(("1 entry", "2 entries"), (IntakeText.Count(1, "entry", "entries"), IntakeText.Count(2, "entry", "entries")));
    }

    [Theory]
    [InlineData(new[] { 1 }, "column 1")]
    [InlineData(new[] { 1, 2 }, "columns 1 and 2")]
    [InlineData(new[] { 2, 3, 4 }, "columns 2, 3 and 4")]
    [InlineData(new[] { 1, 3, 5, 7 }, "columns 1, 3, 5 and 7")]
    public void Columns_ReadsLikeASentence(int[] columns, string expected)
    {
        Assert.Equal(expected, IntakeText.Columns(columns));
    }

    [Fact]
    public void HeaderWithTwoEmptyNames_NamesBothColumns()
    {
        var container = IntakeValidatorTests.Check("id,,name,\"\",x\n1,2,3,4,5\n6,7,8,9,0", "csv", delimiter: ",", header: HeaderGuess.Yes);

        Assert.Contains("columns 2 and 4", Assert.Single(container.Issues, i => i.Code == "csv.header-empty").Message);
    }

    [Fact]
    public void OneBadCharacter_IsSingular()
    {
        var container = IntakeValidatorTests.Check("a\nb\0", "text");

        Assert.StartsWith("1 character could", Assert.Single(container.Issues).Message);
    }

    // ---- Grouping ---------------------------------------------------------------------------

    private static string Table(params string[] rows) => "a,b,c,d\n" + string.Join("\n", rows);

    private static List<ValidationIssue> FieldCounts(string text, HeaderGuess header = HeaderGuess.Yes)
        => IntakeValidatorTests.Check(text, "csv", delimiter: ",", header: header).Issues.Where(i => i.Code == "csv.field-count").ToList();

    [Fact]
    public void FiveConsecutiveBadRows_AreOneIssue_WithLineCount5()
    {
        var text = Table("1,2,3,4", "1,2,3,4,5", "1,2,3,4,5", "1,2,3,4,5", "1,2,3,4,5", "1,2,3,4,5", "1,2,3,4");

        var issue = Assert.Single(FieldCounts(text));

        Assert.Equal((3L, 5L), (issue.Line!.Value, issue.LineCount!.Value));
        Assert.Equal("Rows 3 to 7 have 5 fields, but the header has 4.", issue.Message);
    }

    [Fact]
    public void OneBadRow_SaysRow_AndTheFirstDataRowBasis()
    {
        var text = "1,2,3,4\n5,6,7,8\n1,2,3,4,5\n1,2,3,4";

        var issue = Assert.Single(FieldCounts(text, HeaderGuess.No));

        Assert.Equal("Row 3 has 5 fields, but the first data row has 4.", issue.Message);
        Assert.Null(issue.LineCount);
    }

    [Fact]
    public void TwoSeparateRuns_AreTwoIssues()
    {
        var issues = FieldCounts(Table("1,2,3,4,5", "1,2,3,4,5", "1,2,3,4", "1,2,3,4,5", "1,2,3,4,5"));

        Assert.Equal(new[] { (2L, 2L), (5L, 2L) }, issues.Select(i => (i.Line!.Value, i.LineCount!.Value)));
    }

    [Fact]
    public void TwoRunsWithDifferentCounts_AreTwoIssues()
    {
        var issues = FieldCounts(Table("1,2,3,4,5", "1,2,3,4,5", "1,2,3,4,5,6", "1,2,3,4,5,6"));

        Assert.Equal(2, issues.Count);
        Assert.Contains("5 fields", issues[0].Message);
        Assert.Contains("6 fields", issues[1].Message);
    }

    [Fact]
    public void AQuotedFieldOverTwoLines_DoesNotBreakTheRun()
    {
        var issues = FieldCounts(Table("1,2,3,4,5", "1,\"x\ny\",3,4,5", "1,2,3,4,5"));

        var issue = Assert.Single(issues);
        Assert.Equal((2L, 3L), (issue.Line!.Value, issue.LineCount!.Value));
        Assert.Equal("Rows 2 to 5 have 5 fields, but the header has 4.", issue.Message);
    }

    [Fact]
    public void TwentyFiveSeparateBadRows_Give20Issues_And25InTotal()
    {
        var rows = Enumerable.Range(0, 25).SelectMany(_ => new[] { "1,2,3,4,5", "1,2,3,4" }).ToArray();

        var container = IntakeValidatorTests.Check(Table(rows), "csv", delimiter: ",", header: HeaderGuess.Yes);

        Assert.Equal((20, 25, true), (container.Issues.Count, container.TotalIssues, container.MoreNotShown));
    }

    [Fact]
    public void ConsecutiveKeyValueLinesOfAnotherForm_AreOneIssue()
    {
        var container = IntakeValidatorTests.Check("Name: one\nfirst sentence here\nsecond sentence here\nthird one here\nDate: today\nlast sentence here",
            "keyvalue", form: KeyValueForm.Colon);

        var issues = container.Issues.Where(i => i.Code == "kv.line-form").ToList();
        Assert.Equal(new (long, long?)[] { (2L, 3L), (6L, null) }, issues.Select(i => (i.Line!.Value, i.LineCount)));
        Assert.Equal("Lines 2 to 4 do not have the key and value form of the other lines.", issues[0].Message);
        Assert.Equal(2, container.TotalIssues);
    }

    // ---- Display names ----------------------------------------------------------------------

    private static ContainerInfo Info(string format, string? subType = null, string? version = null)
        => new(1, ContainerKind.Detected, format, "x", "", null, null, 1, 1, 1, 0, 1, ContainerStatus.Ok, "", ContainerConfidence.Sure, subType, SubTypeVersion: version);

    [Theory]
    [InlineData("text", null, null, "Text")]
    [InlineData("json", null, null, "JSON")]
    [InlineData("json", "badgerfish", null, "JSON (BadgerFish)")]
    [InlineData("json-lines", null, null, "JSON lines")]
    [InlineData("xml", null, null, "XML")]
    [InlineData("xml", "soap", "1.1", "XML (SOAP 1.1)")]
    [InlineData("xml", "soap", "1.2", "XML (SOAP 1.2)")]
    [InlineData("xml", "soap", null, "XML (SOAP)")]
    [InlineData("csv", null, null, "CSV table")]
    [InlineData("tsv", null, null, "TSV table")]
    [InlineData("keyvalue", null, null, "Key-value")]
    [InlineData("yaml", null, null, "YAML")]
    [InlineData("fixedwidth", null, null, "Fixed-width table")]
    [InlineData("badgerfish-json", null, null, "JSON (BadgerFish)")]
    [InlineData("soap", null, null, "XML (SOAP)")]
    [InlineData("soap", null, "1.2", "XML (SOAP 1.2)")]
    [InlineData("someformat", null, null, "someformat")]
    [InlineData("json", "othersubtype", null, "JSON")]
    public void Format_GivesTheDisplayName(string format, string? subType, string? version, string expected)
    {
        Assert.Equal(expected, ContainerDisplay.Format(Info(format, subType, version)));
    }
}
