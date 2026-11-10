using EtlDesigner.Engine.Intake;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>Task G5c2 step 1a: column names take part in matching.</summary>
public sealed class IntakeTemplateNamesTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _folder = Directory.CreateTempSubdirectory("g5c2_names_").FullName;

    public IntakeTemplateNamesTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private FileTemplateStore Store() => new(new TemplateSettings { Folder = Path.Combine(_folder, "templates") }, Path.Combine(_folder, "fallback"));

    private static async Task<IntakeEditSession> FileSessionAsync(string path)
    {
        var (item, scan) = await IntakeEditSessionTests.ScanAsync(new FileIntakeFactory().CreateItem(path));
        return new IntakeEditSession(item, scan, null, Path.GetFileName(path));
    }

    private static IntakeTemplate Template(IntakeFingerprint print, params string[][] names)
        => new(1, Guid.NewGuid(), "t", DateTime.UtcNow, print, names.Select(n => new TemplatePart("csv", "Results", ",", HeaderGuess.Yes, ColumnNames: n)).ToList());

    private static readonly IntakeFingerprint OneTable = IntakeFingerprint.Of([new PartSignature("csv", ",", 10, true)]);

    private static string[] Names(string prefix) => Enumerable.Range(1, 10).Select(i => $"{prefix}{i}").ToArray();

    [Fact]
    public async Task R09AndR12_AreNoLongerExact()
    {
        var real = Path.Combine(IntakeTestData.Folder, "real");
        var r09 = await FileSessionAsync(Directory.GetFiles(real, "r09*").Single());
        var r12 = await FileSessionAsync(Directory.GetFiles(real, "r12*").Single());
        var store = Store();
        Assert.True(r12.SaveAsTemplate("r12", store).Result.Ok);
        var found = r09.SuggestTemplates(store).Single();
        _output.WriteLine($"r09 against r12: {found.Match} {found.Similarity} {found.Reason}");
        Assert.NotEqual(TemplateMatch.Exact, found.Match);
        Assert.Equal(1.0, IntakeFingerprint.Similarity(r09.AutomaticFingerprint(), r12.AutomaticFingerprint()));
    }

    [Fact]
    public async Task SameStructure_SameNames_StaysExact()
    {
        var path = Path.Combine(_folder, "a.txt");
        File.WriteAllText(path, IntakeTemplateTests.Instrument(5, seed: 1));
        var other = Path.Combine(_folder, "b.txt");
        File.WriteAllText(other, IntakeTemplateTests.Instrument(40, seed: 4));
        var store = Store();
        var saved = (await FileSessionAsync(path)).SaveAsTemplate("Lab", store);
        Assert.Equal(["Col1", "Col2", "Col3", "Col4"], store.Get(saved.TemplateId!.Value)!.Parts.Single(p => p.Format == "csv").ColumnNames);
        var found = (await FileSessionAsync(other)).SuggestTemplates(store).Single();
        Assert.Equal(TemplateMatch.Exact, found.Match);
        Assert.False(found.NamesDiffer);
        Assert.Null(found.Reason);
    }

    [Fact]
    public void SameStructure_TwoOfTenNamesEqual_IsClose_WithTheReason()
    {
        var file = Names("Col").Take(2).Concat(Names("Other").Skip(2)).ToArray();
        var found = IntakeTemplateMatcher.Match(OneTable, [Template(OneTable, Names("Col"))], null, [file]).Single();
        Assert.Equal(TemplateMatch.Close, found.Match);
        Assert.Equal(0.9, found.Similarity);
        Assert.True(found.NamesDiffer);
        Assert.Equal("The column names are different.", found.Reason);

        var half = Names("Col").Take(5).Concat(Names("Other").Skip(5)).ToArray();
        Assert.Equal(TemplateMatch.Exact, IntakeTemplateMatcher.Match(OneTable, [Template(OneTable, Names("Col"))], null, [half]).Single().Match);
        Assert.Equal(TemplateMatch.Exact, IntakeTemplateMatcher.Match(OneTable, [Template(OneTable, Names("COL"))], null, [Names(" col")]).Single().Match);
        Assert.Equal(TemplateMatch.Close, IntakeTemplateMatcher.Match(OneTable, [Template(OneTable, Names("Col"))], new TemplateSettings { ColumnNameMatchMin = 0.6 }, [half]).Single().Match);
    }

    /// <summary>A template saved before G5c2 has no column names; it matches as before.</summary>
    [Fact]
    public void ATemplateWithoutNames_MatchesAsBefore()
    {
        var json = IntakeTemplateJson.Serialize(Template(OneTable, [])) ;
        var old = IntakeTemplateJson.Deserialize(json.Replace("\"columnNames\": [],", "").Replace(",\n      \"columnNames\": []", ""), out var error)!;
        Assert.Null(error);
        var found = IntakeTemplateMatcher.Match(OneTable, [old], null, [Names("Other")]).Single();
        Assert.Equal(TemplateMatch.Exact, found.Match);
        Assert.False(found.NamesDiffer);
    }

    [Fact]
    public async Task AFileWithoutAHeader_GivesATemplateWithoutNames()
    {
        var path = Path.Combine(_folder, "noheader.txt");
        File.WriteAllText(path, string.Concat(Enumerable.Range(1, 8).Select(i => $"{i},{i * 2},{i * 3}\n")));
        var s = await FileSessionAsync(path);
        Assert.Equal("csv", s.Containers.Single().Format);
        Assert.NotEqual(HeaderGuess.Yes, s.Containers.Single().HeaderGuess);
        var store = Store();
        var id = s.SaveAsTemplate("No header", store).TemplateId!.Value;
        Assert.DoesNotContain("columnNames", File.ReadAllText(Path.Combine(_folder, "templates", id + ".json")));
    }

    [Fact]
    public async Task ColumnNames_NeverGoToTheLineage()
    {
        var log = new TextFileLineageLog(new LineageSettings { Folder = Path.Combine(_folder, "lineage") });
        var path = Path.Combine(_folder, "a.txt");
        File.WriteAllText(path, IntakeTemplateTests.Instrument(5).Replace("Col", "Zeta"));
        var (item, scan) = await IntakeEditSessionTests.ScanAsync(new FileIntakeFactory().CreateItem(path));
        var s = new IntakeEditSession(item, scan, log, "n");
        var store = Store();
        var id = s.SaveAsTemplate("Lab", store).TemplateId!.Value;
        s.SuggestTemplates(store);
        Assert.True(s.ApplyTemplate(store.Get(id)!).Ok);
        Assert.DoesNotContain("Zeta", File.ReadAllText(log.FilePath!));
    }
}
