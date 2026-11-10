using System.IO;
using System.Text;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Intake;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>
/// Task G6a step 1: <see cref="PartStream"/> and the part overload of <see cref="CsvRecordReader"/>.
/// A part cut out of a temp file must read exactly like the same text as a standalone file through
/// the path overload. Small files only (far below the 5,000-row free limit).
/// </summary>
public sealed class CsvPartReaderTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("g6a_part_").FullName;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private const string Before = "Instrument: X\nOperator: Y\n\nResults follow\n";

    private const string After = "\nEnd of report\nPrinted by Z\n";

    /// <summary>Skip lines, a header, a quoted line break, a quoted delimiter and one bad record ("4" has a stray quote).</summary>
    internal static string PartText(char d, bool header, int skip, int rows = 0)
    {
        var sb = new StringBuilder();
        for (var k = 1; k <= skip; k++)
        {
            sb.Append($"Title line {k}\n");
        }

        if (header)
        {
            sb.Append($"id{d}name{d}note\n");
        }

        sb.Append($"1{d}Äpfel{d}plain\n2{d}\"two\nlines\"{d}x\n3{d}c{d}\"has {d} inside\"\n4{d}de\"l\"ta{d}bad\n5{d}e{d}last");
        for (var r = 6; r < 6 + rows; r++)
        {
            sb.Append($"\n{r}{d}row {r}{d}more");
        }

        return sb.ToString();
    }

    /// <summary>Writes before + part + after in <paramref name="encoding"/> and returns the part's source.</summary>
    private CsvPartSource WritePart(string name, string before, string part, string after, Encoding encoding, string encodingName, string delimiter, bool header, int skip)
    {
        var path = Path.Combine(_folder, name);
        var preamble = encoding.GetPreamble();
        var bytes = preamble.Concat(encoding.GetBytes(before + part + after)).ToArray();
        File.WriteAllBytes(path, bytes);
        var start = preamble.Length + encoding.GetByteCount(before);
        var firstLine = before.Count(c => c == '\n') + 1;
        return new CsvPartSource(path, start, encoding.GetByteCount(part), encodingName, delimiter, header, skip, firstLine);
    }

    private string WriteStandalone(string name, string text, Encoding encoding)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, encoding.GetBytes(text));
        return path;
    }

    internal static async Task<(CsvReadOutcome Outcome, List<CsvRecord> Records)> ReadPart(CsvPartSource part)
    {
        var records = new List<CsvRecord>();
        var outcome = await TestTimeout.Run(() => new CsvRecordReader().ReadAsync(part, r => { lock (records) { records.Add(r); } return true; }, CancellationToken.None));
        return (outcome, records);
    }

    internal static async Task<(CsvReadOutcome Outcome, List<CsvRecord> Records)> ReadPath(CsvFileSettings settings)
    {
        var records = new List<CsvRecord>();
        var outcome = await TestTimeout.Run(() => new CsvRecordReader().ReadAsync(settings, r => { lock (records) { records.Add(r); } return true; }, CancellationToken.None));
        return (outcome, records);
    }

    public static TheoryData<string, bool, int, string> Cases()
    {
        var data = new TheoryData<string, bool, int, string>();
        foreach (var d in new[] { ",", ";", "\t" })
        {
            foreach (var header in new[] { true, false })
            {
                foreach (var skip in new[] { 0, 1, 2 })
                {
                    data.Add(d, header, skip, "utf-8");
                }
            }
        }

        data.Add(",", true, 1, "windows-1252");
        data.Add(";", true, 0, "utf-8-bom");
        data.Add(",", true, 2, "utf-16le");
        data.Add("\t", false, 1, "utf-16be");
        return data;
    }

    private static (Encoding Encoding, string Name) EncodingFor(string key) => key switch
    {
        "windows-1252" => (CodePages(1252), "windows-1252"),
        "utf-8-bom" => (new UTF8Encoding(true), "utf-8"),
        "utf-16le" => (new UnicodeEncoding(false, true), "utf-16le"),
        "utf-16be" => (new UnicodeEncoding(true, true), "utf-16be"),
        _ => (new UTF8Encoding(false), "utf-8")
    };

    private static Encoding CodePages(int page)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(page);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PartOverload_ReadsLikeTheSameTextAsAStandaloneFile(string delimiter, bool header, int skip, string encodingKey)
    {
        var (encoding, name) = EncodingFor(encodingKey);
        var text = PartText(delimiter[0], header, skip);
        var part = WritePart("whole.txt", Before, text, After, encoding, name, delimiter, header, skip);
        // The standalone copy has no BOM, so both reads decode with the same explicit encoding.
        var noBom = encodingKey switch { "utf-16le" => new UnicodeEncoding(false, false), "utf-16be" => new UnicodeEncoding(true, false), "utf-8-bom" => new UTF8Encoding(false), _ => encoding };
        var standalone = WriteStandalone("standalone.csv", text, noBom);

        var (partOutcome, partRecords) = await ReadPart(part);
        var (pathOutcome, pathRecords) = await ReadPath(new CsvFileSettings(standalone, delimiter, header, skip, name));

        Assert.Null(partOutcome.SettingsError);
        Assert.Null(partOutcome.Failure);
        Assert.False(partOutcome.LicenceLimitReached);
        Assert.Equal(pathOutcome.Columns, partOutcome.Columns);
        Assert.Equal(pathOutcome.RecordsRead, partOutcome.RecordsRead);
        Assert.Equal(pathRecords.Select(r => (r.RecordNumber, string.Join("|", r.Values))), partRecords.Select(r => (r.RecordNumber, string.Join("|", r.Values))));
        Assert.Equal(pathOutcome.StructuralErrors.Select(e => e.RecordNumber), partOutcome.StructuralErrors.Select(e => e.RecordNumber));
        Assert.All(pathRecords, r => Assert.Null(r.FileLine));
        if (delimiter == "\t" && skip > 0)
        {
            // Found in G6a [P]: with a tab delimiter and skip lines, ETLBox reads every line as ONE
            // column, through the path overload as well (existing behaviour, not changed here).
            Assert.Single(partOutcome.Columns);
            return;
        }

        Assert.Contains(partRecords, r => r.Values.Contains("Äpfel"));
        Assert.Contains(partRecords, r => r.Values.Contains("two\nlines"));
        Assert.All(pathRecords, r => Assert.Null(r.FileLine));

        // File lines: the part starts on line 5; record 1 after the skip lines and the header; record 2 has two lines.
        var first = 5 + skip + (header ? 1 : 0);
        Assert.Equal(first, partRecords[0].FileLine);
        Assert.Equal(first + 1, partRecords[1].FileLine);
        Assert.Equal(first + 3, partRecords[2].FileLine);
        var bad = Assert.Single(partOutcome.StructuralErrors);
        Assert.Equal(first + 4, bad.FileLine);
        Assert.Contains("de\"l\"ta", bad.ErrorText);
    }

    [Fact]
    public async Task PartAtTheStartOfABomFile_StartsAfterTheBom_AndAPartEndingAtTheLastByteReadsToTheEnd()
    {
        var text = PartText(',', true, 0);
        var part = WritePart("bom.txt", "", text, "", new UTF8Encoding(true), "utf-8", ",", true, 0);
        Assert.Equal(3, part.StartByte);
        Assert.Equal(new FileInfo(part.Path).Length, part.StartByte + part.Length);

        var (outcome, records) = await ReadPart(part);
        Assert.Equal(["id", "name", "note"], outcome.Columns);
        Assert.Equal(4, records.Count);
        Assert.Equal("5|e|last", string.Join("|", records[^1].Values));
        Assert.Equal(2, records[0].FileLine);
    }

    [Fact]
    public async Task PartLongerThanTheFile_GivesThePlainMessage_AndAMissingFileTheUsualOne()
    {
        var part = WritePart("short.txt", Before, PartText(',', true, 0), After, new UTF8Encoding(false), "utf-8", ",", true, 0);
        var (tooLong, _) = await ReadPart(part with { Length = part.Length + 10_000 });
        Assert.Equal(CsvRecordReader.PartChangedMessage, tooLong.SettingsError);

        var (missing, _) = await ReadPart(part with { Path = Path.Combine(_folder, "nope.txt") });
        Assert.Equal("File was not found. Check the path and try again.", missing.SettingsError);
    }

    [Fact]
    public async Task AfterAPartRead_TheFileIsNotLocked_AndCanBeDeleted()
    {
        var part = WritePart("lock.txt", Before, PartText(',', true, 1), After, new UTF8Encoding(false), "utf-8", ",", true, 1);
        var before = File.ReadAllBytes(part.Path);
        var (outcome, _) = await ReadPart(part);
        Assert.Null(outcome.SettingsError);
        Assert.Equal(before, File.ReadAllBytes(part.Path));
        using (new FileStream(part.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }

        File.Delete(part.Path);
        Assert.False(File.Exists(part.Path));
    }

    [Fact]
    public async Task StoppingEarly_ClosesThePart()
    {
        var part = WritePart("stop.txt", Before, PartText(',', true, 0, rows: 50), After, new UTF8Encoding(false), "utf-8", ",", true, 0);
        var seen = 0;
        var outcome = await TestTimeout.Run(() => new CsvRecordReader().ReadAsync(part, _ => ++seen < 3, CancellationToken.None));
        Assert.True(outcome.RecordsRead >= 3);
        File.Delete(part.Path);
    }

    // ---- PartStream --------------------------------------------------------------------------------

    private string Bytes(string name, int count)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, Enumerable.Range(0, count).Select(i => (byte)(i % 251)).ToArray());
        return path;
    }

    [Fact]
    public void PartStream_ReturnsExactlyTheRange_AndThenNothing()
    {
        var path = Bytes("range.bin", 10_000);
        using (var stream = new PartStream(path, 1234, 5000))
        {
            var buffer = new byte[20_000];
            var total = 0;
            int n;
            while ((n = stream.Read(buffer, total, 777)) > 0)
            {
                total += n;
            }

            Assert.Equal(5000, total);
            Assert.Equal(File.ReadAllBytes(path).AsSpan(1234, 5000).ToArray(), buffer.AsSpan(0, total).ToArray());
            Assert.Equal(5000, stream.Position);
            Assert.Equal(0, stream.Read(buffer, 0, 10));
        }

        File.Delete(path);
    }

    [Fact]
    public void PartStream_IsReadOnlyAndForwardOnly()
    {
        var path = Bytes("forward.bin", 100);
        using (var stream = new PartStream(path, 10, 20))
        {
            Assert.True(stream.CanRead);
            Assert.False(stream.CanSeek);
            Assert.False(stream.CanWrite);
            Assert.Equal(PartStream.ForwardOnlyMessage, Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin)).Message);
            Assert.Throws<NotSupportedException>(() => stream.Position = 0);
            Assert.Throws<NotSupportedException>(() => stream.Length);
            Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
            Assert.Throws<NotSupportedException>(() => stream.SetLength(5));
        }

        File.Delete(path);
    }

    [Fact]
    public async Task PartStream_ZeroLength_ReadsNothing_AndAsyncReadsTheSameBytes()
    {
        var path = Bytes("zero.bin", 100);
        using (var empty = new PartStream(path, 100, 0))
        {
            Assert.Equal(0, empty.Read(new byte[10], 0, 10));
        }

        using (var stream = new PartStream(path, 90, 10))
        {
            var buffer = new byte[64];
            Assert.Equal(10, await stream.ReadAsync(buffer.AsMemory()));
            Assert.Equal(File.ReadAllBytes(path)[90..], buffer[..10]);
        }

        File.Delete(path);
    }

    [Fact]
    public void PartStream_Dispose_ClosesTheFile_AndOthersCanReadMeanwhile()
    {
        var path = Bytes("dispose.bin", 100);
        var stream = new PartStream(path, 0, 100);
        // FileShare.Read: another reader is allowed while the part is open, a writer is not.
        using (File.OpenRead(path))
        {
        }

        Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        stream.Dispose();
        File.Delete(path);
        Assert.False(File.Exists(path));
    }
}
