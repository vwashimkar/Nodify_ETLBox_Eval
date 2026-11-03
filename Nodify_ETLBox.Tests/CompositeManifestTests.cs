using System.Text.RegularExpressions;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>
/// Runs every non-JSON row of composite/expected_results.csv through CsvPreviewReader and
/// ParseValueTransform and compares with the expected files. All CSV, including the expected
/// files and the manifest, is read through CsvPreviewReader (one parser).
/// </summary>
public class CompositeManifestTests
{
    // C15 and C16 are JSON tests (later stage). C10 is a separate skipped test below.
    private static readonly string[] NotInThisRun = { "C10", "C15", "C16" };

    private static readonly string[] KnownTestIds =
    {
        "C01", "C02", "C03", "C03b", "C04", "C04b", "C05", "C06", "C07", "C07b", "C08", "C08b",
        "C09", "C10", "C11", "C11b", "C12", "C14", "C15", "C16"
    };

    public static TheoryData<string> ManifestRows()
    {
        var data = new TheoryData<string>();
        foreach (var id in KnownTestIds.Except(NotInThisRun))
        {
            data.Add(id);
        }

        return data;
    }

    [Fact]
    public async Task Manifest_ListsExactlyTheKnownTests()
    {
        var manifest = await ReadManifestAsync();

        Assert.Equal(KnownTestIds, manifest.Select(r => r["test_id"]));
    }

    [Theory]
    [MemberData(nameof(ManifestRows))]
    public async Task ManifestRow_MatchesExpectedFiles(string testId)
    {
        var row = (await ReadManifestAsync()).Single(r => r["test_id"] == testId);

        var input = await ReadCsvAsync(
            row["file"], row["separator"], row["header"] == "yes", int.Parse(row["skip_lines"]));
        var records = input.Rows
            .Select((values, index) => new ParseValueRecord(
                index + 1,
                input.Columns.Select((c, i) => (c, v: (string?)values[i])).ToDictionary(p => p.c, p => p.v)))
            .ToList();

        var result = ParseValueTransform.Run(input.Columns, records, BuildSettings(row, input.Columns));

        Assert.Equal(int.Parse(row["rows_in"]), result.RowsIn);
        Assert.Equal(int.Parse(row["rows_out"]), result.RowsOut);
        Assert.Equal(int.Parse(row["rows_rejected"]), result.RowsRejected);
        Assert.Equal(int.Parse(row["rows_flagged"]), result.RowsFlagged);

        var expectedOutput = await ReadCsvAsync(row["expected_output"], ",", true, 0);
        Assert.Equal(expectedOutput.Columns, result.OutputColumns);
        Assert.Equal(expectedOutput.Rows.Count, result.Rows.Count);
        for (var i = 0; i < expectedOutput.Rows.Count; i++)
        {
            Assert.True(expectedOutput.Rows[i].SequenceEqual(result.Rows[i]),
                $"{testId} output row {i + 1}: expected [{string.Join(",", expectedOutput.Rows[i])}] got [{string.Join(",", result.Rows[i])}]");
        }

        Assert.Equal(await ReadIssuesAsync(row["expected_rejects"]), result.Rejects);
        Assert.Equal(await ReadIssuesAsync(row["expected_flagged"]), result.Flagged);
    }

    [Fact(Skip = "needs a valid ETLBox licence and a full-file reader")]
    public void ManifestRow_C10()
    {
    }

