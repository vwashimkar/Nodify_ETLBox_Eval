using System.IO;
using System.Text;
using EtlDesigner.Engine.Intake;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>Task G4a step 1: the encoding setting, Auto's guess and the UTF-8 check in the same pass.</summary>
public class IntakeEncodingTests
{
    /// <summary>"Size: 25 µm" and two more key-value lines in windows-1252: the micro sign is the single byte 0xB5.</summary>
    private static readonly byte[] Windows1252Bytes =
        [.. "Name: probe\nSize: 25 "u8, 0xB5, .. "m\nPlace: lab\n"u8];

    private static IntakeResult Read(byte[] bytes, string encoding, IntakeMode mode = IntakeMode.AutoDetect)
        => IntakeReader.Read(IntakeTestData.MemoryItem(bytes), new IntakeSettings { Mode = mode, Encoding = encoding }, null, CancellationToken.None);

    private static IntakeValidationResult Validate(byte[] bytes, IntakeResult result)
        => IntakeValidator.Validate(IntakeTestData.MemoryItem(bytes), result, null, CancellationToken.None);

    [Fact]
    public void Windows1252Text_InAuto_ReadsTheMicroSign_AndIsGuessed()
    {
        var result = Read(Windows1252Bytes, "auto");

        Assert.Null(result.Error);
        Assert.Equal(("windows-1252", true, true), (result.EncodingUsed, result.EncodingGuessed, result.InvalidUtf8Seen));
        Assert.Equal("windows-1252", result.File!.Encoding);
        Assert.Equal(IntakeEncodings.GuessReason, result.EncodingReason);
        var lines = IntakeReader.ReadLines(IntakeTestData.MemoryItem(Windows1252Bytes), 0, Windows1252Bytes.Length - 1, 10, result.EncodingUsed!);
        Assert.Equal("Size: 25 µm", lines.Lines[1]);
        Assert.Equal(0, Validate(Windows1252Bytes, result).Containers.Sum(c => c.Issues.Count(i => i.Code == "encoding.bad-characters")));
    }

    [Fact]
    public void Windows1252Text_ReadAsUtf8_GivesTheBadCharacterWarning()
    {
        var result = Read(Windows1252Bytes, "utf-8");

        Assert.Equal(("utf-8", false, true), (result.EncodingUsed, result.EncodingGuessed, result.InvalidUtf8Seen));
        Assert.Null(result.EncodingReason);
        var issue = Validate(Windows1252Bytes, result).Containers.SelectMany(c => c.Issues).Single(i => i.Code == "encoding.bad-characters");
        Assert.StartsWith("1 character could not be read correctly", issue.Message);
    }

    [Fact]
    public void AnExplicitWindows1252_IsNeverGuessed()
    {
        var result = Read(Windows1252Bytes, "windows-1252");

        Assert.Equal(("windows-1252", false), (result.EncodingUsed, result.EncodingGuessed));
    }

    [Fact]
    public void ValidUtf8_ReadAsWindows1252_IsGarbled_ButNoError()
    {
        var bytes = "Size: 25 µm\nName: a\nPlace: b\n"u8.ToArray();

        var result = Read(bytes, "windows-1252");

        Assert.Null(result.Error);
        Assert.Equal(("windows-1252", false, false), (result.EncodingUsed, result.EncodingGuessed, result.InvalidUtf8Seen));
        Assert.Equal("Size: 25 Âµm", IntakeReader.ReadLines(IntakeTestData.MemoryItem(bytes), 0, bytes.Length - 1, 1, "windows-1252").Lines[0]);
    }

    [Fact]
    public void AsciiOnly_StaysUtf8_AndIsNotGuessed()
    {
        var result = Read("a,b,c\n1,2,3\n4,5,6\n"u8.ToArray(), "auto");

        Assert.Equal(("utf-8", false, false), (result.EncodingUsed, result.EncodingGuessed, result.InvalidUtf8Seen));
    }

    [Fact]
    public void Utf8WithBom_StaysUtf8()
    {
        var result = Read([0xEF, 0xBB, 0xBF, .. "Size: 25 µm\n"u8], "auto");

        Assert.Equal(("utf-8", false, true), (result.EncodingUsed, result.EncodingGuessed, result.File!.HasBom));
    }

