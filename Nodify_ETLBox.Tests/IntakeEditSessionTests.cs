using System.Diagnostics;
using System.IO;
using System.Text;
using EtlDesigner.Engine.Intake;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>Task G5a step 1: the edit session (change format, table options, rename, split, merge, undo, redo, revert).</summary>
public class IntakeEditSessionTests
{
    // A csv table (lines 1-5), a text block with one blank line inside (7-9), and a JSON block with two blank lines inside (11-17).
    internal const string Sample =
        "id,name,value\n1,alpha,10\n2,beta,20\n3,gamma,30\n4,delta,40\n\n" +
        "Some note text here.\n\nAnother line of prose.\n\n" +
        "{\n  \"a\": 1,\n\n\n  \"b\": 2,\n  \"c\": 3\n}\n";

    internal static async Task<(IntakeItem Item, IntakeScanResult Scan)> ScanAsync(IntakeItem item, IntakeMode mode = IntakeMode.AutoDetect)
    {
        var scan = await new IntakeScanService(new IntakeSettings { Mode = mode }).ScanAsync(item, null, CancellationToken.None);
        Assert.Null(scan.Error);
        return (item, scan);
    }

    internal static async Task<IntakeEditSession> SessionAsync(string text, EditSettings? edit = null, ILineageLog? log = null)
    {
        var (item, scan) = await ScanAsync(IntakeTestData.MemoryItem(text));
        return new IntakeEditSession(item, scan, log, Guid.NewGuid().ToString(), edit: edit);
    }

    internal static async Task<IntakeEditSession> FileSessionAsync(string name, IntakeMode mode)
    {
        var (item, scan) = await ScanAsync(IntakeTestData.Item(name), mode);
        return new IntakeEditSession(item, scan, null, Guid.NewGuid().ToString());
    }

    private static List<(long First, long Last)> Ranges(IntakeEditSession s)
        => s.Containers.Select(c => (c.MarkerBeginLine ?? c.ContentFirstLine ?? 0, c.MarkerEndLine ?? c.ContentLastLine ?? 0)).ToList();

    private static List<string> Lines(IntakeEditSession s, ContainerInfo c)
        => IntakePreview.Read(s.Item, c, s.EncodingName, maxLines: 1000).Lines.Select(l => l.Text).ToList();

    /// <summary>The G2 property, checked without the code under test: every non-blank line of the text is in exactly one container.</summary>
    internal static void AssertEveryNonBlankLineInOneContainer(IntakeEditSession s, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var owner = new int[lines.Length + 2];
        long previous = 0;
        foreach (var c in s.Containers)
        {
            var first = c.MarkerBeginLine ?? c.ContentFirstLine;
            var last = c.MarkerEndLine ?? c.ContentLastLine;
            if (first is null || last is null)
            {
                continue;
            }

            Assert.True(first > previous, $"container {c.Order} starts at {first}, not after line {previous}");
            previous = last.Value;
            for (var line = first.Value; line <= last; line++)
            {
                Assert.True(owner[line] == 0, $"line {line} is in containers {owner[line]} and {c.Order}");
                owner[line] = c.Order;
            }
        }

        for (var line = 1; line <= lines.Length; line++)
        {
            Assert.True(lines[line - 1].Trim().Length == 0 || owner[line] != 0, $"line {line} is in no container");
        }

        Assert.Equal(s.Containers.Count, s.Validation.Count);
        for (var i = 0; i < s.Containers.Count; i++)
        {
            Assert.Equal(i + 1, s.Containers[i].Order);
            Assert.Equal(i + 1, s.Validation[i].Order);
        }
    }

    private static int IndexOf(IntakeEditSession s, string format) => s.Containers.ToList().FindIndex(c => c.Format == format);

