using System.Text;
using EtlDesigner.Engine.Intake;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>Task G5c1 step 2a: template model, fingerprint, similarity and matcher.</summary>
public class IntakeTemplateTests(ITestOutputHelper output)
{
    /// <summary>
    /// An instrument-style file: a key-value header, a csv table (header plus <paramref name="rows"/>
    /// rows of <paramref name="columns"/> columns, values from <paramref name="seed"/>) and a text footer.
    /// </summary>
    internal static string Instrument(int rows, int columns = 4, int seed = 1)
    {
        var text = new StringBuilder();
        text.Append($"Instrument: Model {seed}\nOperator: Person {seed}\nDate: 2026-0{seed % 9 + 1}-1{seed % 9}\nMethod: Assay {seed * 7}\nSerial: SN{seed * 13}\n\n");
        text.Append(string.Join(",", Enumerable.Range(1, columns).Select(c => $"Col{c}"))).Append('\n');
        for (var r = 0; r < rows; r++)
        {
            text.Append(string.Join(",", Enumerable.Range(1, columns).Select(c => ((r + 1) * c * seed % 997 + 0.5).ToString(System.Globalization.CultureInfo.InvariantCulture)))).Append('\n');
        }

        text.Append("\nEnd of report. Signed by the operator.\nAll values were checked twice by the lab.\n");
        return text.ToString();
    }

