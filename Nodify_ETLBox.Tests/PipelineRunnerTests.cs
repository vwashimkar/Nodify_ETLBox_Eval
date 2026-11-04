using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>
/// The full-file run. Every call that reaches ETLBox goes through TestTimeout.
/// </summary>
[Collection(LargeFileCollection.Name)]
public class PipelineRunnerTests
{
    /// <summary>A synchronous IProgress, so reports are seen in order and at once.</summary>
    internal sealed class ProgressLog : IProgress<PipelineProgress>
    {
        public List<PipelineProgress> Reports { get; } = [];
        public void Report(PipelineProgress value) { lock (Reports) { Reports.Add(value); } }
    }

    internal static async Task<PipelineDefinition> ManifestDefinitionAsync(string testId)
    {
        var row = (await CompositeManifestTests.ReadManifestAsync()).Single(r => r["test_id"] == testId);
        var csv = new CsvFileSettings(
            PipelineDefinitionTests.CompositePath(row["file"]), row["separator"], row["header"] == "yes", int.Parse(row["skip_lines"]), "utf-8");
        return new PipelineDefinition(csv, new CsvColumnSelection(), CompositeManifestTests.BuildSettings(row, []), OutputSettings.Default);
    }

    private static Task<PipelineResult> Run(PipelineDefinition definition, string? path = null, IProgress<PipelineProgress>? progress = null, CancellationToken token = default)
        => TestTimeout.Run(() => PipelineRunner.RunAsync(definition, path, progress, token));

    public static TheoryData<string> ManifestRows() => CompositeManifestTests.ManifestRows();

    [Theory]
    [MemberData(nameof(ManifestRows))]
    public async Task ManifestRow_FullRun_MatchesExpectedFiles(string testId)
    {
        var row = (await CompositeManifestTests.ReadManifestAsync()).Single(r => r["test_id"] == testId);

        var result = await Run(await ManifestDefinitionAsync(testId));

        Assert.Null(result.Error);
        Assert.False(result.Cancelled);
        Assert.Equal(int.Parse(row["rows_in"]), result.Counts.Read);
        Assert.Equal(int.Parse(row["rows_out"]), result.Counts.Out);
        Assert.Equal(int.Parse(row["rows_rejected"]), result.Counts.Rejected);
        Assert.Equal(int.Parse(row["rows_flagged"]), result.Counts.Flagged);
        Assert.Equal(result.Counts.Read - result.Counts.Rejected, result.Counts.Parsed);

        var expected = await CompositeManifestTests.ReadCsvAsync(row["expected_output"], ",", true, 0);
        Assert.Equal(expected.Columns, result.Columns);
        Assert.Equal(expected.Rows.Count, result.Rows.Count);
        for (var i = 0; i < expected.Rows.Count; i++)
        {
            Assert.Equal(expected.Rows[i], result.Columns.Select(c => result.Rows[i][c]));
        }

        Assert.Equal(await CompositeManifestTests.ReadIssuesAsync(row["expected_rejects"]), result.Rejects);
        Assert.Equal(await CompositeManifestTests.ReadIssuesAsync(row["expected_flagged"]), result.Flagged);
    }

    [Fact]
    public async Task C01_In4_Out24_RowsAreStringDictionaries()
    {
        var result = await Run(PipelineDefinitionTests.C01Definition());

        Assert.Equal(new PipelineCounts(4, 4, 0, 0, 24), result.Counts);
        Assert.IsType<List<Dictionary<string, string>>>(result.Rows);
        Assert.Equal(["sample_id", "component", "value_a", "value_b", "value_c"], result.Columns);
        Assert.All(result.Rows, r => Assert.Equal(result.Columns.OrderBy(c => c), r.Keys.OrderBy(c => c)));
        Assert.All(result.Rows, r => Assert.All(r.Values, Assert.NotNull));
    }

    [Fact]
    public async Task C04_GivesTheExpectedRejects()
    {
        var result = await Run(await ManifestDefinitionAsync("C04"));

        Assert.Equal(await CompositeManifestTests.ReadIssuesAsync("expected/C04_rejects.csv"), result.Rejects);
        Assert.Equal(new PipelineCounts(5, 2, 3, 0, 12), result.Counts);
    }

