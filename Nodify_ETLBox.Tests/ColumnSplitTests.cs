using System.IO;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>Task G7a engine: the column split, the guided definition (no ParseValue), its JSON and its run.</summary>
public sealed class ColumnSplitTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("g7a_split_").FullName;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private static ParseValueRecord Record(int number, params (string Column, string? Value)[] values)
        => new(number, values.ToDictionary(v => v.Column, v => v.Value, StringComparer.Ordinal));

    [Fact]
    public void NoSplit_PassesTheKeptColumnsThrough_InOrder()
    {
        var (process, columns) = ColumnSplit.Prepare(["a", "b"], new GuidedSteps(null));
        Assert.Equal(["a", "b"], columns);
        var outcome = process(Record(1, ("a", "1"), ("b", null)));
        Assert.Null(outcome.Reject);
        Assert.Empty(outcome.Flags);
        Assert.Equal(["1", ""], Assert.Single(outcome.Rows));
    }

    [Fact]
    public void Split_ReplacesTheColumnAtItsPlace_TrimsTheParts_AndKeepsTheOthers()
    {
        var split = new ColumnSplitSettings("id", "-", ["Plate", "Well"]);
        var (process, columns) = ColumnSplit.Prepare(["x", "id", "y"], new GuidedSteps(split));
        Assert.Equal(["x", "Plate", "Well", "y"], columns);
        var outcome = process(Record(1, ("x", "1"), ("id", " P1 - A01 "), ("y", "2")));
        Assert.Equal(["1", "P1", "A01", "2"], Assert.Single(outcome.Rows));
        Assert.Empty(outcome.Flags);
    }

    [Fact]
    public void MorePieces_IsRejected_FewerPieces_IsFlaggedWithEmptyParts_EmptyValue_GivesEmptyParts()
    {
        var split = new ColumnSplitSettings("id", "|", ["A", "B"]);
        var (process, _) = ColumnSplit.Prepare(["id"], new GuidedSteps(split));

        var more = process(Record(3, ("id", "1|2|3")));
        Assert.Empty(more.Rows);
        Assert.Equal(new EtlDesigner.Engine.Parsing.RecordIssue(3, ColumnSplit.TooManyReason("id", 3, 2)), more.Reject);

        var fewer = process(Record(4, ("id", "1")));
        Assert.Equal(["1", ""], Assert.Single(fewer.Rows));
        Assert.Equal(ColumnSplit.TooFewReason("id", 1, 2), Assert.Single(fewer.Flags).Reason);

        var empty = process(Record(5, ("id", "")));
        Assert.Equal(["", ""], Assert.Single(empty.Rows));
        Assert.Empty(empty.Flags);
    }

    [Theory]
    [InlineData("", "|", "A,B", "Choose the column to split.")]
    [InlineData("id", "", "A,B", "Choose the separator to split at.")]
    [InlineData("id", "|", "A", "Give at least two part names.")]
    [InlineData("id", "|", "A,A", "Every part needs a different name.")]
    [InlineData("id", "|", "A, ", "Every part needs a name.")]
    [InlineData("other", "|", "A,B", "The column 'other' is not among the kept columns; keep it or choose another column.")]
    [InlineData("id", "|", "A,x", "The part name 'x' is already a column; choose another name.")]
    public void Validate_GivesOnePlainSentencePerProblem(string column, string separator, string names, string expected)
    {
        var split = new ColumnSplitSettings(column, separator, names.Split(','));
        Assert.Contains(expected, ColumnSplit.Validate(split, ["x", "id"]));
        Assert.Throws<ArgumentException>(() => ColumnSplit.Prepare(["x", "id"], new GuidedSteps(split)));
    }

    [Fact]
    public void GuidedSteps_JsonRoundTrips_WithAndWithoutASplit()
    {
        Assert.Equal("{}", ColumnSplit.Serialize(new GuidedSteps(null)));
        var steps = new GuidedSteps(new ColumnSplitSettings("id", "\t", ["A", "B"]));
        var json = ColumnSplit.Serialize(steps);
        Assert.Equal("{\"split\":{\"column\":\"id\",\"separator\":\"\\t\",\"partNames\":[\"A\",\"B\"]}}", json);
        Assert.Equal(json, ColumnSplit.Serialize(ColumnSplit.Deserialize(json)));
        Assert.Throws<System.Text.Json.JsonException>(() => ColumnSplit.Deserialize("{\"split\":{\"column\":\"id\"}}"));
    }

    [Fact]
    public async Task GuidedDefinition_HasNoParseValueInItsJson_RoundTrips_AndItsHashChangesWithTheSplit()
    {
        var selector = await G6bParts.I10SelectorAsync();
        var plain = PipelineDefinition.ForGuided(G6bParts.I10Path, selector, new CsvColumnSelection(), null, OutputSettings.Default);
        var split = plain with { Guided = new GuidedSteps(new ColumnSplitSettings("sample_id", "-", ["Prefix", "Number"])) };

        var json = SettingsJson.Serialize(plain);
        Assert.DoesNotContain("parseValue", json);
        Assert.Contains("\"guided\": {}", json);
        Assert.Contains("\"csvPart\"", json);
        var back = SettingsJson.DeserializePipelineDefinition(json);
        Assert.Equal(plain, back);
        Assert.Equal(json, SettingsJson.Serialize(back));
        Assert.Equal(SettingsJson.Serialize(split), SettingsJson.Serialize(SettingsJson.DeserializePipelineDefinition(SettingsJson.Serialize(split))));

        Assert.NotEqual(plain, split);
        Assert.NotEqual(MainViewModel.PipelineHash(plain), MainViewModel.PipelineHash(split));
        Assert.NotEqual(MainViewModel.PipelineHash(G6bParts.Definition(G6bParts.I10Path, selector)), MainViewModel.PipelineHash(plain));
    }

    [Fact]
    public async Task GuidedDefinition_Validate_ExpectsCsvPartThenDotNetObject_AndChecksTheSplit()
    {
        var selector = await G6bParts.I10SelectorAsync();
        var definition = PipelineDefinition.ForGuided(G6bParts.I10Path, selector, new CsvColumnSelection(),
            new ColumnSplitSettings("missing", "-", ["A", "B"]), OutputSettings.Default);
        var kept = selector.Expected.ColumnNames;
        Assert.Equal(["Pipeline: the pipeline needs exactly CSV part -> DotNetObject.", "CSV part: The column 'missing' is not among the kept columns; keep it or choose another column."],
            PipelineDefinitionValidator.Validate(definition, kept, ["FileIntake", "CsvPart", "ParseValue", "DotNetObject"]));
        Assert.Empty(PipelineDefinitionValidator.Validate(definition with { Guided = new GuidedSteps(null) }, kept, ["FileIntake", "CsvPart", "DotNetObject"]));
    }

    [Fact]
    public async Task GuidedRun_WithoutSplit_GivesThePartsRows_AsTheStandaloneCsv()
    {
        var path = G6bParts.Write(_folder, "i10.txt", G6bParts.Variant("more rows"));
        var definition = PipelineDefinition.ForGuided(path, await G6bParts.I10SelectorAsync(), new CsvColumnSelection(), null, OutputSettings.Default);
        var run = await G6bParts.Run(definition, path);
        Assert.Null(run.Error);

        var table = G6bParts.TableOf(File.ReadAllText(path)).Split('\n');
        Assert.Equal(table[0].Split(','), run.Columns);
        Assert.Equal(table.Skip(1).Select(l => l.Replace(',', '|')), G6bParts.RowTexts(run.Columns, run.Rows));
        Assert.Equal(new PipelineCounts(8, 8, 0, 0, 8), run.Counts);
    }

    [Fact]
    public async Task GuidedRun_WithSplit_SplitsEveryRow_AndRejectsWithTheFileLine()
    {
        var text = G6bParts.Variant("example").Replace("S-003,", "S-003-X,");
        var path = G6bParts.Write(_folder, "i10.txt", text);
        var definition = PipelineDefinition.ForGuided(path, await CsvPartLocatorTests.SelectorAsync(path), new CsvColumnSelection(),
            new ColumnSplitSettings("sample_id", "-", ["Prefix", "Number"]), OutputSettings.Default);
        var run = await G6bParts.Run(definition, path);
        Assert.Null(run.Error);
        Assert.Equal(["Prefix", "Number", "weight_g", "volume_ml", "chloride_pct", "result"], run.Columns);
        Assert.Equal(["S", "001"], new[] { run.Rows[0]["Prefix"], run.Rows[0]["Number"] });
        var reject = Assert.Single(run.Rejects);
        Assert.StartsWith(ColumnSplit.TooManyReason("sample_id", 3, 2) + " (file line ", reject.Reason);
        Assert.Equal(new PipelineCounts(5, 4, 1, 0, 4), run.Counts);
    }

    [Fact]
    public async Task GuidedRun_SplitColumnMissing_StopsWithThePipelineSentence()
    {
        var path = G6bParts.Write(_folder, "i10.txt", G6bParts.Variant("example"));
        var definition = PipelineDefinition.ForGuided(path, await CsvPartLocatorTests.SelectorAsync(path), new CsvColumnSelection(),
            new ColumnSplitSettings("nope", "-", ["A", "B"]), OutputSettings.Default);
        var run = await G6bParts.Run(definition, path);
        Assert.Equal("The file does not match the pipeline settings: The column 'nope' is not among the kept columns; keep it or choose another column.", run.Error);
    }
}
