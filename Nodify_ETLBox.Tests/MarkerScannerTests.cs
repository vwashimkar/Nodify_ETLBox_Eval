using System.IO;
using EtlDesigner.Engine.Intake;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class MarkerScannerTests
{
    public static TheoryData<string> MarkerFiles()
    {
        var data = new TheoryData<string>();
        foreach (var name in IntakeTestData.MarkerFiles)
        {
            data.Add(name);
        }

        return data;
    }

    private static string[] ToRow(ContainerInfo c) =>
    [
        c.Order.ToString(),
        c.Kind == ContainerKind.Marker ? "marker" : "gap_text",
        c.Format,
        c.Name,
        c.Notes,
        c.MarkerBeginLine?.ToString() ?? string.Empty,
        c.MarkerEndLine?.ToString() ?? string.Empty,
        c.ContentFirstLine?.ToString() ?? string.Empty,
        c.ContentLastLine?.ToString() ?? string.Empty,
        c.ContentLineCount.ToString(),
        c.ContentStartByte?.ToString() ?? string.Empty,
        c.ContentEndByte?.ToString() ?? string.Empty,
        c.Status.ToString(),
        c.Message
    ];

    [Theory]
    [InlineData("i01_sample", 6487L)]
    [InlineData("i02_crlf", 6706L)]
    [InlineData("i03_bom", 6490L)]
    public void TestFile_HasItsExactLineEnds(string name, long size)
    {
        // git can change line ends on checkout; .gitattributes keeps them, and this says so if it fails.
        Assert.Equal(size, new FileInfo(IntakeTestData.PathOf(name)).Length);
    }

    [Theory]
    [MemberData(nameof(MarkerFiles))]
    public void EveryColumn_MatchesTheExpectedFile(string name)
    {
        var expected = IntakeTestData.ReadCsv(Path.Combine(IntakeTestData.Folder, "expected", name + "_segments.csv"));
        var header = expected[0];
        var rows = expected.Skip(1).ToList();

        var result = IntakeTestData.ReadMarkers(name);

        Assert.Null(result.Error);
        Assert.False(result.Cancelled);
        Assert.Equal(rows.Count, result.Containers.Count);

        for (var row = 0; row < rows.Count; row++)
        {
            var actual = ToRow(result.Containers[row]);
            for (var column = 0; column < header.Length; column++)
            {
                Assert.True(
                    rows[row][column] == actual[column],
                    $"{name} row {row + 1} column {header[column]}: expected '{rows[row][column]}', got '{actual[column]}'");
            }
        }
    }

    [Fact]
    public void I06_TheStrayEnd_IsAFileLevelWarning()
    {
        var expected = IntakeTestData.ReadCsv(Path.Combine(IntakeTestData.Folder, "expected", "i06_stray_end_warnings.csv"));

        var warning = Assert.Single(IntakeTestData.ReadMarkers("i06_stray_end").Warnings);

        Assert.Equal(2, expected.Count);
        Assert.Equal(expected[1][0], warning.Line.ToString());
        Assert.Equal(expected[1][1], warning.Message);
    }

    [Theory]
    [InlineData("i01_sample")]
    [InlineData("i04_unterminated_last")]
    [InlineData("i05_missing_end_before_next_begin")]
    [InlineData("i07_edge_cases")]
    public void OtherFiles_HaveNoFileLevelWarning(string name)
    {
        Assert.Empty(IntakeTestData.ReadMarkers(name).Warnings);
    }

    [Theory]
    [MemberData(nameof(MarkerFiles))]
    public void MarkerContainers_AreSure(string name)
    {
        Assert.All(IntakeTestData.ReadMarkers(name).Containers, c => Assert.Equal(ContainerConfidence.Sure, c.Confidence));
    }

    [Fact]
    public void I01_TheTrapLinesInTheIntro_StayText()
    {
        var intro = IntakeTestData.ReadMarkers("i01_sample").Containers[0];

        Assert.Equal(ContainerKind.GapText, intro.Kind);
        Assert.Equal(1, intro.ContentFirstLine);
        Assert.Equal(12, intro.ContentLastLine);
    }

    [Theory]
    [InlineData("##### BEGIN <format> | a | b #####")]
    [InlineData("##### BEGIN two words | a | b #####")]
    [InlineData("##### BEGIN #####")]
    [InlineData("##### BEGIN  #####")]
    [InlineData("##### BEGIN | a | b #####")]
    [InlineData("##### BEGIN csv | a | b ####")]
    [InlineData("##### Begin csv | a | b #####")]
    public void NotAMarker_StaysText(string line)
    {
        var container = Assert.Single(IntakeTestData.ReadMarkers(IntakeTestData.MemoryItem(line + "\n")).Containers);

        Assert.Equal(ContainerKind.GapText, container.Kind);
    }

    [Fact]
    public void MissingName_CountsTheSegmentsOfThatFormat()
    {
        const string text = "##### BEGIN json | named #####\n{}\n##### END #####\n##### BEGIN json #####\n{}\n##### END #####\n";

        Assert.Equal("json-2", IntakeTestData.ReadMarkers(IntakeTestData.MemoryItem(text)).Containers[1].Name);
    }

    [Fact]
    public void Markers_WithALineEndingInTheLastLine_AndNoNewLine_StillClose()
    {
        var result = IntakeTestData.ReadMarkers(IntakeTestData.MemoryItem("##### BEGIN csv | a #####\nx,y\n##### END #####"));

        var container = Assert.Single(result.Containers);
        Assert.Equal(ContainerStatus.Ok, container.Status);
        Assert.Equal(3, container.MarkerEndLine);
    }

    [Fact]
    public void UnterminatedAndEmpty_AtTheEnd_IsAnError_WithBothMessages()
    {
        var container = Assert.Single(IntakeTestData.ReadMarkers(IntakeTestData.MemoryItem("##### BEGIN csv | a #####\n")).Containers);

        Assert.Equal(ContainerStatus.Error, container.Status);
        Assert.Equal("empty segment; no END marker; the segment runs to the end of the file", container.Message);
    }

    [Fact]
    public void CustomProfile_IsUsed()
    {
        var settings = new IntakeSettings
        {
            Mode = IntakeMode.Markers,
            Markers = new MarkerProfile { BeginPrefix = "== ", BeginSuffix = " ==", EndLine = "-- end --", FieldSeparator = ';' }
        };

        var result = IntakeReader.Read(IntakeTestData.MemoryItem("== csv ; run 1 ==\na,b\n-- end --\n"), settings, null, CancellationToken.None);

        var container = Assert.Single(result.Containers);
        Assert.Equal("run 1", container.Name);
        Assert.Equal(3, container.MarkerEndLine);
    }

    [Theory]
    [MemberData(nameof(MarkerFiles))]
    public void NoLineIsLostOrDuplicated(string name)
    {
        var lines = IntakeTestData.SplitLines(name);
        var result = IntakeTestData.ReadMarkers(name);
        var uses = new int[lines.Count + 1];

        foreach (var container in result.Containers)
        {
            if (container.MarkerBeginLine is { } begin)
            {
                uses[begin]++;
            }

            if (container.MarkerEndLine is { } end)
            {
                uses[end]++;
            }

            if (container.ContentFirstLine is { } first)
            {
                for (var number = first; number <= container.ContentLastLine; number++)
                {
                    uses[number]++;
                }
            }
        }

        for (var number = 1; number <= lines.Count; number++)
        {
            Assert.True(uses[number] <= 1, $"{name} line {number} is used {uses[number]} times");

            if (uses[number] == 0)
            {
                Assert.True(string.IsNullOrWhiteSpace(lines[number - 1].Text), $"{name} line {number} is lost: '{lines[number - 1].Text}'");
            }
        }

        var firstLines = result.Containers.Select(c => c.MarkerBeginLine ?? c.ContentFirstLine).ToList();
        Assert.Equal(firstLines.Order().ToList(), firstLines);
    }

    [Theory]
    [MemberData(nameof(MarkerFiles))]
    public void ReadLines_GivesExactlyTheContentLines(string name)
    {
        var lines = IntakeTestData.SplitLines(name);
        var item = IntakeTestData.Item(name);

        foreach (var container in IntakeTestData.ReadMarkers(item).Containers.Where(c => c.ContentLineCount > 0))
        {
            var first = (int)container.ContentFirstLine!.Value - 1;
            var expected = lines.Skip(first).Take((int)container.ContentLineCount).ToList();

            var read = IntakeReader.ReadLines(item, container.ContentStartByte!.Value, container.ContentEndByte!.Value, int.MaxValue);

            Assert.Null(read.Error);
            Assert.Equal(expected.Select(l => l.Text), read.Lines);
            Assert.Equal(expected[0].Start, container.ContentStartByte);
            Assert.Equal(expected[^1].End, container.ContentEndByte);
        }
    }

    [Fact]
    public void ReadLines_KeepsTrailingTabAndSpaces()
    {
        var item = IntakeTestData.Item("i07_edge_cases");
        var container = IntakeTestData.ReadMarkers(item).Containers.Single(c => c.Name == "spaces-kept");

        var read = IntakeReader.ReadLines(item, container.ContentStartByte!.Value, container.ContentEndByte!.Value, 10);

        Assert.Equal(["x ,y\t", "  leading,spaces  "], read.Lines);
    }

    [Fact]
    public void ReadLines_LfCrLfAndBom_GiveTheSameText_AtDifferentOffsets()
    {
        static List<(long Start, IReadOnlyList<string> Lines)> Contents(string name)
        {
            var item = IntakeTestData.Item(name);
            return IntakeTestData.ReadMarkers(item).Containers
                .Select(c => (c.ContentStartByte!.Value, IntakeReader.ReadLines(item, c.ContentStartByte!.Value, c.ContentEndByte!.Value, int.MaxValue).Lines))
                .ToList();
        }

        var lf = Contents("i01_sample");
        var crLf = Contents("i02_crlf");
        var bom = Contents("i03_bom");

        Assert.Equal(lf.Count, crLf.Count);
        Assert.Equal(lf.Count, bom.Count);

        for (var i = 0; i < lf.Count; i++)
        {
            Assert.Equal(lf[i].Lines, crLf[i].Lines);
            Assert.Equal(lf[i].Lines, bom[i].Lines);
            Assert.Equal(lf[i].Start + 3, bom[i].Start);
        }

        Assert.NotEqual(lf[^1].Start, crLf[^1].Start);
    }

    [Fact]
    public void ReadLines_ARangeEndingInABlankLine_KeepsThatLine()
    {
        var item = IntakeTestData.Item("i05_missing_end_before_next_begin");
        var container = IntakeTestData.ReadMarkers(item).Containers.Single(c => c.Name == "json-object");

        var read = IntakeReader.ReadLines(item, container.ContentStartByte!.Value, container.ContentEndByte!.Value, 100);

        Assert.Equal(14, read.Lines.Count);
        Assert.Equal(string.Empty, read.Lines[^1]);
    }

    [Fact]
    public void ReadLines_StopsAtMaxLines()
    {
        var item = IntakeTestData.Item("i01_sample");

        Assert.Equal(["This file is a multi-format parser test sample."], IntakeReader.ReadLines(item, 0, 605, 1).Lines);
        Assert.Empty(IntakeReader.ReadLines(item, 0, 605, 0).Lines);
    }

    [Fact]
    public void ReadLines_ProblemsComeBackAsAMessage()
    {
        var missing = new FileIntakeFactory().CreateItem(Path.Combine(IntakeTestData.Folder, "no_such_file.txt"));

        Assert.Equal(LineIndexer.NotFoundMessage, IntakeReader.ReadLines(missing, 0, 10, 5).Error);
        Assert.Contains("not valid", IntakeReader.ReadLines(IntakeTestData.Item("i01_sample"), 10, 5, 5).Error);
    }

    [Fact]
    public void ReadLines_DecodesWithTheEncodingGiven()
    {
        var item = IntakeTestData.MemoryItem(new byte[] { 0x4D, 0xE9, 0x0A });

        Assert.Equal(["Mé"], IntakeReader.ReadLines(item, 0, 2, 5, "windows-1252").Lines);
    }

    [Fact]
    public void AutoDetect_FindsNoContainersYet_AndSaysSo()
    {
        var result = IntakeReader.Read(IntakeTestData.Item("i01_sample"), new IntakeSettings(), null, CancellationToken.None);

        Assert.Empty(result.Containers);
        Assert.Null(result.Error);
        Assert.Contains("auto-detection comes in the next stage", Assert.Single(result.Warnings).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(219, result.File!.LineCount);
    }

    [Fact]
    public void MarkersThenAutoDetect_GivesTheMarkers_AndTheSameMessage()
    {
        var settings = new IntakeSettings { Mode = IntakeMode.MarkersThenAutoDetect };

        var result = IntakeReader.Read(IntakeTestData.Item("i01_sample"), settings, null, CancellationToken.None);

        Assert.Equal(19, result.Containers.Count);
        Assert.Contains(result.Warnings, w => w.Message == IntakeReader.AutoDetectPendingMessage);
    }
}