    [Fact]
    public void Utf16WithBom_IsUnchanged()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("a,b\n1,2\n")).ToArray();

        var result = Read(bytes, "auto", IntakeMode.Markers);

        Assert.Equal(("utf-16le", false, true), (result.EncodingUsed, result.EncodingGuessed, result.File!.HasBom));
    }

    [Theory]
    [InlineData("utf-16le", false)]
    [InlineData("utf-16be", true)]
    public void Utf16Setting_ReadsAFileWithoutABom(string name, bool bigEndian)
    {
        var bytes = new UnicodeEncoding(bigEndian, false).GetBytes("first\nsecond\n");

        var result = Read(bytes, name, IntakeMode.Markers);

        Assert.Null(result.Error);
        Assert.Equal((name, false, 2L), (result.EncodingUsed, result.File!.HasBom, result.File.LineCount));
        Assert.Equal("second", IntakeReader.ReadLines(IntakeTestData.MemoryItem(bytes), 12, 24, 1, name).Lines[0]);
    }

    [Theory]
    [InlineData("klingon")]
    [InlineData("utf-32")]
    public void AnUnknownName_GivesAPlainError(string name)
    {
        var result = Read("a\n"u8.ToArray(), name);

        Assert.Equal(IntakeEncodings.UnknownMessage, result.Error);
        Assert.DoesNotContain("Exception", result.Error);
    }

    [Theory]
    [InlineData("AUTO")]
    [InlineData(" Windows-1252 ")]
    public void Names_IgnoreCaseAndSpaces(string name)
    {
        Assert.Null(Read("a\n"u8.ToArray(), name).Error);
    }

    /// <summary>A three-byte character (the euro sign) and a four-byte one at every position that straddles the 64 KB block boundary.</summary>
    [Theory]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 1)]
    [InlineData(4, 2)]
    [InlineData(4, 3)]
    public void AValidCharacterSplitByTheBlockBoundary_IsNotInvalid(int length, int bytesBeforeTheBoundary)
    {
        var character = length == 3 ? "€"u8.ToArray() : "\U0001F600"u8.ToArray();
        var line = Encoding.ASCII.GetBytes(new string('a', 99) + "\n");
        var start = LineIndexer.BlockSize - bytesBeforeTheBoundary;
        var bytes = Enumerable.Repeat(line, start / line.Length + 1).SelectMany(l => l).Take(start).ToList();
        bytes.AddRange(character);
        bytes.AddRange("\nend\n"u8.ToArray());

        var result = Read(bytes.ToArray(), "auto", IntakeMode.Markers);

        Assert.Equal(("utf-8", false, false), (result.EncodingUsed, result.EncodingGuessed, result.InvalidUtf8Seen));
    }

    [Fact]
    public void Utf8Check_CarriesItsStateAcrossOneByteBlocks()
    {
        var valid = "aµ€\U0001F600z"u8.ToArray();
        var check = new Utf8Check();
        foreach (var b in valid)
        {
            check.Append([b]);
        }

        check.Finish();
        Assert.False(check.InvalidSeen);

        var cut = new Utf8Check();
        cut.Append([0xE2, 0x82]);
        cut.Append("a"u8);
        Assert.True(cut.InvalidSeen);

        var open = new Utf8Check();
        open.Append([0xE2, 0x82]);
        open.Finish();
        Assert.True(open.InvalidSeen);
    }

    [Fact]
    public void TheEncodingFields_RoundTripThroughJson()
    {
        var result = Read(Windows1252Bytes, "auto");

        var back = IntakeJson.DeserializeResult(IntakeJson.Serialize(result));

        Assert.Equal((result.EncodingUsed, true, true, result.EncodingReason), (back.EncodingUsed, back.EncodingGuessed, back.InvalidUtf8Seen, back.EncodingReason));
        Assert.Contains("\"encodingUsed\": \"windows-1252\"", IntakeJson.Serialize(result));
    }

    [Fact]
    public void Settings_RoundTrip_TheEncoding()
    {
        Assert.Equal("iso-8859-1", IntakeJson.DeserializeSettings(IntakeJson.Serialize(new IntakeSettings { Encoding = "iso-8859-1" })).Encoding);
    }
}