    [Fact]
    public async Task Sample_IsReadAsATableATextAndAJsonBlock()
    {
        var s = await SessionAsync(Sample);
        Assert.Equal([("csv", 1L, 5L), ("text", 7L, 9L), ("json", 11L, 17L)],
            s.Containers.Select(c => (c.Format, c.ContentFirstLine!.Value, c.ContentLastLine!.Value)).ToList());
        Assert.False(s.IsModified);
        Assert.False(s.CanUndo);
    }

    // ---- ChangeFormat ------------------------------------------------------------------------------

    [Fact]
    public async Task ChangeFormat_ToTsv_SetsTheTab_ClearsSubTypeAndReasons_AndMarksTheContainer()
    {
        var s = await SessionAsync(Sample);
        var result = s.ChangeFormat(0, "TSV");
        Assert.True(result.Ok, result.Message);
        var c = s.Containers[0];
        Assert.Equal("tsv", c.Format);
        Assert.Equal("\t", c.Delimiter);
        Assert.Null(c.SubType);
        Assert.Equal(["Set by you."], c.Reasons);
        Assert.True(c.ConfirmedByUser);
        Assert.Equal(ContainerConfidence.Sure, c.Confidence);
        Assert.True(s.IsModified);
        AssertEveryNonBlankLineInOneContainer(s, Sample);
    }

    [Fact]
    public async Task ChangeFormat_ToText_ClearsTableOptions_AndToCsv_GivesAComma()
    {
        var s = await SessionAsync(Sample);
        Assert.True(s.ChangeFormat(0, "text").Ok);
        var c = s.Containers[0];
        Assert.Null(c.Delimiter);
        Assert.Null(c.HeaderGuess);
        Assert.Equal(0, c.SkipLines);

        Assert.True(s.ChangeFormat(1, "csv").Ok);
        Assert.Equal(",", s.Containers[1].Delimiter);
        Assert.True(s.ChangeFormat(1, "fixedwidth").Ok);
        Assert.Equal("space", s.Containers[1].Delimiter);
    }

    [Theory]
    [InlineData("excel")]
    [InlineData("")]
    [InlineData("soap")]
    public async Task ChangeFormat_UnknownName_IsRefused_AndNothingChanges(string format)
    {
        var s = await SessionAsync(Sample);
        var before = s.Containers.ToList();
        var result = s.ChangeFormat(0, format);
        Assert.False(result.Ok);
        Assert.Contains("is not a format the tool knows", result.Message);
        Assert.Equal(before, s.Containers);
        Assert.False(s.CanUndo);
    }

    [Fact]
    public async Task ChangeFormat_OnAMarkerFile_KeepsTheMarkerLines()
    {
        var s = await FileSessionAsync("i01_sample", IntakeMode.Markers);
        var index = IndexOf(s, "json");
        var before = s.Containers[index];
        Assert.True(s.ChangeFormat(index, "text").Ok);
        var after = s.Containers[index];
        Assert.Equal("text", after.Format);
        Assert.Equal(ContainerKind.Marker, after.Kind);
        Assert.Equal((before.MarkerBeginLine, before.MarkerEndLine, before.ContentFirstLine), (after.MarkerBeginLine, after.MarkerEndLine, after.ContentFirstLine));
    }

    // ---- SetTableOptions ---------------------------------------------------------------------------

    [Fact]
    public async Task SetTableOptions_ChangesDelimiterHeaderAndSkipLines()
    {
        var s = await SessionAsync(Sample);
        var result = s.SetTableOptions(0, "semicolon", "No", 2);
        Assert.True(result.Ok, result.Message);
        var c = s.Containers[0];
        Assert.Equal((";", HeaderGuess.No, 2), (c.Delimiter, c.HeaderGuess, c.SkipLines));
        Assert.Equal("Options set by you.", c.Reasons![^1]);
        Assert.True(c.ConfirmedByUser);

        Assert.True(s.SetTableOptions(0, delimiter: "tab").Ok);
        Assert.Equal("\t", s.Containers[0].Delimiter);
        Assert.Equal(2, s.Containers[0].SkipLines);
    }

