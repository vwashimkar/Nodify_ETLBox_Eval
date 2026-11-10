using EtlDesigner.Engine.Intake;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>Task G6a step 2: <see cref="IntakeEditSession.ToTemplate"/> and <see cref="TemplateApplyResult.PartMap"/>.</summary>
public sealed class IntakeTemplatePartMapTests
{
    private static Task<IntakeEditSession> SessionAsync(string text) => IntakeEditSessionTests.SessionAsync(text);

    [Fact]
    public async Task ToTemplate_IsTheSavedTemplate_WithoutStoreOrLog()
    {
        var log = new IntakeStudioOpenTests.MemoryLineage();
        var (item, scan) = await IntakeEditSessionTests.ScanAsync(IntakeTestData.MemoryItem(IntakeTemplateTests.Instrument(5)));
        var s = new IntakeEditSession(item, scan, log, "id");
        Assert.True(s.Rename(1, "Results").Ok);
        var before = log.Events.Count;

        var template = s.ToTemplate("  Lab run ");
        Assert.Equal("Lab run", template.Name);
        Assert.Equal(s.Containers.Count, template.Parts.Count);
        Assert.Equal("Results", template.Parts[1].Name);
        Assert.Equal(["Col1", "Col2", "Col3", "Col4"], template.Parts[1].ColumnNames);
        Assert.Equal(before, log.Events.Count);
        Assert.Throws<ArgumentException>(() => s.ToTemplate(" "));
    }

    [Fact]
    public async Task PartMap_I01Edited_AppliedToACopyWithMoreRows()
    {
        var s = await IntakeEditSessionTests.FileSessionAsync("i01_sample", IntakeMode.AutoDetect);
        var csv = s.Containers.ToList().FindIndex(c => c.Format == "csv" && c.ContentLineCount >= 3);
        Assert.True(s.Rename(csv, "Results table").Ok);
        Assert.True(s.Merge(0).Ok);
        csv = s.Containers.ToList().FindIndex(c => c.Name == "Results table");
        var template = s.ToTemplate("i01 edited");

        var lines = File.ReadAllText(IntakeTestData.PathOf("i01_sample")).Split('\n').ToList();
        var lastRow = (int)s.Containers[csv].ContentLastLine!.Value;
        lines.Insert(lastRow, lines[lastRow - 1]);
        var other = await SessionAsync(string.Join("\n", lines));
        var result = other.ApplyTemplate(template);
        Assert.True(result.Ok, result.Message);

        var map = result.PartMap!;
        Assert.Equal(other.Containers.Count, map.Count);
        Assert.Equal(result.PartsApplied, map.Count(k => k >= 0));
        Assert.Equal([csv], Enumerable.Range(0, map.Count).Where(i => map[i] == csv).ToList());
        Assert.Equal("Results table", other.Containers[map.ToList().IndexOf(csv)].Name);
        for (var i = 0; i < map.Count; i++)
        {
            if (map[i] >= 0)
            {
                Assert.Equal(template.Parts[map[i]].Format, other.Containers[i].Format);
            }
        }

        Assert.True(other.Undo().Ok);
        Assert.False(other.IsModified);
    }

    [Fact]
    public async Task PartMap_RepeatingTemplate_OnePartMakesManyParts()
    {
        static string Repeats(int copies) => string.Join("\n", Enumerable.Range(1, copies).Select(i => IntakeTemplateTests.Instrument(3 + i % 5, seed: i % 7 + 1)));
        var s = await SessionAsync(Repeats(3));
        for (var i = 0; i < 9; i += 3)
        {
            Assert.True(s.Rename(i + 1, "Results").Ok);
        }

        var template = s.ToTemplate("Lab run");
        var big = await SessionAsync(Repeats(20));
        var result = big.ApplyTemplate(template);
        Assert.True(result.Ok, result.Message);
        var map = result.PartMap!;
        Assert.Equal(60, map.Count);
        Assert.Equal(20, map.Count(k => k == 1));
        Assert.All(Enumerable.Range(0, 60).Where(i => map[i] == 1), i => Assert.Equal("Results", big.Containers[i].Name));
    }

    [Fact]
    public async Task PartMap_PartsLeftAsDetected_AreMinusOne()
    {
        var s = await SessionAsync(IntakeTemplateTests.Instrument(4));
        Assert.True(s.Rename(1, "Results").Ok);
        var template = s.ToTemplate("Short");

        // The same layout followed by a JSON part that the template does not describe.
        var other = await SessionAsync(IntakeTemplateTests.Instrument(6) + "\n{\n  \"a\": 1,\n  \"b\": [1, 2]\n}\n");
        var result = other.ApplyTemplate(template);
        Assert.True(result.Ok, result.Message);
        var map = result.PartMap!;
        var json = other.Containers.ToList().FindIndex(c => c.Format == "json");
        Assert.True(json >= 0);
        Assert.Equal(-1, map[json]);
        Assert.Equal(1, map[1]);
        Assert.Equal(result.PartsTotal - result.PartsApplied, map.Count(k => k < 0));
    }

    [Fact]
    public async Task PartMap_IsNullWhenTheTemplateDoesNotFit()
    {
        var s = await SessionAsync(IntakeTemplateTests.Instrument(4));
        var template = s.ToTemplate("Short");
        var other = await SessionAsync("{\n  \"a\": 1\n}\n\n<root><x>1</x></root>\n");
        var result = other.ApplyTemplate(template);
        Assert.False(result.Ok);
        Assert.Null(result.PartMap);
    }
}
