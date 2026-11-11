using System.IO;
using System.Security.Cryptography;
using System.Text;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Intake;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.UI.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>
/// Shared pieces of the G6b tests: the i10 example, its variants made in temp, and a CSV part pipeline over
/// it. ParseValue for the i10 table: source column sample_id split once at "-" (key "S", one component),
/// one slot with the four other columns, so every record gives one long row.
/// </summary>
internal static class G6bParts
{
    public const string I10 = "i10_instrument_header_table_footer";

    public static string I10Path => IntakeTestData.PathOf(I10);

    public static string[] I10Lines => File.ReadAllText(I10Path).Split('\n');

    public static ParseValueSettings I10ParseValue() => new()
    {
        SourceColumn = "sample_id",
        DelimiterLevels = [new("-", SplitMode.Once), new("|", SplitMode.All)],
        Slots = [new Slot("S1", ["weight_g", "volume_ml", "chloride_pct", "result"])]
    };

    public static PipelineDefinition Definition(string exampleFile, CsvPartSelector selector)
        => PipelineDefinition.ForCsvPart(exampleFile, selector, new CsvColumnSelection(), I10ParseValue(), OutputSettings.Default);

    public static PipelineDefinition StandaloneDefinition(string csvPath)
        => new(new CsvFileSettings(csvPath, ",", true, 0, "utf-8"), new CsvColumnSelection(), I10ParseValue(), OutputSettings.Default);

    /// <summary>The i10 text changed as named; the original is only read.</summary>
    public static string Variant(string kind)
    {
        var lines = I10Lines.ToList();
        switch (kind)
        {
            case "example":
                break;
            case "more rows":
                lines.InsertRange(27, ["S-006,1.0001,12.11,4.30,PASS", "S-007,0.9990,12.50,4.41,PASS", "S-008,1.0200,13.90,4.81,FAIL"]);
                break;
            case "blank line before the table":
                lines.Insert(20, "");
                break;
            case "other title lines":
                lines[4] = "Operator: B. Rao";
                lines[5] = "Date: 2026-12-01";
                lines[19] = "Comment: second run of the week";
                break;
            case "a reject":
                lines.Insert(27, "S,1.0001,12.11,4.30,PASS");
                break;
            case "table missing":
                lines.RemoveRange(21, 7);
                break;
            case "three copies":
                var table = lines.GetRange(21, 6);
                lines.InsertRange(27, ["", .. table, "", .. table]);
                break;
            case "one column more":
                for (var i = 21; i < 27; i++)
                {
                    lines[i] += i == 21 ? ",extra" : ",x";
                }

                break;
            case "a column renamed":
                lines[21] = lines[21].Replace("result", "outcome");
                break;
            case "many rows":
                lines.InsertRange(27, Enumerable.Range(6, 4_000).Select(n => $"S-{n:0000},1.{n % 10}001,12.{n % 7}1,4.{n % 9}0,PASS"));
                break;
            default:
                throw new ArgumentException(kind);
        }

        return string.Join("\n", lines);
    }

    /// <summary>The table of a variant (header line to the line before the next blank line) as its own CSV text.</summary>
    public static string TableOf(string text)
    {
        var lines = text.Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("sample_id,", StringComparison.Ordinal));
        var end = Array.FindIndex(lines, start, l => l.Length == 0);
        return string.Join("\n", lines[start..end]);
    }

    public static async Task<CsvPartSelector> I10SelectorAsync() => await CsvPartLocatorTests.SelectorAsync(I10Path);

    public static Task<PipelineResult> Run(PipelineDefinition definition, string? path, CancellationToken token = default, IProgress<PipelineProgress>? progress = null)
        => TestTimeout.Run(() => PipelineRunner.RunAsync(definition, path, progress, token));

    public static string Write(string folder, string name, string text, Encoding? encoding = null)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        return path;
    }

    public static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static IEnumerable<string> RowTexts(IReadOnlyList<string> columns, IEnumerable<Dictionary<string, string>> rows)
        => rows.Select(r => string.Join("|", columns.Select(c => r[c])));
}