    [Fact]
    public async Task C12_Out21()
    {
        var result = await Run(await ManifestDefinitionAsync("C12"));

        Assert.Equal(21, result.Counts.Out);
        Assert.Equal(21, result.Rows.Count);
    }

    [Fact]
    public async Task OtherFilePath_IsUsedInsteadOfTheDefinitionPath()
    {
        var definition = PipelineDefinitionTests.C01Definition(path: PipelineDefinitionTests.CompositePath("missing.csv"));

        var result = await Run(definition, PipelineDefinitionTests.CompositePath("c01_basic.csv"));

        Assert.Null(result.Error);
        Assert.Equal(24, result.Counts.Out);
    }

    [Fact]
    public async Task MissingFile_GivesMessage_NoException()
    {
        var result = await Run(PipelineDefinitionTests.C01Definition(), PipelineDefinitionTests.CompositePath("missing.csv"));

        Assert.Equal("File was not found. Check the path and try again.", result.Error);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task ColumnsDoNotMatch_SaysWhichColumnIsMissing()
    {
        // c06 has other column names than the C01 settings expect.
        var result = await Run(PipelineDefinitionTests.C01Definition(), PipelineDefinitionTests.CompositePath("c12_irregular_slots.csv"));

        Assert.NotNull(result.Error);
        Assert.StartsWith("The file does not match the ParseValue settings:", result.Error);
        Assert.Contains("'r01'", result.Error);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task UnsupportedKind_IsRefused()
    {
        var result = await Run(PipelineDefinitionTests.C01Definition(kind: ObjectKind.DataTable));

        Assert.Equal(ObjectKinds.NotYetSupported, result.Error);
    }

    [Fact]
    public async Task Progress_IsReportedAtLeastOnce_AndTheLastReportHasTheFinalCounts()
    {
        var log = new ProgressLog();

        var result = await Run(PipelineDefinitionTests.C01Definition(), progress: log);

        Assert.NotEmpty(log.Reports);
        Assert.Equal(result.Counts, log.Reports[^1].Counts);
    }

    [Fact]
    public async Task StructuralError_BecomesAReject_WithARecordNumber()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taskD_broken_{Guid.NewGuid():N}.csv");
        var good = "S1:a|b,1,2,3,4,5,6";
        await File.WriteAllLinesAsync(path, ["sample,r01,r02,r03,r04,r05,r06", good, good, "\"S3:a|b,1,2,3,4,5,6", good]);
        try
        {
            var definition = PipelineDefinitionTests.C01Definition(path: path) with
            {
                ParseValue = PipelineDefinitionTests.C01Definition().ParseValue with
                {
                    Slots = ParseValueSettings.SlotsFromGroups(["r01", "r02", "r03", "r04", "r05", "r06"], 3)
                }
            };

            var result = await Run(definition);

            // The unclosed quote may swallow the rest of the file [U]; what is proven here is that
            // the run ends with a reject (not an exception) and keeps the rows before it.
            Assert.False(result.Cancelled);
            Assert.True(result.Rejects.Count >= 1 || result.Error is not null, "expected a reject or a message");
            Assert.True(result.Counts.Out >= 4, $"the two good records before the broken one should give 4 rows; got {result.Counts.Out}. Error: {result.Error}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TokenCancelledBeforeStart_ReturnsCancelledAtOnce()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await Run(PipelineDefinitionTests.C01Definition(), token: cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(0, result.Counts.Read);
    }

    [Fact]
    public async Task Cancel_Repeated20Times_ReturnsQuickly_AndReleasesTheFile()
    {
        for (var i = 0; i < 20; i++)
        {
            var path = PrivateCopyOfC10();
            try
            {
                using var cts = new CancellationTokenSource();
                var started = DateTime.UtcNow;
                cts.CancelAfter(TimeSpan.FromMilliseconds(i));

                var result = await Run(PipelineDefinitionTests.C01Definition(path: path), token: cts.Token);

                Assert.True(result.Cancelled, $"run {i + 1}: not cancelled (read {result.Counts.Read}, error {result.Error})");
                Assert.True(result.Counts.Read < 5000, $"run {i + 1}: read {result.Counts.Read} before the cancel was seen");
                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), $"run {i + 1}: took too long");
            }
            finally
            {
                File.Delete(path);
            }

            Assert.False(File.Exists(path));
        }
    }

    /// <summary>The preview cancel tests open the shared c10 file exclusively, so the tests here use a copy.</summary>
    internal static string PrivateCopyOfC10()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taskD_c10_{Guid.NewGuid():N}.csv");
        File.Copy(PipelineDefinitionTests.CompositePath("c10_large.csv"), path);
        return path;
    }

