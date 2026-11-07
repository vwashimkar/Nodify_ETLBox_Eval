using System.IO;
using System.Security.Cryptography;
using System.Text;
using EtlDesigner.Engine.Intake;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>UTF-16 with a BOM: the BOM decides, offsets stay offsets in the original file.</summary>
public class Utf16IntakeTests
{
    private const string Text = "alpha\r\nbeta,1\n\nγ δ\rlast";

    private sealed record Line(long Number, long Start, long End, bool Blank, string Head, bool Whole);

    private static (IntakeFileInfo? File, List<Line> Lines, string? Error) Scan(IntakeItem item)
    {
        var lines = new List<Line>();
        var outcome = LineIndexer.Scan(item, 500, "utf-8",
            (in IntakeLine line) => lines.Add(new Line(line.Number, line.StartByte, line.EndByte, line.IsBlank, Encoding.UTF8.GetString(line.Head), line.HeadIsWholeLine)),
            () => 0, null, CancellationToken.None);
        return (outcome.File, lines, outcome.Error);
    }

    private static byte[] Bytes(Encoding encoding, bool bom)
        => bom ? [.. encoding.GetPreamble(), .. encoding.GetBytes(Text)] : encoding.GetBytes(Text);

    public static TheoryData<string> Encodings => new() { "utf-8", "utf-16le", "utf-16be" };

    private static byte[] BytesFor(string name) => name switch
    {
        "utf-16le" => Bytes(new UnicodeEncoding(false, true), bom: true),
        "utf-16be" => Bytes(new UnicodeEncoding(true, true), bom: true),
        _ => Bytes(new UTF8Encoding(false), bom: false)
    };

    [Theory]
    [MemberData(nameof(Encodings))]
    public void SameText_GivesTheSameLines_InEveryEncoding(string encoding)
    {
        var bytes = BytesFor(encoding);
        var (file, lines, error) = Scan(IntakeTestData.MemoryItem(bytes));

        Assert.Null(error);
        Assert.Equal(encoding, file!.Encoding);
        Assert.Equal(encoding != "utf-8", file.HasBom);
        Assert.Equal(LineEndingKind.Mixed, file.LineEnding);
        Assert.Equal(5, file.LineCount);
        Assert.Equal(bytes.Length, file.Size);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Sha256);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, lines.Select(l => l.Number));
        Assert.Equal(new[] { "alpha", "beta,1", "", "γ δ", "last" }, lines.Select(l => l.Head));
        Assert.Equal(new[] { false, false, true, false, false }, lines.Select(l => l.Blank));
        Assert.All(lines, l => Assert.True(l.Whole));

        var readBack = lines.Select(l => IntakeReader.ReadLines(IntakeTestData.MemoryItem(bytes), l.Start, l.End, 1, file.Encoding)).ToList();
        Assert.All(readBack, r => Assert.Null(r.Error));
        Assert.Equal(lines.Select(l => l.Head), readBack.Select(r => r.Lines.Single()));
    }

    [Fact]
    public void Utf16_OffsetsCountTheBomAndTwoBytesPerCharacter()
    {
        var (_, utf8, _) = Scan(IntakeTestData.MemoryItem(BytesFor("utf-8")));
        var (_, le, _) = Scan(IntakeTestData.MemoryItem(BytesFor("utf-16le")));
        var (_, be, _) = Scan(IntakeTestData.MemoryItem(BytesFor("utf-16be")));

        Assert.Equal((0L, 5L), (utf8[0].Start, utf8[0].End));
        Assert.Equal((2L, 12L), (le[0].Start, le[0].End));
        Assert.Equal((16L, 28L), (le[1].Start, le[1].End));
        Assert.Equal(le.Select(l => (l.Start, l.End)), be.Select(l => (l.Start, l.End)));
        Assert.NotEqual(utf8.Select(l => l.Start), le.Select(l => l.Start));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Utf16_ReadInOddPieces_GivesTheSameLines(int chunk)
    {
        var bytes = BytesFor("utf-16le");

        var whole = Scan(IntakeTestData.MemoryItem(bytes));
        var pieces = Scan(LineIndexerTests.ChunkedItem(bytes, chunk));

        Assert.Equal(whole.Lines, pieces.Lines);
        Assert.Equal(whole.File, pieces.File! with { Name = whole.File!.Name });
    }

    [Fact]
    public void Utf16WithoutABom_IsRefused_WithAMessageThatSaysSo()
    {
        var (file, _, error) = Scan(IntakeTestData.MemoryItem(Encoding.Unicode.GetBytes("ab\ncd")));

        Assert.Null(file);
        Assert.Equal(LineIndexer.NoBomMessage, error);
        Assert.Contains("BOM", error);
    }

    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-32")]
    public void Utf16WithoutAByteOrder_OrUtf32_AsASetting_IsAnUnknownName(string name)
    {
        var settings = IntakeTestData.Markers with { Encoding = name };

        Assert.Equal(IntakeEncodings.UnknownMessage, IntakeReader.Read(IntakeTestData.MemoryItem("a"), settings, null, CancellationToken.None).Error);
    }

    [Fact]
    public void RealUtf16File_Decodes_AndHas53Lines()
    {
        var item = new FileIntakeFactory().CreateItem(Path.Combine(RealIntakeFileTests.Folder, "r02_moldev_softmax_pro_utf16.txt"));
        var (file, lines, error) = Scan(item);

        Assert.Null(error);
        Assert.Equal("utf-16le", file!.Encoding);
        Assert.Equal(53, file.LineCount);
        Assert.Equal("##BLOCKS= 2", lines[0].Head);
        Assert.Equal("##BLOCKS= 2", IntakeReader.ReadLines(item, lines[0].Start, lines[0].End, 1, file.Encoding).Lines.Single());
        Assert.StartsWith("Original Filename:", lines[52].Head);
    }
}
