using EtlDesigner.Engine.Generation;
using EtlDesigner.Engine.Intake;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.UI.Services;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>Task G7a step 4: engine messages to sentences a lab user understands, with the technical text kept.</summary>
public sealed class PlainErrorTextTests
{
    public static TheoryData<string, string> EngineTexts() => new()
    {
        { PipelineRunner.StructuralReason("Bad data found at row 4"), PlainErrorText.BadRead },
        { "Could not read the file. Details: CsvHelper.BadDataException: Bad data found on row 9", PlainErrorText.BadRead },
        { "The CSV part 'csv-2' was not found in this file (no part starts at the anchor).", PlainErrorText.NotFound },
        { "This file does not look like the example (similarity 0.41; at least 0.80 is needed); check that it comes from the same instrument.", PlainErrorText.NotSameInstrument },
        { "The CSV part 'csv-2' has 4 columns in this file, but the pipeline expects 5.", PlainErrorText.ColumnCountChanged },
        { "The column names of 'csv-2' differ: expected a, b; found a, c.", PlainErrorText.HeaderChanged },
        { "This file has 3 CSV parts like 'csv-2'; the pipeline reads one.", PlainErrorText.SeveralCopies },
        { "The file is 612.0 MB, above the limit of 500 MB. Raise the limit or choose a smaller file.", PlainErrorText.TooLarge },
        { CsvPipelineDllGenerator.MissingYamlMessage, PlainErrorText.YamlMissing },
        { PipelineRunner.LicenceLimitMessage(5000), PlainErrorText.Licence },
        { "Pipeline error: The registered trial key expired. The expiration date was: 2026-11-01.", PlainErrorText.Licence },
        { "Part 2 of this file is not a table, but the pipeline expects a comma table.", PlainErrorText.NotATable },
        { "Part 2 of this file is a semicolon table, but the pipeline expects a comma table.", PlainErrorText.OtherSeparator },
        { CsvPartLocator.FileMissingMessage, PlainErrorText.FileMissing },
        { CsvPartLocator.FileLockedMessage, PlainErrorText.FileLocked }
    };

    [Theory]
    [MemberData(nameof(EngineTexts))]
    public void EngineText_GivesThePlainSentence_AndKeepsTheTechnicalTextAsDetails(string technical, string sentence)
    {
        var plain = PlainErrorText.Map(technical);
        Assert.Equal(sentence, plain.Sentence);
        Assert.Equal(technical, plain.Details);
    }

    [Theory]
    [InlineData(CsvPartFailure.NoMatch, PlainErrorText.NotSameInstrument)]
    [InlineData(CsvPartFailure.PartNotPlaced, PlainErrorText.NotFound)]
    [InlineData(CsvPartFailure.ColumnCountDiffers, PlainErrorText.ColumnCountChanged)]
    [InlineData(CsvPartFailure.ColumnNamesDiffer, PlainErrorText.HeaderChanged)]
    [InlineData(CsvPartFailure.SeveralParts, PlainErrorText.SeveralCopies)]
    [InlineData(CsvPartFailure.DelimiterDiffers, PlainErrorText.OtherSeparator)]
    [InlineData(CsvPartFailure.FileLocked, PlainErrorText.FileLocked)]
    [InlineData(CsvPartFailure.Cancelled, PlainErrorText.Cancelled)]
    public void FailureKind_DecidesBeforeTheText(CsvPartFailure kind, string sentence)
        => Assert.Equal(sentence, PlainErrorText.Map("some engine text", kind).Sentence);

    [Fact]
    public void UnknownText_ComesBackAsItIs()
    {
        var plain = PlainErrorText.Map("Something new happened.");
        Assert.Equal("Something new happened.", plain.Sentence);
        Assert.Equal("Something new happened.", plain.Details);
    }

    [Fact]
    public void RowReason_ExplainsABrokenRow_AndKeepsOtherReasons()
    {
        Assert.Equal(PlainErrorText.BadRow, PlainErrorText.RowReason(PipelineRunner.StructuralReason("x") + " (file line 9)"));
        Assert.Equal(ColumnSplit.TooManyReason("id", 3, 2), PlainErrorText.RowReason(ColumnSplit.TooManyReason("id", 3, 2)));
    }

    [Fact]
    public void NoSentence_ContainsNodeOrJsonWords()
    {
        foreach (var field in typeof(PlainErrorText).GetFields().Where(f => f.IsLiteral))
        {
            var text = (string)field.GetValue(null)!;
            foreach (var word in new[] { "node", "port", "JSON", "selector", "ParseValue", "container" })
            {
                Assert.False(System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{word}s?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                    $"'{word}' in: {text}");
            }
        }
    }
}