    [Theory]
    [InlineData("colon", null, null, "is not a delimiter the tool knows")]
    [InlineData(null, "maybe", null, "must be Yes or No")]
    [InlineData(null, null, 5, "between 0 and 4")]
    [InlineData(null, null, -1, "between 0 and 4")]
    public async Task SetTableOptions_BadValues_AreRefused(string? delimiter, string? header, int? skip, string expected)
    {
        var s = await SessionAsync(Sample);
        var before = s.Containers[0];
        var result = s.SetTableOptions(0, delimiter, header, skip);
        Assert.False(result.Ok);
        Assert.Contains(expected, result.Message);
        Assert.Same(before, s.Containers[0]);
    }

    [Fact]
    public async Task SetTableOptions_OnText_IsRefused_AndFixedWidth_TakesOnlySpace()
    {
        var s = await SessionAsync(Sample);
        Assert.Contains("apply only to CSV, TSV and fixed-width", s.SetTableOptions(1, "comma").Message);

        Assert.True(s.ChangeFormat(0, "fixedwidth").Ok);
        Assert.Contains("can only be space", s.SetTableOptions(0, "comma").Message);
        Assert.True(s.SetTableOptions(0, "space", "Yes").Ok);
    }

    // ---- Rename ------------------------------------------------------------------------------------

    [Fact]
    public async Task Rename_TrimsTheName_AndKeepsTheCheck()
    {
        var s = await SessionAsync(Sample);
        var check = s.Validation[0];
        Assert.True(s.Rename(0, "  Results  ").Ok);
        Assert.Equal("Results", s.Containers[0].Name);
        Assert.Same(check, s.Validation[0]);
    }

    [Theory]
    [InlineData("   ", "empty")]
    [InlineData("tab\there", "control character")]
    [InlineData("line\nbreak", "control character")]
    public async Task Rename_BadNames_AreRefused(string name, string expected)
    {
        var s = await SessionAsync(Sample);
        var result = s.Rename(0, name);
        Assert.False(result.Ok);
        Assert.Contains(expected, result.Message);
    }

    [Fact]
    public async Task Rename_81Characters_IsRefused_80AreAccepted()
    {
        var s = await SessionAsync(Sample);
        Assert.Contains("at most 80", s.Rename(0, new string('x', 81)).Message);
        Assert.True(s.Rename(0, new string('x', 80)).Ok);
    }

    // ---- Split -------------------------------------------------------------------------------------

    [Fact]
    public async Task Split_ATable_GivesTwoParts_WithTheRightLinesAndBytes()
    {
        var s = await SessionAsync(Sample);
        var result = s.Split(0, 4);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(4, s.Containers.Count);

        var (top, bottom) = (s.Containers[0], s.Containers[1]);
        Assert.Equal((1L, 3L, 3L), (top.ContentFirstLine!.Value, top.ContentLastLine!.Value, top.ContentLineCount));
        Assert.Equal((4L, 5L, 2L), (bottom.ContentFirstLine!.Value, bottom.ContentLastLine!.Value, bottom.ContentLineCount));
        Assert.Equal(["id,name,value", "1,alpha,10", "2,beta,20"], Lines(s, top));
        Assert.Equal(["3,gamma,30", "4,delta,40"], Lines(s, bottom));
        Assert.True(top.ConfirmedByUser && bottom.ConfirmedByUser);
        Assert.Equal("Split by you.", bottom.Reasons![^1]);
        Assert.Equal(top.Name + " (2)", bottom.Name);
        AssertEveryNonBlankLineInOneContainer(s, Sample);
    }