    private static ParseValueSettings BuildSettings(IReadOnlyDictionary<string, string> row, IReadOnlyList<string> fileColumns)
    {
        return new ParseValueSettings
        {
            SourceColumn = row["source_column"],
            DelimiterLevels = ParseLevels(row["delimiters"]),
            Trim = row["trim"] == "yes",
            DropEmpty = row["drop_empty"] == "yes",
            Slots = ParseSlots(row["slots"]),
            ExcessPolicy = row["excess_policy"] switch
            {
                "reject" => ExcessPolicy.Reject,
                "pair what fits" => ExcessPolicy.PairWhatFits,
                var other => throw new InvalidOperationException($"Unknown excess policy '{other}'.")
            },
            DuplicatePolicy = row["duplicate_policy"] switch
            {
                "allow" => DuplicatePolicy.Allow,
                "flag" => DuplicatePolicy.Flag,
                var other => throw new InvalidOperationException($"Unknown duplicate policy '{other}'.")
            },
            CarryColumns = row["carry_columns"] switch
            {
                "none" => CarryColumns.None,
                "*" => CarryColumns.All,
                var list => CarryColumns.Of(list.Split(',').Select(s => s.Trim()).ToArray())
            }
        };
    }

    // "L1=:(once);L2=|(all);L3=/(each)". The manifest does not name the Each column; C09's notes
    // ("splits each component into name and revision") and expected/C09_output.csv call it "revision".
    private static IReadOnlyList<DelimiterLevel> ParseLevels(string text)
    {
        return text.Split(';').Select(part =>
        {
            var m = Regex.Match(part, @"^L\d+=(.+)\((once|all|each)\)$");
            Assert.True(m.Success, $"Cannot read delimiter level '{part}'.");
            return m.Groups[2].Value switch
            {
                "once" => new DelimiterLevel(m.Groups[1].Value, SplitMode.Once),
                "all" => new DelimiterLevel(m.Groups[1].Value, SplitMode.All),
                _ => new DelimiterLevel(m.Groups[1].Value, SplitMode.Each, "revision")
            };
        }).ToList();
    }

    // "auto(r01..r18;3)" names r01, r02, ..., r18 by pattern (not by file position, see C08b);
    // "S1=m01,m02;S2=m03,..." is explicit.
    private static IReadOnlyList<Slot> ParseSlots(string text)
    {
        var auto = Regex.Match(text, @"^auto\((\D+)(\d+)\.\.(\D+)(\d+);(\d+)\)$");
        if (auto.Success)
        {
            var prefix = auto.Groups[1].Value;
            var width = auto.Groups[2].Value.Length;
            var first = int.Parse(auto.Groups[2].Value);
            var last = int.Parse(auto.Groups[4].Value);
            var names = Enumerable.Range(first, last - first + 1)
                .Select(n => prefix + n.ToString().PadLeft(width, '0'))
                .ToList();
            return ParseValueSettings.SlotsFromGroups(names, int.Parse(auto.Groups[5].Value));
        }

        return text.Split(';').Select(part =>
        {
            var pieces = part.Split('=', 2);
            return new Slot(pieces[0], pieces[1].Split(',').Select(s => s.Trim()).ToList());
        }).ToList();
    }

    private static async Task<IReadOnlyList<RecordIssue>> ReadIssuesAsync(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return Array.Empty<RecordIssue>();
        }

        var csv = await ReadCsvAsync(relativePath, ",", true, 0);
        var number = csv.Columns.ToList().IndexOf("record_number");
        var reason = csv.Columns.ToList().IndexOf("reason");
        return csv.Rows.Select(r => new RecordIssue(int.Parse(r[number]), r[reason])).ToList();
    }

    private static async Task<List<Dictionary<string, string>>> ReadManifestAsync()
    {
        var csv = await ReadCsvAsync("expected_results.csv", ",", true, 0);
        return csv.Rows
            .Select(r => csv.Columns.Select((c, i) => (c, v: r[i])).ToDictionary(p => p.c, p => p.v))
            .ToList();
    }

    private static async Task<CsvPreview> ReadCsvAsync(string relativePath, string separator, bool header, int skipLines)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SampleData", "TestData", "composite", relativePath);
        var settings = new CsvFileSettings(path, separator, header, skipLines, "utf-8", 1000);

        var preview = await TestTimeout.Run(() => new CsvPreviewReader().ReadAsync(settings, CancellationToken.None));

        Assert.Null(preview.Error);
        Assert.False(preview.HasMoreRows, $"{relativePath} has more than 1,000 records; the preview reader cannot read it all.");
        return preview;
    }
}