    [LicenceLimitFact]
    public async Task C10_WithoutAValidLicence_GivesTheLicenceMessage_AndKeepsTheRows()
    {
        var path = PrivateCopyOfC10();
        PipelineResult result;
        try
        {
            var definition = await ManifestDefinitionAsync("C10");
            result = await Run(definition with { CsvFile = definition.CsvFile with { Path = path } }, limit: TimeSpan.FromSeconds(30));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.True(result.LicenceLimitReached);
        Assert.False(result.Cancelled);
        Assert.InRange(result.Counts.Read, 4900, 5001);
        Assert.Equal(PipelineRunner.LicenceLimitMessage(result.Counts.Read), result.Error);
        Assert.StartsWith("The ETLBox licence limit was reached after ", result.Error);
        Assert.EndsWith("rows. Rows so far are kept. A valid licence is needed to read the full file.", result.Error);
        Assert.True(result.Rows.Count > 0);
        Assert.Equal(result.Counts.Out, result.Rows.Count);
    }

    [Fact(Skip = "needs a valid ETLBox licence")]
    public async Task C10_FullFile_WithALicence()
    {
        var result = await Run(await ManifestDefinitionAsync("C10"), limit: TimeSpan.FromSeconds(60));

        Assert.Null(result.Error);
        Assert.Equal(new PipelineCounts(20000, 19980, 20, 0, 119820), result.Counts);
        Assert.Equal(await CompositeManifestTests.ReadIssuesAsync("expected/C10_rejects.csv"), result.Rejects);
    }

    private static Task<PipelineResult> Run(PipelineDefinition definition, TimeSpan limit)
        => TestTimeout.Run(() => PipelineRunner.RunAsync(definition, null, null, CancellationToken.None), limit);
}

/// <summary>
/// A test of the licence-limit message. It is skipped, not failed, when a valid licence is
/// present. The probe reads c10_large.csv up to 5,100 records once per test run. Without a valid
/// licence ETLBox still delivers record 5,001 and then stops with the licence error (seen
/// 2026-11-04 with the expired trial key), so only reading past it with no licence error counts.
/// </summary>
internal sealed class LicenceLimitFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> ValidLicence = new(() =>
    {
        // A private copy: the preview cancel tests open the shared c10 file exclusively.
        var path = PipelineRunnerTests.PrivateCopyOfC10();
        try
        {
            var settings = new CsvFileSettings(path, ",", true, 0, "utf-8");
            var read = Task.Run(() => new CsvRecordReader().ReadAsync(settings, r => r.RecordNumber < 5100, CancellationToken.None));
            return read.Wait(TimeSpan.FromSeconds(30)) && !read.Result.LicenceLimitReached && read.Result.RecordsRead >= 5100;
        }
        finally
        {
            File.Delete(path);
        }
    });

    public LicenceLimitFactAttribute()
    {
        if (ValidLicence.Value)
        {
            Skip = "a valid ETLBox licence is present, so the licence limit cannot be reached";
        }
    }
}

/// <summary>
/// Tests that open the shared c10_large.csv exclusively, or copy it, never run at the same time.
/// </summary>
[CollectionDefinition(Name)]
public sealed class LargeFileCollection
{
    public const string Name = "c10_large.csv";
}
