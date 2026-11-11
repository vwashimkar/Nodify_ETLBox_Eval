using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Intake;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>
/// Task G6a step 4: <see cref="CsvPartSelector"/> and <see cref="CsvPartLocator"/>. The sample files
/// are only read; every changed file is a temp copy. ETLBox reads stay far below 5,000 rows.
/// </summary>
public sealed class CsvPartLocatorTests : IDisposable
{
    private const string I10 = "i10_instrument_header_table_footer";

    private readonly ITestOutputHelper _output;
    private readonly string _folder = Directory.CreateTempSubdirectory("g6a_locate_").FullName;

    public CsvPartLocatorTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private static string I10Text => File.ReadAllText(IntakeTestData.PathOf(I10));

    private string Temp(string name, string text, Encoding? encoding = null)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        return path;
    }

    /// <summary>The selector of the first csv part of a file (Auto-detect, encoding auto).</summary>
    internal static async Task<CsvPartSelector> SelectorAsync(string path, int nthTable = 0, IntakeMode mode = IntakeMode.AutoDetect)
    {
        var item = new FileIntakeFactory().CreateItem(path);
        var scan = await new IntakeScanService(new IntakeSettings { Mode = mode }).ScanAsync(item, null, CancellationToken.None);
        var session = new IntakeEditSession(item, scan, null, "test");
        var index = Enumerable.Range(0, session.Containers.Count).Where(i => session.Containers[i].Format is "csv" or "tsv").ElementAt(nthTable);
        if (session.Containers[index].Delimiter is null)
        {
            // Marker segments are not detected, so they have no delimiter: set it as a user would.
            Assert.True(session.SetTableOptions(index, delimiter: "comma").Ok);
        }

        var selector = CsvPartSelector.FromSession(session, index, out var message);
        Assert.True(selector is not null, message);
        return selector;
    }

    private async Task<CsvPartLocation> LocateAsync(string path, CsvPartSelector selector)
    {
        var location = await CsvPartLocator.LocateAsync(path, selector, CancellationToken.None);
        _output.WriteLine($"{Path.GetFileName(path)}: {location.Reason}: {location.Message} (similarity {location.Similarity}, {location.Match}, {location.ElapsedMs} ms)");
        return location;
    }

    private static async Task<List<string>> RowsAsync(CsvPartSource part)
    {
        var (outcome, records) = await CsvPartReaderTests.ReadPart(part);
        Assert.Null(outcome.SettingsError);
        Assert.Empty(outcome.StructuralErrors);
        return records.Select(r => string.Join("|", r.Values)).ToList();
    }

    // (1) ------------------------------------------------------------------------------------------

    [Fact]
    public async Task I10_LocateOnTheExample_PassesTheSelfCheck_AndReadsLikeTheTableAsAStandaloneCsv()
    {
        var path = IntakeTestData.PathOf(I10);
        var selector = await SelectorAsync(path);
        Assert.Equal(["sample_id", "weight_g", "volume_ml", "chloride_pct", "result"], selector.Expected.ColumnNames);
        Assert.Equal((22L, 27L), (selector.ExampleFirstLine, selector.ExampleLastLine));

        var location = await LocateAsync(path, selector);
        Assert.True(location.Ok, location.Message);
        Assert.Equal(TemplateMatch.Exact, location.Match);
        Assert.Equal(I10 + ".txt", location.FileName);
        Assert.Equal((22L, 27L), (location.FirstLine, location.LastLine));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), location.Sha256, ignoreCase: true);

        var standalone = Temp("table.csv", string.Join("\n", I10Text.Split('\n')[21..27]));
        var (pathOutcome, pathRecords) = await CsvPartReaderTests.ReadPath(new CsvFileSettings(standalone, ",", true, 0, "utf-8"));
        var (partOutcome, partRecords) = await CsvPartReaderTests.ReadPart(location.Part!);
        Assert.Equal(pathOutcome.Columns, partOutcome.Columns);
        Assert.Equal(5, partRecords.Count);
        Assert.Equal(pathRecords.Select(r => string.Join("|", r.Values)), partRecords.Select(r => string.Join("|", r.Values)));
        Assert.Equal([23L, 24L, 25L, 26L, 27L], partRecords.Select(r => r.FileLine!.Value));
    }

    // (2) ------------------------------------------------------------------------------------------

    public static TheoryData<string> Variants() => ["more rows", "blank line before the table", "other header text", "three footer lines", "one footer line"];

    private static string Variant(string kind)
    {
        var lines = I10Text.Split('\n').ToList();
        switch (kind)
        {
            case "more rows":
                lines.InsertRange(27, ["S-006,1.0001,12.11,4.30,PASS", "S-007,0.9990,12.50,4.41,PASS", "S-008,1.0200,13.90,4.81,FAIL"]);
                break;
            case "blank line before the table":
                lines.Insert(20, "");
                break;
            case "other header text":
                lines[4] = "Operator: B. Rao";
                lines[5] = "Date: 2026-12-01";
                lines[19] = "Comment: second run of the week";
                break;
            case "three footer lines":
                lines.Insert(30, "Page 1 of 1");
                break;
            case "one footer line":
                lines.RemoveAt(29);
                break;
        }

        return string.Join("\n", lines);
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task I10Variants_AreFound_AndTheirTableRowsRead(string kind)
    {
        var selector = await SelectorAsync(IntakeTestData.PathOf(I10));
        var text = Variant(kind);
        var path = Temp("variant.txt", text);
        var location = await LocateAsync(path, selector);
        Assert.True(location.Ok, location.Message);

        var rows = await RowsAsync(location.Part!);
        var expected = text.Split('\n').Where(l => l.StartsWith("S-0", StringComparison.Ordinal)).Select(l => l.Replace(',', '|')).ToList();
        Assert.Equal(expected, rows);
    }

    // (3) ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Failures_GiveTheirReasonAndOnePlainSentence()
    {
        var selector = await SelectorAsync(IntakeTestData.PathOf(I10));
        var lines = I10Text.Split('\n');
        string Table(Func<string, string> change) => string.Join("\n", lines.Select((l, i) => i is >= 21 and <= 26 ? change(l) : l));

        var semicolon = await LocateAsync(Temp("semicolon.txt", Table(l => l.Replace(',', ';'))), selector);
        var oneMore = await LocateAsync(Temp("onemore.txt", Table(l => l + (l.StartsWith("sample_id") ? ",note" : ",x"))), selector);
        var renamed = await LocateAsync(Temp("renamed.txt", Table(l => l.Replace("result", "outcome"))), selector);
        var missing = await LocateAsync(Temp("notable.txt", string.Join("\n", lines.Where((_, i) => i is < 21 or > 27))), selector);
        var other = await LocateAsync(IntakeTestData.PathOf("i09_sample_nomarkers"), selector);
        var gone = await LocateAsync(Path.Combine(_folder, "nothere.txt"), selector);

        var all = new[] { semicolon, oneMore, renamed, missing, other, gone };
        Assert.All(all, l => Assert.False(l.Ok));
        Assert.All(all, l => Assert.Null(l.Part));
        Assert.All(all, l => Assert.DoesNotContain(_folder, l.Message));
        Assert.All(all, l => Assert.EndsWith(".", l.Message));

        Assert.Equal(CsvPartFailure.ColumnNamesDiffer, renamed.Reason);
        Assert.Equal("The column names of 'csv-1' differ: expected sample_id, weight_g, volume_ml, chloride_pct, result; found sample_id, weight_g, volume_ml, chloride_pct, outcome.", renamed.Message);
        Assert.Equal(CsvPartFailure.FileMissing, gone.Reason);
        Assert.Equal(CsvPartLocator.FileMissingMessage, gone.Message);
        Assert.Equal(CsvPartFailure.NoMatch, other.Reason);
        Assert.Matches(@"^This file does not look like the example \(similarity 0\.\d\d; at least 0\.80 is needed\); check that it comes from the same instrument\.$", other.Message);
        Assert.Equal(CsvPartFailure.NoMatch, semicolon.Reason);
        Assert.Equal("This file does not look like the example (similarity 0.67; at least 0.80 is needed); check that it comes from the same instrument.", semicolon.Message);
        Assert.Equal(CsvPartFailure.ColumnCountDiffers, oneMore.Reason);
        Assert.Equal("The CSV part 'csv-1' has 6 columns in this file, but the pipeline expects 5.", oneMore.Message);
        Assert.Equal(CsvPartFailure.PartNotPlaced, missing.Reason);
        Assert.Equal("The CSV part 'csv-1' was not found in this file (no csv part found).", missing.Message);
    }

    [Fact]
    public async Task AFileOpenForWritingByAnotherHandle_IsReportedAsLocked()
    {
        var selector = await SelectorAsync(IntakeTestData.PathOf(I10));
        var path = Temp("locked.txt", I10Text);
        CsvPartLocation location;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            location = await LocateAsync(path, selector);
        }

        Assert.Equal(CsvPartFailure.FileLocked, location.Reason);
        Assert.Equal(CsvPartLocator.FileLockedMessage, location.Message);
        Assert.True((await LocateAsync(path, selector)).Ok);
    }

    // (4) ------------------------------------------------------------------------------------------

    private static string ThreeTables()
    {
        var lines = I10Text.Split('\n');
        var table = string.Join("\n", lines[21..27]);
        return string.Join("\n", lines[..21]) + "\n" + table + "\n\n" + table + "\n\n" + table + "\n\n" + string.Join("\n", lines[28..]);
    }

    [Fact]
    public async Task ThreeCopiesOfTheTable_FailWithTheSeveralPartsMessage()
    {
        var three = Temp("three.txt", ThreeTables());
        var fromThree = await SelectorAsync(three);
        var location = await LocateAsync(three, fromThree);
        Assert.Equal(CsvPartFailure.SeveralParts, location.Reason);
        Assert.Equal("This file has 3 CSV parts like 'csv-1'; the pipeline reads one.", location.Message);

        var fromOnce = await SelectorAsync(IntakeTestData.PathOf(I10));
        var again = await LocateAsync(three, fromOnce);
        Assert.False(again.Ok);
        Assert.Equal(CsvPartFailure.SeveralParts, again.Reason);
        Assert.Equal(location.Message, again.Message);
    }

    // (5) ------------------------------------------------------------------------------------------

    [Fact]
    public async Task SelfCheck_AppliesOnlyToTheExampleFile()
    {
        var selector = await SelectorAsync(IntakeTestData.PathOf(I10));
        var tampered = selector with { ExampleFirstLine = selector.ExampleFirstLine + 1 };
        var onExample = await LocateAsync(IntakeTestData.PathOf(I10), tampered);
        Assert.Equal(CsvPartFailure.ExampleMoved, onExample.Reason);
        Assert.Equal(CsvPartLocator.ExampleMovedMessage, onExample.Message);

        var onOther = await LocateAsync(Temp("other.txt", Variant("more rows")), tampered);
        Assert.True(onOther.Ok, onOther.Message);
    }

    // (6) ------------------------------------------------------------------------------------------

    [Fact]
    public async Task R09Selector_IsRefusedOnR12_AndFoundOnR09()
    {
        var r09 = Path.Combine(IntakeTestData.Folder, "real", "r09_qiacuity_dpcr.csv");
        var r12 = Path.Combine(IntakeTestData.Folder, "real", "r12_msd_workbench.csv");
        var selector = await SelectorAsync(r09);
        var onR12 = await LocateAsync(r12, selector);
        Assert.False(onR12.Ok);
        Assert.Contains(onR12.Reason, new[] { CsvPartFailure.NoMatch, CsvPartFailure.ColumnNamesDiffer, CsvPartFailure.ColumnCountDiffers, CsvPartFailure.PartNotPlaced });
        var onR09 = await LocateAsync(r09, selector);
        Assert.True(onR09.Ok, onR09.Message);
    }

    // (7) ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    [InlineData("utf-8-bom")]
    public async Task Utf16AndBomFiles_GiveTheSameRowsAsUtf8(string kind)
    {
        Encoding encoding = kind switch { "utf-16le" => new UnicodeEncoding(false, true), "utf-16be" => new UnicodeEncoding(true, true), _ => new UTF8Encoding(true) };
        var text = Variant("more rows").Replace("S-001", "S-001-Ä");
        var plain = Temp("plain.txt", text);
        var coded = Temp("coded.txt", text, encoding);
        var selector = await SelectorAsync(plain);

        var a = await LocateAsync(plain, selector);
        var b = await LocateAsync(coded, selector);
        Assert.True(b.Ok, b.Message);
        Assert.Equal(a.FirstLine, b.FirstLine);
        Assert.Equal(await RowsAsync(a.Part!), await RowsAsync(b.Part!));
        Assert.Contains("S-001-Ä|1.0021|12.31|4.35|PASS", await RowsAsync(b.Part!));
    }

    // (8) ------------------------------------------------------------------------------------------

    [Fact]
    public void ALargePart_StreamsWithFlatMemory()
    {
        var slow = Environment.GetEnvironmentVariable("INTAKE_SLOW_TESTS") == "1";
        var targetBytes = slow ? 100L * 1024 * 1024 : 2L * 1024 * 1024;
        var path = Path.Combine(_folder, "large.txt");
        const string head = "Instrument: X\nOperator: Y\n\n";
        long rows = 0;
        using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            writer.Write(head);
            writer.Write("id,value,note\n");
            while (writer.BaseStream.Length < targetBytes)
            {
                writer.Write($"{++rows},{rows * 0.5},row number {rows}\n");
            }

            writer.Write("\nEnd of report\n");
        }

        var start = Encoding.UTF8.GetByteCount(head);
        var length = new FileInfo(path).Length - start - "\nEnd of report\n".Length - 1;
        var before = LiveMemory.Bytes();
        var clock = Stopwatch.StartNew();
        long lines = 0;
        using (var reader = new StreamReader(new PartStream(path, start, length), new UTF8Encoding(false), false))
        {
            while (reader.ReadLine() is not null)
            {
                lines++;
            }
        }

        clock.Stop();
        var after = LiveMemory.Bytes();
        _output.WriteLine($"Part of {length:N0} bytes, {lines:N0} lines in {clock.ElapsedMilliseconds} ms; live bytes before {before:N0}, after {after:N0}");
        Assert.Equal(rows + 1, lines);
        Assert.True(Math.Abs(after - before) < 5 * 1024 * 1024, $"live bytes grew from {before:N0} to {after:N0}");
        File.Delete(path);
    }

    // (9) ------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheOriginalFile_IsUnchanged_AndCanBeDeletedRightAfter()
    {
        var path = Temp("original.txt", I10Text);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        var written = File.GetLastWriteTimeUtc(path);
        var selector = await SelectorAsync(path);

        var location = await LocateAsync(path, selector);
        Assert.True(location.Ok, location.Message);
        Assert.Equal(5, (await RowsAsync(location.Part!)).Count);

        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    // (10) -----------------------------------------------------------------------------------------

    [Fact]
    public async Task SelectorJson_RoundTrips_AndBadOrNewerJsonGivesThePlainMessage()
    {
        var selector = await SelectorAsync(IntakeTestData.PathOf(I10));
        var json = CsvPartSelectorJson.Serialize(selector);
        var back = CsvPartSelectorJson.Deserialize(json, out var error);
        Assert.Null(error);
        Assert.NotNull(back);
        Assert.Equal(json, CsvPartSelectorJson.Serialize(back));
        Assert.Equal(IntakeTemplateJson.Serialize(selector.Template), IntakeTemplateJson.Serialize(back.Template));
        Assert.Equal(selector.Expected.ColumnNames, back.Expected.ColumnNames);
        Assert.True((await LocateAsync(IntakeTestData.PathOf(I10), back)).Ok);

        Assert.Null(CsvPartSelectorJson.Deserialize("{ not json", out error));
        Assert.Equal(CsvPartSelectorJson.UnreadableMessage, error);
        Assert.Null(CsvPartSelectorJson.Deserialize(json.Replace("\"partIndex\":1", "\"partIndex\":99"), out error));
        Assert.Equal(CsvPartSelectorJson.UnreadableMessage, error);
        Assert.Null(CsvPartSelectorJson.Deserialize(json.Replace("\"formatVersion\":1,\"mode\"", "\"formatVersion\":2,\"mode\""), out error));
        Assert.Equal(CsvPartSelectorJson.NewerMessage, error);
    }

    [Fact]
    public async Task FromSession_RefusesAPartThatIsNotATable()
    {
        var item = IntakeTestData.Item(I10);
        var scan = await new IntakeScanService().ScanAsync(item, null, CancellationToken.None);
        var session = new IntakeEditSession(item, scan, null, "test");
        Assert.Null(CsvPartSelector.FromSession(session, 0, out var message));
        Assert.Equal("Part 1 is not a CSV or TSV table, so a pipeline cannot read it; choose a table or change its format first.", message);
        Assert.Null(CsvPartSelector.FromSession(session, 99, out message));
        Assert.Equal("Choose a part of the file first.", message);
    }

    // (11) -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_DuringTheScanOfABigFile_ReturnsPromptlyWithAPlainResult()
    {
        var selector = await SelectorAsync(IntakeTestData.PathOf(I10));
        var path = Path.Combine(_folder, "big.txt");
        var copy = File.ReadAllText(IntakeTestData.PathOf("i01_sample"));
        using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            for (var i = 0; i < 2000; i++)
            {
                writer.Write(copy);
            }
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var clock = Stopwatch.StartNew();
        var location = await TestTimeout.Run(() => CsvPartLocator.LocateAsync(path, selector, cts.Token));
        _output.WriteLine($"{location.Reason} after {clock.ElapsedMilliseconds} ms: {location.Message}");
        Assert.Equal(CsvPartFailure.Cancelled, location.Reason);
        Assert.Equal(CsvPartLocator.CancelledMessage, location.Message);
        Assert.True(clock.ElapsedMilliseconds < 5000);
        File.Delete(path);
    }

    // Timing ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Timing_I01AndI10()
    {
        foreach (var (name, mode) in new[] { ("i01_sample", IntakeMode.AutoDetect), ("i01_sample", IntakeMode.Markers), (I10, IntakeMode.AutoDetect) })
        {
            var selector = await SelectorAsync(IntakeTestData.PathOf(name), mode: mode);
            var clock = Stopwatch.StartNew();
            var location = await CsvPartLocator.LocateAsync(IntakeTestData.PathOf(name), selector, CancellationToken.None);
            _output.WriteLine($"{name} {mode}: {location.Reason} '{location.Message}' in {clock.ElapsedMilliseconds} ms");
            Assert.NotEqual(CsvPartFailure.Unexpected, location.Reason);
        }
    }

    /// <summary>36,001 parts (i01 in Markers mode 2,000 times) plus one unique table at the end. Slow: INTAKE_SLOW_TESTS=1.</summary>
    [Fact]
    public async Task Timing_36001Parts_RepeatedPartFails_UniquePartIsFound()
    {
        if (Environment.GetEnvironmentVariable("INTAKE_SLOW_TESTS") != "1")
        {
            return;
        }

        var path = Path.Combine(_folder, "parts36001.txt");
        var copy = File.ReadAllText(IntakeTestData.PathOf("i01_sample"));
        using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            for (var i = 0; i < 2000; i++)
            {
                writer.Write(copy);
            }

            writer.Write("##### BEGIN csv | Unique #####\nunique_a,unique_b,unique_c\n1,2,3\n4,5,6\n##### END #####\n");
        }

        var clock = Stopwatch.StartNew();
        var item = new FileIntakeFactory().CreateItem(path);
        var scan = await new IntakeScanService(new IntakeSettings { Mode = IntakeMode.Markers }).ScanAsync(item, null, CancellationToken.None);
        var session = new IntakeEditSession(item, scan, null, "test");
        _output.WriteLine($"{session.Containers.Count:N0} parts scanned in {clock.ElapsedMilliseconds} ms");
        var first = session.Containers.ToList().FindIndex(c => c.Format == "csv");
        var last = session.Containers.ToList().FindLastIndex(c => c.Format == "csv");
        // Every csv marker part gets the same delimiter, so every repeat is edited the same way and the
        // template keeps its repeating group (G5c2 rule); editing only the first copy would not.
        foreach (var i in Enumerable.Range(0, session.Containers.Count).Where(i => session.Containers[i].Format == "csv" && session.Containers[i].Delimiter is null).ToList())
        {
            Assert.True(session.SetTableOptions(i, delimiter: "comma").Ok);
        }

        _output.WriteLine($"delimiters set at {clock.ElapsedMilliseconds} ms");
        var repeated = CsvPartSelector.FromSession(session, first, out _)!;
        var unique = CsvPartSelector.FromSession(session, last, out _)!;
        _output.WriteLine($"selectors built at {clock.ElapsedMilliseconds} ms");

        foreach (var (label, selector) in new[] { ("repeated", repeated), ("unique", unique) })
        {
            clock.Restart();
            var location = await CsvPartLocator.LocateAsync(path, selector, CancellationToken.None);
            _output.WriteLine($"{label}: {location.Reason} '{location.Message}' in {clock.ElapsedMilliseconds} ms");
            Assert.Equal(label == "unique", location.Ok);
        }

        File.Delete(path);
    }

    // (12) -----------------------------------------------------------------------------------------

    [Fact]
    public void IntakeAndCsvSources_DoNotUseUiPipelineProjectOrGeneration_AndEmbeddedCsvFilesDoNotUseIntake()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Nodify_ETLBox_Eval.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var app = Path.Combine(root.FullName, "Nodify_ETLBox");
        string[] forbidden = ["EtlDesigner.UI", "EtlDesigner.Engine.Pipeline", "EtlDesigner.Engine.Project", "EtlDesigner.Engine.Generation", "System.Windows"];
        var problems = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(app, "Engine", "Intake"), "*.cs").Concat(Directory.GetFiles(Path.Combine(app, "Engine", "Csv"), "*.cs")))
        {
            foreach (var line in File.ReadLines(file).Where(l => l.TrimStart().StartsWith("using ", StringComparison.Ordinal)))
            {
                if (forbidden.Any(f => line.Contains(f, StringComparison.Ordinal)))
                {
                    problems.Add($"{Path.GetFileName(file)}: {line.Trim()}");
                }
            }
        }

        // G6b: a CsvFile DLL compiles CsvPipelineDllGenerator.SourceFiles without Engine/Intake; a CSV part DLL adds
        // CsvPartSourceFiles. No embedded file may use UI, Project, Generation or WPF, and each one is an EmbeddedResource.
        var embedded = File.ReadAllText(Path.Combine(app, "Nodify_ETLBox.csproj"));
        foreach (var rel in EtlDesigner.Engine.Generation.CsvPipelineDllGenerator.SourceFiles.Concat(EtlDesigner.Engine.Generation.CsvPipelineDllGenerator.CsvPartSourceFiles))
        {
            var file = Path.Combine(app, rel.Replace('/', Path.DirectorySeparatorChar));
            var text = File.ReadAllText(file);
            if (!embedded.Contains($"Include=\"{rel.Replace('/', '\\')}\"", StringComparison.Ordinal))
            {
                problems.Add($"{rel} is not an EmbeddedResource");
            }

            if (EtlDesigner.Engine.Generation.CsvPipelineDllGenerator.SourceFiles.Contains(rel) && text.Contains("EtlDesigner.Engine.Intake", StringComparison.Ordinal))
            {
                problems.Add($"{rel} is in every DLL and uses Engine/Intake");
            }

            foreach (var line in text.Split('\n').Where(l => l.TrimStart().StartsWith("using ", StringComparison.Ordinal)))
            {
                if (new[] { "EtlDesigner.UI", "EtlDesigner.Engine.Project", "EtlDesigner.Engine.Generation", "System.Windows" }.Any(f => line.Contains(f, StringComparison.Ordinal)))
                {
                    problems.Add($"{rel}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(problems);
    }

    // G6b step 0b: the two messages G6a could not reach with a real file change -----------------

    [Fact]
    public async Task TamperedExpectedDelimiter_GivesDelimiterDiffers_WithItsSentence()
    {
        var path = IntakeTestData.PathOf(I10);
        var selector = await SelectorAsync(path);
        var tampered = selector with { Expected = selector.Expected with { Delimiter = ";" } };

        var location = await LocateAsync(path, tampered);

        Assert.False(location.Ok);
        Assert.Equal(CsvPartFailure.DelimiterDiffers, location.Reason);
        Assert.Equal("Part 2 of this file is a comma table, but the pipeline expects a semicolon table.", location.Message);
        Assert.Null(location.Part);
    }

    [Fact]
    public async Task PartIndexOnANonTablePart_GivesNotATable_WithItsSentence()
    {
        var path = IntakeTestData.PathOf(I10);
        var selector = await SelectorAsync(path);
        var keyValue = selector.Template.Parts.Select((p, i) => (p, i)).First(x => x.p.Format is not ("csv" or "tsv")).i;
        var tampered = selector with { PartIndex = keyValue };

        var location = await LocateAsync(path, tampered);

        Assert.False(location.Ok);
        Assert.Equal(CsvPartFailure.NotATable, location.Reason);
        Assert.Equal($"Part {keyValue + 1} of this file is not a table, but the pipeline expects a comma table.", location.Message);
        Assert.Null(location.Part);
    }
}