    internal static async Task<IntakeFingerprint> FingerprintAsync(string text, string name = "memory.txt")
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var item = new IntakeItem(Guid.NewGuid(), "file", name, bytes.Length, DateTime.UtcNow, () => new MemoryStream(bytes, writable: false));
        var (_, scan) = await IntakeEditSessionTests.ScanAsync(item);
        return IntakeFingerprint.Of(item, scan.Intake!.Containers, scan.EncodingUsed ?? "utf-8");
    }

    private static PartSignature S(string format, int? columns = null) => format == "csv" ? new PartSignature(format, ",", columns, true) : new PartSignature(format);

    private static IntakeTemplate Template(string name, IntakeFingerprint fingerprint)
        => new(IntakeTemplate.CurrentFormatVersion, Guid.NewGuid(), name, DateTime.UtcNow, fingerprint, []);

    [Fact]
    public async Task SameStructure_OtherRowsAndValues_IsExact()
    {
        var a = await FingerprintAsync(Instrument(5, seed: 1));
        var b = await FingerprintAsync(Instrument(120, seed: 7));
        output.WriteLine(a.Text);
        Assert.Equal(a.Text, b.Text);
        Assert.Equal(1.0, IntakeFingerprint.Similarity(a, b));
        Assert.Equal(TemplateMatch.Exact, IntakeTemplateMatcher.Match(b, [Template("lab", a)])[0].Match);
    }

    [Fact]
    public async Task FileNameAndFolder_ChangeNothing()
    {
        var text = Instrument(10);
        Assert.Equal((await FingerprintAsync(text, @"C:\one\a.txt")).Text, (await FingerprintAsync(text, @"D:\other\renamed.csv")).Text);
    }

    [Fact]
    public async Task OtherStructure_IsNone()
    {
        var instrument = await FingerprintAsync(Instrument(10));
        var other = await FingerprintAsync("{\n  \"a\": 1,\n  \"b\": [1, 2]\n}\n\nJust a line of prose here.\n");
        var match = IntakeTemplateMatcher.Match(other, [Template("lab", instrument)]).Single();
        output.WriteLine($"{other.Text} vs {instrument.Text}: {match.Similarity}");
        Assert.Equal(TemplateMatch.None, match.Match);
        Assert.Equal(0.4, match.Similarity); // only the text part matches: 2 * 1 / (2 + 3)
    }

    /// <summary>Three entries, one table with a fifth column: (1 + 0.5 + 1) * 2 / 6 = 0.8333, so Close at the default 0.8.</summary>
    [Fact]
    public async Task OneExtraColumn_IsClose_At0Point8333()
    {
        var four = await FingerprintAsync(Instrument(10, columns: 4));
        var five = await FingerprintAsync(Instrument(10, columns: 5));
        output.WriteLine($"{four.Text} vs {five.Text}");
        Assert.Equal(3, four.Entries.Count);
        var match = IntakeTemplateMatcher.Match(five, [Template("lab", four)]).Single();
        Assert.Equal(0.8333, match.Similarity);
        Assert.Equal(TemplateMatch.Close, match.Match);
        Assert.Equal(TemplateMatch.None, IntakeTemplateMatcher.Match(five, [Template("lab", four)], new TemplateSettings { CloseThreshold = 0.9 }).Single().Match);
    }

    [Fact]
    public async Task ThreeAndThreeHundredRepeatedParts_MatchEachOther()
    {
        static string Tables(int n) => string.Concat(Enumerable.Range(0, n).Select(i => $"a,b,c\n{i},2,3\n{i + 1},3,4\n\n"));
        var three = await FingerprintAsync(Tables(3));
        var many = await FingerprintAsync(Tables(300));
        output.WriteLine(three.Text);
        Assert.Equal(three.Text, many.Text);
        Assert.True(three.Entries.Single().Repeats);
    }

    [Theory]
    [InlineData("text csv4 keyvalue", "text csv4 keyvalue", 1.0)]
    [InlineData("text csv4 keyvalue", "text csv4 keyvalue json", 0.8571)]
    [InlineData("text csv4 keyvalue", "text csv5 keyvalue", 0.8333)]
    [InlineData("text csv4", "json xml", 0.0)]
    [InlineData("csv4 csv4 csv4 text", "csv4 text", 0.875)]
    public void Similarity_DocumentedNumbers(string a, string b, double expected)
    {
        static IntakeFingerprint F(string text) => IntakeFingerprint.Of(text.Split(' ').Select(t => t.StartsWith("csv") ? S("csv", int.Parse(t[3..])) : S(t)).ToList());
        Assert.Equal(expected, IntakeFingerprint.Similarity(F(a), F(b)));
        Assert.Equal(expected, IntakeFingerprint.Similarity(F(b), F(a)));
    }

    [Fact]
    public void Fingerprint_CollapsesRepeatingGroups()
    {
        var unit = new[] { S("keyvalue"), S("csv", 3), S("text") };
        var twice = IntakeFingerprint.Of([.. unit, .. unit]);
        var many = IntakeFingerprint.Of(Enumerable.Repeat(unit, 50).SelectMany(u => u).ToList());
        Assert.Equal("(keyvalue csv[,|3|h] text)*", twice.Text);
        Assert.Equal(twice.Text, many.Text);
        Assert.Equal("keyvalue csv[,|3|h] text", IntakeFingerprint.Of(unit).Text);
    }

    [Fact]
    public void Matcher_ReturnsAtMostThree_BestFirst()
    {
        var scan = IntakeFingerprint.Of([S("text"), S("csv", 4)]);
        var templates = new[]
        {
            Template("a", IntakeFingerprint.Of([S("json")])),
            Template("b", IntakeFingerprint.Of([S("text"), S("csv", 4)])),
            Template("c", IntakeFingerprint.Of([S("text"), S("csv", 5)])),
            Template("d", IntakeFingerprint.Of([S("text")]))
        };
        var found = IntakeTemplateMatcher.Match(scan, templates);
        Assert.Equal(["b", "c", "d"], found.Select(c => c.Template.Name));
        Assert.Equal([TemplateMatch.Exact, TemplateMatch.None, TemplateMatch.None], found.Select(c => c.Match));
        Assert.Equal(0.75, found[1].Similarity);
    }

    [Theory]
    [InlineData("Results", null, "Results")]
    [InlineData("   Section 2: Calibration   ", null, "Section 2: Calibration")]
    [InlineData("", null, null)]
    [InlineData("12,13,14", null, null)]
    [InlineData("1.5,abc,2.5,3", "csv4", null)]
    [InlineData("Name,Value,Unit,Note", "csv4", "Name,Value,Unit,Note")]
    [InlineData("Total,1,2", "csv4", "Total,1,2")]
    public void Anchor_Rule(string line, string? previous, string? expected)
    {
        var before = previous is null ? null : new ContainerInfo(1, ContainerKind.Detected, "csv", "t", "", null, null, 1, 3, 3, 0, 10, ContainerStatus.Ok, "",
            ContainerConfidence.Sure, Delimiter: ",");
        Assert.Equal(expected, PartFacts.Anchor(line, before, previous is null ? null : 4, 80));
        Assert.Equal(80, PartFacts.Anchor(new string('x', 200), null, null, 80)!.Length);
    }

    [Fact]
    public void Json_RoundTrip_AndDamagedOrNewerFiles()
    {
        var template = Template("Lab A", IntakeFingerprint.Of([S("text"), S("csv", 4)])) with
        {
            Parts = [new TemplatePart("csv", "Results", ",", HeaderGuess.Yes, 1, Columns: 4, Anchor: "Results", From: [S("csv", 4)], StartsInside: true)]
        };
        var json = IntakeTemplateJson.Serialize(template);
        var back = IntakeTemplateJson.Deserialize(json, out var error);
        Assert.Null(error);
        Assert.Equal(json, IntakeTemplateJson.Serialize(back!));

        Assert.Null(IntakeTemplateJson.Deserialize("{ broken", out error));
        Assert.Equal("The saved layout could not be read.", error);
        Assert.Null(IntakeTemplateJson.Deserialize(json.Replace("\"name\": \"Lab A\"", "\"name\": \"\""), out error));
        Assert.Equal("The saved layout could not be read.", error);
        Assert.Null(IntakeTemplateJson.Deserialize(json.Replace("\"formatVersion\": 1", "\"formatVersion\": 2"), out error));
        Assert.Equal("The saved layout was made by a newer version of the tool.", error);
    }
}