    [Fact]
    public async Task Split_AtABlankLineFollowedByText_KeepsTheBlankLineWithTheFirstPart()
    {
        var s = await SessionAsync(Sample);
        Assert.True(s.Split(1, 8).Ok);
        Assert.Equal((7L, 8L), (s.Containers[1].ContentFirstLine!.Value, s.Containers[1].ContentLastLine!.Value));
        Assert.Equal((9L, 9L), (s.Containers[2].ContentFirstLine!.Value, s.Containers[2].ContentLastLine!.Value));
        Assert.Equal(["Some note text here.", ""], Lines(s, s.Containers[1]));
        AssertEveryNonBlankLineInOneContainer(s, Sample);
    }

    [Theory]
    [InlineData(0, 1, "its first line")]
    [InlineData(0, 6, "not inside this container")]
    [InlineData(0, 99, "not inside this container")]
    [InlineData(2, 13, "is blank and is not followed by a line with text")]
    [InlineData(7, 3, "There is no container number 8")]
    public async Task Split_Refusals(int index, long line, string expected)
    {
        var s = await SessionAsync(Sample);
        var before = s.Containers.ToList();
        var result = s.Split(index, line);
        Assert.False(result.Ok);
        Assert.Contains(expected, result.Message);
        Assert.Equal(before, s.Containers);
    }

    [Fact]
    public async Task Split_AContainerAboveTheEditLimit_IsRefused_WithoutReadingIt()
    {
        var bytes = Encoding.UTF8.GetBytes(Sample);
        var opens = 0;
        var item = IntakeTestData.MemoryItem(bytes) with { OpenRead = () => { Interlocked.Increment(ref opens); return new MemoryStream(bytes, writable: false); } };
        var (_, scan) = await ScanAsync(item);
        var s = new IntakeEditSession(item, scan, null, "x", edit: new EditSettings { MaxEditMb = 0 });

        var before = opens;
        var result = s.Split(0, 3);
        Assert.False(result.Ok);
        Assert.Contains("larger than the edit limit of 0 MB", result.Message);
        Assert.Equal(before, opens);
    }

    [Fact]
    public async Task Split_RedetectsEachPart()
    {
        // A key-value header directly followed by a table: split at the table, and each part gets its own format.
        const string text = "Instrument: X1\nOperator: Ann\nDate: 2026-11-08\nMode: fast\nid,name,value\n1,a,10\n2,b,20\n3,c,30\n4,d,40\n";
        var s = await SessionAsync(text);
        var index = s.Containers.ToList().FindIndex(c => c.ContentFirstLine <= 4 && c.ContentLastLine >= 5);
        if (index < 0)
        {
            // The detector already splits them: merge first, then split.
            Assert.True(s.Merge(0).Ok);
            index = 0;
        }

        Assert.True(s.Split(index, 5).Ok);
        Assert.Equal("keyvalue", s.Containers[index].Format);
        Assert.Equal("csv", s.Containers[index + 1].Format);
        AssertEveryNonBlankLineInOneContainer(s, text);
    }

    [Fact]
    public async Task Split_AUtf16File_FindsTheRightBytes()
    {
        var text = "id,name,value\n1,alpha,10\n2,beta,20\n3,gamma,30\n";
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();
        var (item, scan) = await ScanAsync(IntakeTestData.MemoryItem(bytes));
        var s = new IntakeEditSession(item, scan, null, "x");
        Assert.True(s.Split(0, 3).Ok);
        Assert.Equal(["id,name,value", "1,alpha,10"], Lines(s, s.Containers[0]));
        Assert.Equal(["2,beta,20", "3,gamma,30"], Lines(s, s.Containers[1]));
    }

    // ---- Merge -------------------------------------------------------------------------------------

    [Fact]
    public async Task Merge_TakesTheLinesBetween_AndKeepsTheFirstName()
    {
        var s = await SessionAsync(Sample);
        var name = s.Containers[0].Name;
        Assert.True(s.Merge(0).Ok);
        Assert.Equal(2, s.Containers.Count);
        var c = s.Containers[0];
        Assert.Equal((1L, 9L, 9L), (c.ContentFirstLine!.Value, c.ContentLastLine!.Value, c.ContentLineCount));
        Assert.Equal(name, c.Name);
        Assert.Equal("Merged by you.", c.Reasons![^1]);
        Assert.Equal(9, Lines(s, c).Count);
        AssertEveryNonBlankLineInOneContainer(s, Sample);
    }