/// <summary>Task G6b step 1: <see cref="PipelineDefinition.CsvPart"/>, its validator, JSON and hash, and the runner's part path.</summary>
public sealed class PipelineRunnerCsvPartTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _folder = Directory.CreateTempSubdirectory("g6b_run_").FullName;

    public PipelineRunnerCsvPartTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    [Fact]
    public void CsvFileDefinition_JsonAndHash_AreByteIdenticalToTheJsonWrittenBeforeG6b()
    {
        // TestData/pipeline_c01_before_g6b.json is the definition JSON a DLL generated on 2026-11-04 baked in (its README).
        var before = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "pipeline_c01_before_g6b.json")).ReplaceLineEndings("\n").TrimEnd();
        var path = @"C:\codes\Nodify_ETLBox_Eval\Nodify_ETLBox.Tests\bin\Debug\net10.0-windows\SampleData\TestData\composite\c01_basic.csv";
        var definition = PipelineDefinitionTests.C01Definition(path: path);

        var now = SettingsJson.Serialize(definition).ReplaceLineEndings("\n");
        Assert.Equal(before, now);
        Assert.Equal(before, SettingsJson.Serialize(SettingsJson.DeserializePipelineDefinition(before)).ReplaceLineEndings("\n"));
        Assert.DoesNotContain("csvPart", now);
        Assert.Null(SettingsJson.DeserializePipelineDefinition(before).CsvPart);
        Assert.Equal(MainViewModel.PipelineHash(definition), MainViewModel.PipelineHash(SettingsJson.DeserializePipelineDefinition(before)));
    }

    [Fact]
    public async Task CsvPartDefinition_RoundTripsThroughJson_AndTheHashChangesWithTheSelector()
    {
        var selector = await G6bParts.I10SelectorAsync();
        var definition = G6bParts.Definition(G6bParts.I10Path, selector);

        var json = SettingsJson.Serialize(definition);
        var back = SettingsJson.DeserializePipelineDefinition(json);
        Assert.Contains("\"csvPart\"", json);
        Assert.Equal(definition, back);
        Assert.Equal(json, SettingsJson.Serialize(back));
        Assert.Equal(MainViewModel.PipelineHash(definition), MainViewModel.PipelineHash(back));
        Assert.Equal(G6bParts.I10Path, back.CsvFile.Path);

        var renamed = definition with { CsvPart = selector with { Expected = selector.Expected with { ColumnNames = ["a", "b", "c", "d", "e"] } } };
        var otherPart = definition with { CsvPart = selector with { PartIndex = 0 } };
        Assert.NotEqual(definition, renamed);
        Assert.NotEqual(MainViewModel.PipelineHash(definition), MainViewModel.PipelineHash(renamed));
        Assert.NotEqual(MainViewModel.PipelineHash(definition), MainViewModel.PipelineHash(otherPart));
        Assert.NotEqual(definition, definition with { CsvPart = null });
    }

    [Fact]
    public async Task Validator_AcceptsTheCsvPartChain_AndGivesPlainMessagesForAnIncompleteSelector()
    {
        var selector = await G6bParts.I10SelectorAsync();
        var definition = G6bParts.Definition(G6bParts.I10Path, selector);

        Assert.Empty(PipelineDefinitionValidator.Validate(definition));
        Assert.Empty(PipelineDefinitionValidator.Validate(definition, graphChain: ["CsvPart", "ParseValue", "DotNetObject"]));
        Assert.Empty(PipelineDefinitionValidator.Validate(definition, graphChain: ["FileIntake", "CsvPart", "ParseValue", "DotNetObject"]));
        Assert.Equal([PipelineDefinitionValidator.WrongPartShape], PipelineDefinitionValidator.Validate(definition, graphChain: ["CsvFile", "ParseValue", "DotNetObject"]));

        string[] One(CsvPartSelector s, string? example = null) => PipelineDefinitionValidator.Validate(G6bParts.Definition(example ?? G6bParts.I10Path, s)).ToArray();
        Assert.Equal(["CSV part: The chosen part is not in the stored layout; choose the part in Intake Studio again."], One(selector with { PartIndex = 9 }));
        Assert.Equal(["CSV part: The chosen part is not a table; choose a CSV or TSV part in Intake Studio."], One(selector with { PartIndex = 0 }));
        Assert.Equal(["CSV part: The expected columns of the part are missing; choose the part in Intake Studio again."],
            One(selector with { Expected = selector.Expected with { ColumnCount = 0, ColumnNames = [] } }));
        Assert.Equal(["CSV part: The part has no delimiter; set its delimiter in Intake Studio first."], One(selector with { Expected = selector.Expected with { Delimiter = "" } }));
        Assert.Equal(["CSV part: The example file of the part is not known; choose the part in Intake Studio again."], One(selector with { ExampleSha256 = "" }));
        Assert.Equal(["CSV part: No example file is set; choose the file in the File intake node."], One(selector, example: " "));
        Assert.Equal(["CSV part: No layout is stored for this part; choose the part in Intake Studio again."],
            One(selector with { Template = selector.Template with { Parts = [] } }));
        Assert.Equal(["CSV part: " + CsvPartSelectorJson.NewerMessage], One(selector with { FormatVersion = CsvPartSelector.CurrentFormatVersion + 1 }));
    }

    [Theory]
    [InlineData("example")]
    [InlineData("more rows")]
    [InlineData("blank line before the table")]
    [InlineData("other title lines")]
    public async Task PartRun_GivesTheSameResult_AsTheTableCutOutAsAStandaloneCsv(string kind)
    {
        var selector = await G6bParts.I10SelectorAsync();
        var text = G6bParts.Variant(kind);
        var path = kind == "example" ? G6bParts.I10Path : G6bParts.Write(_folder, "variant.txt", text);
        var standalone = G6bParts.Write(_folder, "table.csv", G6bParts.TableOf(text));

        var part = await G6bParts.Run(G6bParts.Definition(G6bParts.I10Path, selector), path);
        var whole = await G6bParts.Run(G6bParts.StandaloneDefinition(standalone), null);

        Assert.Null(part.Error);
        Assert.Null(whole.Error);
        Assert.False(part.Cancelled);
        Assert.Equal(whole.Columns, part.Columns);
        Assert.Equal(G6bParts.RowTexts(whole.Columns, whole.Rows), G6bParts.RowTexts(part.Columns, part.Rows));
        Assert.Equal(whole.Counts, part.Counts);
        Assert.Empty(part.Rejects);
        Assert.Empty(part.Flagged);
        Assert.Equal(TableRows(text), part.Counts.Read);
        Assert.True(part.Rows.Count > 0);

        var facts = Assert.IsType<CsvPartRunFacts>(part.CsvPart);
        Assert.Equal(Path.GetFileName(path), facts.FileName);
        Assert.Equal(G6bParts.Sha(path), facts.Sha256, ignoreCase: true);
        Assert.Equal(selector.Template.Id, facts.TemplateId);
        Assert.Equal(selector.Template.Name, facts.TemplateName);
        Assert.NotNull(facts.Similarity);
        Assert.Contains(facts.Match, new[] { "Exact", "Close" });
        Assert.Equal(["sample_id", "weight_g", "volume_ml", "chloride_pct", "result"], facts.ColumnNames);
        var first = Array.FindIndex(text.Split('\n'), l => l.StartsWith("sample_id,", StringComparison.Ordinal)) + 1;
        Assert.Equal(first, facts.FirstLine);
        Assert.Equal(first + TableRows(text), facts.LastLine);
        Assert.Null(whole.CsvPart);
        _output.WriteLine($"{kind}: part {facts.PartNumber}, lines {facts.FirstLine} to {facts.LastLine}, {facts.Match} {facts.Similarity}, {part.Counts}");
    }

    private static int TableRows(string text) => G6bParts.TableOf(text).Split('\n').Length - 1;

    [Fact]
    public async Task PartRun_RejectReasons_CarryTheFileLine()
    {
        var selector = await G6bParts.I10SelectorAsync();
        var path = G6bParts.Write(_folder, "reject.txt", G6bParts.Variant("a reject"));

        var result = await G6bParts.Run(G6bParts.Definition(G6bParts.I10Path, selector), path);

        Assert.Null(result.Error);
        var reject = Assert.Single(result.Rejects);
        _output.WriteLine($"{reject.RecordNumber}: {reject.Reason}");
        Assert.Equal(6, reject.RecordNumber);
        Assert.EndsWith("(file line 28)", reject.Reason);
    }

    public static TheoryData<string, CsvPartFailure> Failures() => new()
    {
        { "file missing", CsvPartFailure.FileMissing },
        { "i09", CsvPartFailure.NoMatch },
        { "table missing", CsvPartFailure.PartNotPlaced },
        { "three copies", CsvPartFailure.SeveralParts },
        { "one column more", CsvPartFailure.ColumnCountDiffers },
        { "a column renamed", CsvPartFailure.ColumnNamesDiffer },
        { "delimiter tampered", CsvPartFailure.DelimiterDiffers },
        { "example moved", CsvPartFailure.ExampleMoved }
    };

    /// <summary>The file and selector of a failure case; the original files are only read.</summary>
    internal static (string Path, CsvPartSelector Selector) FailureCase(string kind, CsvPartSelector selector, string folder) => kind switch
    {
        "file missing" => (Path.Combine(folder, "missing.txt"), selector),
        "i09" => (IntakeTestData.PathOf("i09_sample_nomarkers"), selector),
        "delimiter tampered" => (G6bParts.I10Path, selector with { Expected = selector.Expected with { Delimiter = ";" } }),
        "example moved" => (G6bParts.I10Path, selector with { ExampleFirstLine = selector.ExampleFirstLine - 1 }),
        _ => (G6bParts.Write(folder, kind.Replace(' ', '_') + ".txt", G6bParts.Variant(kind)), selector)
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task EachLocatorFailure_IsTheRunFailure_WithTheLocatorSentence(string kind, CsvPartFailure reason)
    {
        var (path, selector) = FailureCase(kind, await G6bParts.I10SelectorAsync(), _folder);
        var location = await CsvPartLocator.LocateAsync(path, selector, CancellationToken.None);

        var result = await G6bParts.Run(G6bParts.Definition(G6bParts.I10Path, selector), path);

        _output.WriteLine($"{kind}: {result.Error}");
        Assert.Equal(reason, location.Reason);
        Assert.Equal(location.Message, result.Error);
        Assert.False(result.Cancelled);
        Assert.False(result.LicenceLimitReached);
        Assert.Empty(result.Rows);
        Assert.Empty(result.Columns);
        Assert.Equal(new PipelineCounts(0, 0, 0, 0, 0), result.Counts);
        Assert.Equal(Path.GetFileName(path), result.CsvPart!.FileName);
    }

    [Fact]
    public async Task Cancel_DuringTheScan_ReturnsPromptly_WithTheCancelledShape()
    {
        var selector = await G6bParts.I10SelectorAsync();
        var text = G6bParts.Variant("example") + "\n" + string.Concat(Enumerable.Repeat("Some long text that the scan has to read line by line.\n", 150_000));
        var path = G6bParts.Write(_folder, "big.txt", text);

        var cancelledInScan = false;
        for (var delay = 2; delay <= 256 && !cancelledInScan; delay *= 2)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(delay));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = await G6bParts.Run(G6bParts.Definition(G6bParts.I10Path, selector), path, cts.Token);
            _output.WriteLine($"delay {delay} ms: cancelled {result.Cancelled}, error {result.Error}, read {result.Counts.Read}, {clock.ElapsedMilliseconds} ms");
            Assert.True(result.Cancelled || result.Error is not null, "a scan cancelled this early cannot succeed");
            if (result.Cancelled)
            {
                Assert.Null(result.Error);
                Assert.Equal(0, result.Counts.Read);
                Assert.True(clock.ElapsedMilliseconds < 5_000, $"the cancel took {clock.ElapsedMilliseconds} ms");
                cancelledInScan = true;
            }
        }

        Assert.True(cancelledInScan);
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    /// <summary>Characters added to each data row of the cancel test (about 4 MB for 4,005 rows: about 0.5 s alone, 2 to 3 s on a busy CPU).</summary>
    private const int WideRowPadding = 1_000;

    [Fact]
    public async Task Cancel_DuringTheRead_ReturnsPromptly_AndReleasesTheFile()
    {
        var selector = await G6bParts.I10SelectorAsync();
        // G6d: no timer. With short rows the 4,005-row read took only 5 to 15 ms and the old doubling cancel
        // delays (5, 10, ... 1,280 ms) often jumped over it (4 of 6 runs alone failed). The runner's progress
        // clock starts before the part search and the first row after 250 ms of run time is always reported,
        // so cancelling from the progress callback lands while rows are read, on any machine, as long as the run
        // lasts longer than 250 ms. Wide rows (a long 'result' value, about 4 MB) make sure it does; still below
        // 5,000 rows (licence) and 16 MB (validation limit).
        var text = string.Join("\n", G6bParts.Variant("many rows").Split('\n')
            .Select(l => l.StartsWith("S-", StringComparison.Ordinal) ? l + new string('x', WideRowPadding) : l));
        var definition = G6bParts.Definition(G6bParts.I10Path, selector);
        var path = G6bParts.Write(_folder, "many_wide.txt", text);

        using var cts = new CancellationTokenSource();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long cancelledAt = -1;
        var progress = new SyncProgress<PipelineProgress>(p =>
        {
            if (p.Counts.Read > 0 && !cts.IsCancellationRequested)
            {
                cancelledAt = clock.ElapsedMilliseconds;
                cts.Cancel();
            }
        });
        // 30 s, not the default 10 s: with 12 MB the part search alone took up to 7.3 s on a fully busy CPU (G6d).
        // A real hang still fails here.
        var result = await TestTimeout.Run(() => PipelineRunner.RunAsync(definition, path, progress, cts.Token), TimeSpan.FromSeconds(30));
        var returnedAt = clock.ElapsedMilliseconds;
        _output.WriteLine($"File of {text.Length:N0} characters: cancelled {result.Cancelled} at {cancelledAt} ms, read {result.Counts.Read}, returned at {returnedAt} ms, error {result.Error}");

        Assert.True(result.Cancelled, $"the run was not cancelled (read {result.Counts.Read}, error {result.Error})");
        Assert.Null(result.Error);
        Assert.InRange(result.Counts.Read, 1, 4_004);
        Assert.True(returnedAt - cancelledAt < 2_000, $"the run returned {returnedAt - cancelledAt} ms after the cancel");
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task TheOriginalFile_IsUnchanged_AndDeletableRightAfterTheRun()
    {
        var selector = await G6bParts.I10SelectorAsync();
        var path = G6bParts.Write(_folder, "copy.txt", G6bParts.Variant("more rows"));
        var sha = G6bParts.Sha(path);
        var written = File.GetLastWriteTimeUtc(path);

        var result = await G6bParts.Run(G6bParts.Definition(G6bParts.I10Path, selector), path);

        Assert.Null(result.Error);
        Assert.Equal(sha, G6bParts.Sha(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        File.Delete(path);
        Assert.False(File.Exists(path));
    }
}

/// <summary>A progress that calls back on the reporting thread (Progress&lt;T&gt; posts to the thread pool).</summary>
internal sealed class SyncProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
