using System.IO;
using System.Security.Cryptography;
using System.Text;
using EtlDesigner.Engine.Intake;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class LineIndexerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "intake_indexer_" + Guid.NewGuid().ToString("N"));

    public LineIndexerTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static List<(long Number, long Start, long End, bool Blank)> Index(IntakeItem item, out LineIndexOutcome outcome)
    {
        var lines = new List<(long, long, long, bool)>();
        outcome = LineIndexer.Scan(
            item,
            500,
            "utf-8",
            (in IntakeLine line) => lines.Add((line.Number, line.StartByte, line.EndByte, line.IsBlank)),
            () => 0,
            null,
            CancellationToken.None);
        return lines;
    }

    private static IntakeItem ChunkedItem(byte[] bytes, int chunk)
        => new(Guid.NewGuid(), "memory", "chunked.txt", bytes.Length, DateTime.UtcNow, () => new ChunkedStream(bytes, chunk));

    [Fact]
    public void LfCrLfAndCr_AreLineEnds_WithExactOffsets()
    {
        var lines = Index(IntakeTestData.MemoryItem("ab\ncd\r\nef\rgh"), out var outcome);

        Assert.Equal([(1L, 0L, 2L, false), (2L, 3L, 5L, false), (3L, 7L, 9L, false), (4L, 10L, 12L, false)], lines);
        Assert.Equal(LineEndingKind.Mixed, outcome.File!.LineEnding);
        Assert.Equal(4, outcome.File.LineCount);
    }

    [Theory]
    [InlineData("a\nb\n", LineEndingKind.Lf, 2)]
    [InlineData("a\r\nb\r\n", LineEndingKind.CrLf, 2)]
    [InlineData("a\rb\r", LineEndingKind.Cr, 2)]
    [InlineData("a", LineEndingKind.None, 1)]
    [InlineData("\n", LineEndingKind.Lf, 1)]
    [InlineData("\n\n", LineEndingKind.Lf, 2)]
    [InlineData("\r\n\r", LineEndingKind.Mixed, 2)]
    [InlineData("a\n ", LineEndingKind.Lf, 2)]
    public void LineEndingKind_AndLineCount(string text, LineEndingKind kind, long count)
    {
        Index(IntakeTestData.MemoryItem(text), out var outcome);

        Assert.Equal(kind, outcome.File!.LineEnding);
        Assert.Equal(count, outcome.File.LineCount);
    }

    [Fact]
    public void BlankMeansSpacesAndTabsOnly()
    {
        var lines = Index(IntakeTestData.MemoryItem("\n  \t\n x\n"), out _);

        Assert.Equal([true, true, false], lines.Select(l => l.Blank));
    }

    [Fact]
    public void ABom_IsSkipped_ButCounted()
    {
        var lines = Index(IntakeTestData.MemoryItem(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\n' }), out var outcome);

        Assert.Equal((1L, 3L, 4L, false), Assert.Single(lines));
        Assert.True(outcome.File!.HasBom);
    }

    [Theory]
    [InlineData("i01_sample")]
    [InlineData("i02_crlf")]
    [InlineData("i03_bom")]
    [InlineData("i07_edge_cases")]
    public void AChunkOfOneByte_GivesTheSameResult_AsAWholeFile(string name)
    {
        var bytes = File.ReadAllBytes(IntakeTestData.PathOf(name));
        var whole = IntakeTestData.ReadMarkers(IntakeTestData.MemoryItem(bytes));

        var chunked = IntakeTestData.ReadMarkers(ChunkedItem(bytes, 1));

        Assert.Equal(whole.Containers, chunked.Containers);
        Assert.Equal(whole.File with { Name = "x" }, chunked.File! with { Name = "x" });
    }

    [Fact]
    public void ACrLfAcrossTwoBlocks_IsOneLineEnd()
    {
        var bytes = Encoding.ASCII.GetBytes(new string('a', LineIndexer.BlockSize - 1) + "\r\nb\n");

        var lines = Index(IntakeTestData.MemoryItem(bytes), out var outcome);

        Assert.Equal(2, lines.Count);
        Assert.Equal(LineIndexer.BlockSize + 1, lines[1].Start);
        Assert.Equal(LineEndingKind.Mixed, outcome.File!.LineEnding);
    }

    [Fact]
    public void AVeryLongLine_IsIndexed_WithACutHead()
    {
        var bytes = Encoding.ASCII.GetBytes("##### END #####" + new string(' ', LineIndexer.HeadCapacity) + "x\nb\n");

        var lines = new List<(int Head, bool Whole)>();
        LineIndexer.Scan(IntakeTestData.MemoryItem(bytes), 500, "utf-8", (in IntakeLine line) => lines.Add((line.Head.Length, line.HeadIsWholeLine)), () => 0, null, CancellationToken.None);

        Assert.Equal([(LineIndexer.HeadCapacity, false), (1, true)], lines);
    }

    [Fact]
    public void Sha256_IsTheHashOfTheWholeFile()
    {
        var path = IntakeTestData.PathOf("i03_bom");

        var result = IntakeTestData.ReadMarkers("i03_bom");

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), result.File!.Sha256);
    }

    [Theory]
    [InlineData("i01_sample", "utf-8", false, LineEndingKind.Lf)]
    [InlineData("i02_crlf", "utf-8", false, LineEndingKind.CrLf)]
    [InlineData("i03_bom", "utf-8", true, LineEndingKind.Lf)]
    public void FileInfo_IsFilled(string name, string encoding, bool bom, LineEndingKind lineEnding)
    {
        var file = IntakeTestData.ReadMarkers(name).File!;

        Assert.Equal(name + ".txt", file.Name);
        Assert.Equal(new FileInfo(IntakeTestData.PathOf(name)).Length, file.Size);
        Assert.Equal(encoding, file.Encoding);
        Assert.Equal(bom, file.HasBom);
        Assert.Equal(lineEnding, file.LineEnding);
        Assert.Equal(219, file.LineCount);
    }

    [Fact]
    public void MissingFile_GivesAMessage_NoException()
    {
        var result = IntakeTestData.ReadMarkers(new FileIntakeFactory().CreateItem(Path.Combine(_folder, "missing.txt")));

        Assert.Equal(LineIndexer.NotFoundMessage, result.Error);
        Assert.Empty(result.Containers);
        Assert.Null(result.File);
    }

    [Fact]
    public void MissingFolder_GivesAMessage_NoException()
    {
        var result = IntakeTestData.ReadMarkers(new FileIntakeFactory().CreateItem(Path.Combine(_folder, "no", "missing.txt")));

        Assert.Equal(LineIndexer.NotFoundMessage, result.Error);
    }

    [Fact]
    public void UnreadableFile_GivesAMessage_NoException()
    {
        var path = Path.Combine(_folder, "locked.txt");
        File.WriteAllText(path, "text");
        using var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = IntakeTestData.ReadMarkers(new FileIntakeFactory().CreateItem(path));

        Assert.Equal(LineIndexer.UnreadableMessage, result.Error);
    }

    [Fact]
    public void AFolderInsteadOfAFile_GivesAMessage_NoException()
    {
        var result = IntakeTestData.ReadMarkers(new FileIntakeFactory().CreateItem(_folder));

        Assert.Equal(LineIndexer.UnreadableMessage, result.Error);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF })]
    public void EmptyFile_GivesAMessage_NoException(byte[] bytes)
    {
        var path = Path.Combine(_folder, "empty.txt");
        File.WriteAllBytes(path, bytes);

        var result = IntakeTestData.ReadMarkers(new FileIntakeFactory().CreateItem(path));

        Assert.Equal(LineIndexer.EmptyMessage, result.Error);
        Assert.Empty(result.Containers);
    }

    [Fact]
    public void FileAboveTheLimit_GivesAMessage_AndIsNotRead()
    {
        var path = Path.Combine(_folder, "big.txt");
        File.WriteAllBytes(path, new byte[1024 * 1024 + 1]);
        var settings = IntakeTestData.Markers with { MaxFileSizeMb = 1 };

        var result = IntakeReader.Read(new FileIntakeFactory().CreateItem(path), settings, null, CancellationToken.None);

        Assert.Contains("above the limit of 1 MB", result.Error);
        Assert.Contains("Raise the limit", result.Error);
    }

    [Fact]
    public void AFileOfExactlyTheLimit_IsRead()
    {
        var bytes = new byte[1024 * 1024];
        var settings = IntakeTestData.Markers with { MaxFileSizeMb = 1 };

        Assert.Null(IntakeReader.Read(IntakeTestData.MemoryItem(bytes), settings, null, CancellationToken.None).Error);
    }

    [Fact]
    public void Utf16File_GivesAMessage()
    {
        Assert.Equal(LineIndexer.Utf16Message, IntakeTestData.ReadMarkers(IntakeTestData.MemoryItem(new byte[] { 0xFF, 0xFE, 0x61, 0x00 })).Error);
    }

    [Theory]
    [InlineData("no-such-encoding", "Encoding name is unknown")]
    [InlineData("utf-16", "UTF-16")]
    public void BadEncodingSetting_GivesAMessage(string encoding, string message)
    {
        var settings = IntakeTestData.Markers with { EncodingName = encoding };

        var result = IntakeReader.Read(IntakeTestData.Item("i01_sample"), settings, null, CancellationToken.None);

        Assert.Contains(message, result.Error);
    }

    [Fact]
    public void ATokenCancelledBeforeTheStart_DoesNotOpenTheFile()
    {
        var opened = false;
        var item = IntakeTestData.MemoryItem("a\n") with
        {
            OpenRead = () =>
            {
                opened = true;
                return new MemoryStream();
            }
        };

        var result = IntakeReader.Read(item, IntakeTestData.Markers, null, new CancellationToken(canceled: true));

        Assert.True(result.Cancelled);
        Assert.False(opened);
    }

    private sealed class ChunkedStream(byte[] data, int chunk) : MemoryStream(data, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(chunk, count));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(chunk, buffer.Length)]);
    }
}