    [Fact]
    public async Task Merge_TheLastContainer_IsRefused()
    {
        var s = await SessionAsync(Sample);
        var result = s.Merge(2);
        Assert.False(result.Ok);
        Assert.Contains("no next container", result.Message);
        Assert.Contains("no container number", s.Merge(-1).Message);
    }

    [Fact]
    public async Task SplitThenMerge_GivesTheOriginalRanges()
    {
        var s = await SessionAsync(Sample);
        var before = s.Containers[0];
        Assert.True(s.Split(0, 3).Ok);
        Assert.True(s.Merge(0).Ok);
        var after = s.Containers[0];
        Assert.Equal((before.ContentFirstLine, before.ContentLastLine, before.ContentStartByte, before.ContentEndByte, before.ContentLineCount),
            (after.ContentFirstLine, after.ContentLastLine, after.ContentStartByte, after.ContentEndByte, after.ContentLineCount));
        Assert.Equal(before.Format, after.Format);
    }

    // ---- i01 in both modes -------------------------------------------------------------------------

    [Theory]
    [InlineData(IntakeMode.AutoDetect)]
    [InlineData(IntakeMode.Markers)]
    public async Task I01_EveryOperation_KeepsTheProperty_AndUndoesToTheScan(IntakeMode mode)
    {
        var s = await FileSessionAsync("i01_sample", mode);
        var text = File.ReadAllText(IntakeTestData.PathOf("i01_sample"));
        var scan = s.Containers.ToList();

        var big = s.Containers.ToList().FindIndex(2, c => c.ContentLineCount >= 4);
        var c = s.Containers[big];
        var line = c.ContentFirstLine!.Value + 2;
        var steps = new List<Func<EditResult>>
        {
            () => s.ChangeFormat(0, "text"),
            () => s.Rename(1, "Renamed"),
            () => s.Split(big, line),
            () => s.Merge(big),
            () => s.Merge(0)
        };

        foreach (var step in steps)
        {
            var result = step();
            Assert.True(result.Ok, result.Message);
            AssertEveryNonBlankLineInOneContainer(s, text);
        }

        if (mode == IntakeMode.Markers)
        {
            // Split then merge of a marker segment keeps both marker lines.
            Assert.Equal((c.MarkerBeginLine, c.MarkerEndLine), (s.Containers[big - 1].MarkerBeginLine, s.Containers[big - 1].MarkerEndLine));
        }

        var edited = s.Containers.ToList();
        for (var i = 0; i < steps.Count; i++)
        {
            Assert.True(s.Undo().Ok);
            AssertEveryNonBlankLineInOneContainer(s, text);
        }

        Assert.Equal(scan, s.Containers);
        Assert.False(s.IsModified);
        Assert.False(s.CanUndo);

        for (var i = 0; i < steps.Count; i++)
        {
            Assert.True(s.Redo().Ok);
        }

        Assert.True(s.IsModified);
        Assert.False(s.CanRedo);
        Assert.Equal(scan.Count - 1, s.Containers.Count);
        Assert.Equal(edited, s.Containers);
    }

    // ---- Undo, redo, revert ------------------------------------------------------------------------

