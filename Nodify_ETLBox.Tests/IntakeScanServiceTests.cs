using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using EtlDesigner.Engine.Intake;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>Task G4a step 3: the scan service (read, then check) and the container preview.</summary>
[Collection("Intake memory")]
public class IntakeScanServiceTests : IDisposable
{
    private static readonly TimeSpan LargeLimit = TimeSpan.FromSeconds(60);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "intake-scan-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public IntakeScanServiceTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private sealed class Recorder : IProgress<IntakeScanProgress>
    {
        public List<IntakeScanProgress> Reports { get; } = [];

        public void Report(IntakeScanProgress value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }
    }

    private string WriteRepeated(string name, int copies)
    {
        var sample = File.ReadAllBytes(IntakeTestData.PathOf("i01_sample"));
        var path = Path.Combine(_folder, name);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        for (var i = 0; i < copies; i++)
        {
            output.Write(sample);
        }

        return path;
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    [Theory]
    [InlineData(IntakeMode.AutoDetect)]
    [InlineData(IntakeMode.Markers)]
    public async Task ScanOfI01_GivesTheSameAsTheReaderAndTheValidator(IntakeMode mode)
    {
        var settings = new IntakeSettings { Mode = mode };
        var item = IntakeTestData.Item("i01_sample");

        var scan = await TestTimeout.Run(() => new IntakeScanService(settings).ScanAsync(item, null, CancellationToken.None));
        var read = IntakeReader.Read(item, settings, null, CancellationToken.None);
        var validation = IntakeValidator.Validate(item, read, null, CancellationToken.None, settings);

        Assert.Null(scan.Error);
        Assert.False(scan.Cancelled);
        Assert.Equal(Json(read.Containers), Json(scan.Intake!.Containers));
        Assert.Equal(Json(validation.Containers), Json(scan.Validation!.Containers));
        Assert.Equal(("utf-8", false), (scan.EncodingUsed, scan.EncodingGuessed));
    }

    [Fact]
    public async Task AMissingFile_GivesAPlainError()
    {
        var path = Path.Combine(_folder, "missing.txt");

        var scan = await TestTimeout.Run(() => new IntakeScanService().ScanAsync(new FileIntakeFactory().CreateItem(path), null, CancellationToken.None));

        Assert.Equal(LineIndexer.NotFoundMessage, scan.Error);
        Assert.Null(scan.Validation);
    }

    [Fact]
    public async Task ALockedFile_GivesAPlainError()
    {
        var path = Path.Combine(_folder, "locked.txt");
        File.WriteAllText(path, "a,b,c\n1,2,3\n4,5,6\n");
        var item = new FileIntakeFactory().CreateItem(path);
        using var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var scan = await TestTimeout.Run(() => new IntakeScanService().ScanAsync(item, null, CancellationToken.None));

        Assert.Equal(LineIndexer.UnreadableMessage, scan.Error);
        Assert.DoesNotContain(_folder, scan.Error);
    }

    [Fact]
    public async Task AFileOverTheSizeLimit_IsRefusedBeforeReading_WithTheLimitInMb()
    {
        var path = Path.Combine(_folder, "big.txt");
        File.WriteAllText(path, string.Concat(Enumerable.Repeat("1234567890,abcdefghij\n", 100_000)));
        var opened = false;
        var item = new FileIntakeFactory().CreateItem(path);
        var open = item.OpenRead;
        item = item with { OpenRead = () => { opened = true; return open(); } };

        var scan = await TestTimeout.Run(() => new IntakeScanService(new IntakeSettings { MaxFileSizeMb = 1 }).ScanAsync(item, null, CancellationToken.None));

        Assert.Contains("limit of 1 MB", scan.Error);
        Assert.False(opened);
    }

    [Fact]
    public async Task Cancel_20Times_DuringAScanOfTheLargeStressFile_ReleasesTheFile()
    {
        var original = WriteRepeated("stress.txt", 2000);

        for (var i = 0; i < 20; i++)
        {
            var path = Path.Combine(_folder, $"copy{i}.txt");
            File.Copy(original, path);
            using var cts = new CancellationTokenSource();
            var reads = 0;
            var item = new FileIntakeFactory().CreateItem(path);
            var open = item.OpenRead;
            var cancelAt = 5 + 23 * i;
            item = item with
            {
                OpenRead = () => new IntakeLargeFileTests.ObservingStream(open(), _ =>
                {
                    if (Interlocked.Increment(ref reads) == cancelAt)
                    {
                        cts.Cancel();
                    }
                })
            };

            var scan = await TestTimeout.Run(() => new IntakeScanService().ScanAsync(item, null, cts.Token), LargeLimit);

            Assert.True(scan.Cancelled, $"run {i + 1} was not cancelled");
            Assert.Null(scan.Error);
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public async Task TheLastProgressReport_Is100_WithTheFinalText()
    {
        var path = WriteRepeated("progress.txt", 200);
        var recorder = new Recorder();

        var scan = await TestTimeout.Run(() => new IntakeScanService().ScanAsync(new FileIntakeFactory().CreateItem(path), recorder, CancellationToken.None), LargeLimit);

        Assert.Null(scan.Error);
        var last = recorder.Reports[^1];
        Assert.Equal((IntakePhase.Done, 100, scan.Intake!.Containers.Count), (last.Phase, last.Percent, last.ContainersFound));
        Assert.Equal($"Finished: {scan.Intake.Containers.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} containers found", last.Text);
        Assert.Equal("Reading the file", recorder.Reports[0].Text);
        Assert.True(recorder.Reports.Select(r => r.Percent).SequenceEqual(recorder.Reports.Select(r => r.Percent).Order()), "the percent went down");
        Assert.All(recorder.Reports.Where(r => r.Phase == IntakePhase.Validating), r => Assert.Matches(@"^Checking container \d+ of \d+$", r.Text));
    }

    // ---- Preview -------------------------------------------------------------------------

    [Fact]
    public void Preview_OfA100000LineContainer_Returns200Lines_Quickly()
    {
        var text = string.Concat(Enumerable.Range(1, 100_000).Select(i => $"{i},value {i}\n")).TrimEnd('\n');
        var (item, result) = IntakeValidatorTests.One(text, "csv", 1, 100_000);
        var container = result.Containers[0];
        IntakePreview.Read(item, container, "utf-8");

        var clock = Stopwatch.StartNew();
        var preview = IntakePreview.Read(item, container, "utf-8");
        clock.Stop();
        _output.WriteLine($"preview of 100,000 lines: {clock.Elapsed.TotalMilliseconds:F1} ms");
        File.AppendAllText(Path.Combine(Path.GetDirectoryName(AutoDetectTruthTests.ReportPath())!, "g4a-large-file.txt"),
            $"{DateTime.Now:HH:mm:ss} preview of a 100,000-line container: {clock.Elapsed.TotalMilliseconds:F1} ms{Environment.NewLine}");

        Assert.Null(preview.Error);
        Assert.Equal(200, preview.Lines.Count);
        Assert.True(preview.Truncated);
        Assert.False(preview.LinesCut);
        Assert.Equal(new PreviewLine(200, "200,value 200"), preview.Lines[^1]);
        Assert.True(clock.ElapsedMilliseconds < 1000, $"the preview took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Preview_CutsALongLineTo500Characters()
    {
        var (item, result) = IntakeValidatorTests.One("short\n" + new string('x', 5000) + "\nend", "text", 1, 3);

        var preview = IntakePreview.Read(item, result.Containers[0], "utf-8");

        Assert.True(preview.LinesCut);
        Assert.False(preview.Truncated);
        Assert.Equal(new string('x', 500) + "...", preview.Lines[1].Text);
        Assert.Equal(("short", "end"), (preview.Lines[0].Text, preview.Lines[2].Text));
    }

    [Fact]
    public void Preview_LineNumbers_AreFileLineNumbers()
    {
        var text = string.Concat(Enumerable.Range(1, 39).Select(i => $"filler {i}\n")) + "first\r\nsecond\n\nfourth";
        var (item, result) = IntakeValidatorTests.One(text.Replace("\r", ""), "text", 40, 43);

        var preview = IntakePreview.Read(item, result.Containers[0], "auto");

        Assert.Equal(new[] { 40L, 41, 42, 43 }, preview.Lines.Select(l => l.LineNumber));
        Assert.Equal(new[] { "first", "second", "", "fourth" }, preview.Lines.Select(l => l.Text));
        Assert.False(preview.Truncated);
    }

    [Fact]
    public void Preview_ExactlyMaxLines_IsNotTruncated_AndOneMoreIs()
    {
        var (item, result) = IntakeValidatorTests.One("a\nb\nc", "text", 1, 3);
        var (item2, result2) = IntakeValidatorTests.One("a\nb\nc\n", "text", 1, 4);

        Assert.False(IntakePreview.Read(item, result.Containers[0], "utf-8", maxLines: 3).Truncated);
        Assert.True(IntakePreview.Read(item2, result2.Containers[0], "utf-8", maxLines: 3).Truncated);
    }

    [Fact]
    public void Preview_ReadsWindows1252_AndUtf16_WithTheEncodingOfTheRead()
    {
        var bytes = new byte[] { (byte)'a', 0xB5, (byte)'\n', (byte)'b' };
        var item = IntakeTestData.MemoryItem(bytes);
        var read = IntakeReader.Read(item, new IntakeSettings { Mode = IntakeMode.Markers }, null, CancellationToken.None);
        var container = read.Containers[0] with { ContentStartByte = 0, ContentEndByte = 4, ContentFirstLine = 1 };

        Assert.Equal("aµ", IntakePreview.Read(item, container, read.EncodingUsed!).Lines[0].Text);

        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("x\ny")).ToArray();
        var utf16Item = IntakeTestData.MemoryItem(utf16);
        var c16 = container with { ContentStartByte = 2, ContentEndByte = utf16.Length };
        Assert.Equal(new[] { "x", "y" }, IntakePreview.Read(utf16Item, c16, "utf-16le").Lines.Select(l => l.Text));
    }

    [Fact]
    public void Preview_OfAMissingFile_OrAnUnknownEncoding_IsAPlainError()
    {
        var (_, result) = IntakeValidatorTests.One("a\nb", "text", 1, 2);
        var missing = new FileIntakeFactory().CreateItem(Path.Combine(_folder, "gone.txt"));

        Assert.Equal(LineIndexer.NotFoundMessage, IntakePreview.Read(missing, result.Containers[0], "utf-8").Error);
        Assert.Equal(IntakeEncodings.UnknownMessage, IntakePreview.Read(missing, result.Containers[0], "klingon").Error);
    }
}