    [Fact]
    public async Task UndoAndRedo_OfEachOperation_RestoreTheContainersAndChecks()
    {
        var operations = new List<Func<IntakeEditSession, EditResult>>
        {
            s => s.ChangeFormat(0, "text"),
            s => s.SetTableOptions(0, "pipe"),
            s => s.Rename(0, "New"),
            s => s.Split(0, 3),
            s => s.Merge(0)
        };

        foreach (var operation in operations)
        {
            var s = await SessionAsync(Sample);
            var before = (s.Containers.ToList(), s.Validation.ToList());
            Assert.True(operation(s).Ok);
            var after = (s.Containers.ToList(), s.Validation.ToList());

            Assert.True(s.Undo().Ok);
            Assert.Equal(before.Item1, s.Containers);
            Assert.Equal(before.Item2, s.Validation);
            Assert.False(s.IsModified);
            Assert.True(s.CanRedo);

            Assert.True(s.Redo().Ok);
            Assert.Equal(after.Item1, s.Containers);
            Assert.Equal(after.Item2, s.Validation);
            Assert.True(s.IsModified);
        }
    }

    [Fact]
    public async Task Undo_WithNothingToUndo_AndANewEdit_ClearsRedo()
    {
        var s = await SessionAsync(Sample);
        Assert.False(s.Undo().Ok);
        Assert.False(s.Redo().Ok);
        Assert.True(s.Rename(0, "A").Ok);
        Assert.True(s.Undo().Ok);
        Assert.True(s.Rename(1, "B").Ok);
        Assert.False(s.CanRedo);
    }

    [Fact]
    public async Task Undo_KeepsAtMostMaxUndoSteps()
    {
        var s = await SessionAsync(Sample, new EditSettings { MaxUndo = 3 });
        for (var i = 0; i < 5; i++)
        {
            Assert.True(s.Rename(0, "Name " + i).Ok);
        }

        var undone = 0;
        while (s.Undo().Ok)
        {
            undone++;
        }

        Assert.Equal(3, undone);
        Assert.Equal("Name 1", s.Containers[0].Name);
        Assert.True(s.IsModified);
    }

    [Fact]
    public async Task Revert_GoesBackToTheScan_AndCanBeUndone()
    {
        var s = await SessionAsync(Sample);
        var scan = s.Containers.ToList();
        Assert.True(s.Split(0, 3).Ok);
        Assert.True(s.ChangeFormat(0, "text").Ok);
        var edited = s.Containers.ToList();

        Assert.True(s.Revert().Ok);
        Assert.Equal(scan, s.Containers);
        Assert.False(s.IsModified);
        Assert.True(s.CanUndo);

        Assert.True(s.Undo().Ok);
        Assert.Equal(edited, s.Containers);
        Assert.True(s.IsModified);
    }

    // ---- Validation ------------------------------------------------------------------------------

    [Fact]
    public async Task AnEdit_ChecksOnlyTheChangedContainers()
    {
        var bytes = Encoding.UTF8.GetBytes(Sample);
        var seeks = new List<long>();
        var item = IntakeTestData.MemoryItem(bytes) with
        {
            OpenRead = () => new IntakeLargeFileTests.ObservingStream(new MemoryStream(bytes, writable: false), p => { lock (seeks) seeks.Add(p); })
        };
        var (_, scan) = await ScanAsync(item);
        var s = new IntakeEditSession(item, scan, null, "x");
        var untouched = new[] { s.Validation[1], s.Validation[2] };

        seeks.Clear();
        Assert.True(s.ChangeFormat(0, "text").Ok);
        Assert.Same(untouched[0], s.Validation[1]);
        Assert.Same(untouched[1], s.Validation[2]);
        Assert.All(seeks.Where(p => p > 0), p => Assert.True(p <= s.Containers[0].ContentEndByte, $"read at byte {p}, outside container 1"));

        seeks.Clear();
        Assert.True(s.Split(2, 15).Ok);
        Assert.Equal(untouched[0] with { Order = 2 }, s.Validation[1]);
        Assert.All(seeks.Where(p => p > 0), p => Assert.True(p >= s.Containers[2].ContentStartByte, $"read at byte {p}, before the split container"));
    }

    [Fact]
    public void ConfirmedByUser_IsWrittenOnlyWhenTrue_AndReadBack()
    {
        var plain = new ContainerInfo(1, ContainerKind.Detected, "csv", "csv-1", "", null, null, 1, 2, 2, 0, 10, ContainerStatus.Ok, "", ContainerConfidence.Sure);
        var result = new IntakeResult(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow, "1", null, [plain, plain with { Order = 2, ConfirmedByUser = true }], [], 0, false, null);
        var json = IntakeJson.Serialize(result);
        Assert.Equal(1, json.Split("confirmedByUser").Length - 1);
        var back = IntakeJson.DeserializeResult(json);
        Assert.False(back.Containers[0].ConfirmedByUser);
        Assert.True(back.Containers[1].ConfirmedByUser);
    }
}

/// <summary>An edit on 36,000 containers: time and memory.</summary>
[Collection("Intake memory")]
public class IntakeEditSessionLargeTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("g5a_edit_").FullName;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    [Fact]
    public void OneEdit_On36000Containers_IsFast_AndAddsAlmostNoMemory()
    {
        // i01 repeated 2,000 times, read in marker mode: 18 containers per copy plus one.
        var sample = File.ReadAllBytes(IntakeTestData.PathOf("i01_sample"));
        var path = Path.Combine(_folder, "i01_x2000.txt");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            for (var i = 0; i < 2000; i++)
            {
                file.Write(sample);
            }
        }

        var item = new FileIntakeFactory().CreateItem(path);
        var intake = IntakeReader.Read(item, IntakeTestData.Markers, null, CancellationToken.None);
        var validation = new IntakeValidationResult(Guid.NewGuid(), intake.ItemId,
            intake.Containers.Select(c => new ContainerValidation(c.Order, ContainerStatus.Ok, [], 0, false)).ToList(), 0, 0, 0, 0, false, null);
        var scan = new IntakeScanResult(intake, validation, intake.EncodingUsed, false, 0, false, null, IntakeMode.Markers);
        var s = new IntakeEditSession(item, scan, null, "x");
        Assert.True(s.Containers.Count >= 36_000, $"{s.Containers.Count} containers");

        var middle = s.Containers.Count / 2;
        while (s.Containers[middle].ContentLineCount < 3)
        {
            middle++;
        }

        var line = s.Containers[middle].ContentFirstLine!.Value + 1;
        s.Rename(0, "warm-up");
        s.Undo();

        var memory = LiveMemory.Bytes();
        var start = memory;
        var times = new List<(string Name, long Ms, long Bytes)>();
        void Time(string name, Func<EditResult> step)
        {
            var clock = Stopwatch.StartNew();
            var result = step();
            var ms = clock.ElapsedMilliseconds;
            Assert.True(result.Ok, result.Message);
            var now = LiveMemory.Bytes();
            times.Add((name, ms, now - memory));
            memory = now;
        }

        Time("split", () => s.Split(middle, line));
        Time("merge", () => s.Merge(middle));
        Time("change format", () => s.ChangeFormat(middle, "text"));
        Time("rename", () => s.Rename(middle, "Middle"));
        Time("undo", s.Undo);
        Time("redo", s.Redo);
        var added = memory - start;

        foreach (var (name, ms, bytes) in times)
        {
            output.WriteLine($"{name}: {ms} ms, {bytes:N0} bytes");
        }

        output.WriteLine($"containers: {s.Containers.Count}, memory added by six steps: {added:N0} bytes");
        Assert.All(times, t => Assert.True(t.Ms < 1000, $"{t.Name} took {t.Ms} ms"));

        // The first split renumbers the containers after it: they become copies while the scan keeps
        // its originals (about 150 bytes a container). The steps after it add almost nothing.
        Assert.True(added < 300L * s.Containers.Count, $"six steps added {added:N0} bytes");
        Assert.All(times.Skip(1), t => Assert.True(t.Bytes < 512 * 1024, $"{t.Name} added {t.Bytes:N0} bytes"));
    }
}
