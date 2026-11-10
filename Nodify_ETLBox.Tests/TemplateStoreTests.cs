using EtlDesigner.Engine.Intake;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>Task G5c1 step 2b: the template store. Temp folders only.</summary>
public sealed class TemplateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EtlDesignerTests", "templates-" + Guid.NewGuid().ToString("N"));

    public TemplateStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Folder => Path.Combine(_root, "templates");

    private FileTemplateStore Store(TemplateSettings? settings = null) => new((settings ?? new TemplateSettings()) with { Folder = Folder }, Path.Combine(_root, "fallback"));

    internal static IntakeTemplate Template(string name)
        => new(IntakeTemplate.CurrentFormatVersion, Guid.NewGuid(), name, DateTime.UtcNow,
            IntakeFingerprint.Of([new PartSignature("keyvalue"), new PartSignature("csv", ",", 4, true)]),
            [new TemplatePart("keyvalue", "Header"), new TemplatePart("csv", "Results", ",", HeaderGuess.Yes, Columns: 4, Anchor: "Col1,Col2")]);

    /// <summary>A path where a folder cannot be made: a file has that name.</summary>
    private string Blocked(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "not a folder");
        return path;
    }

    [Fact]
    public void Save_List_Get_AndANewStoreReadsTheFiles()
    {
        var store = Store();
        var a = Template("Lab B");
        var b = Template("Lab A");
        Assert.True(store.Save(a).Ok);
        Assert.True(store.Save(b).Ok);
        Assert.Equal(["Lab A", "Lab B"], store.List().Select(t => t.Name));
        Assert.True(File.Exists(Path.Combine(Folder, a.Id + ".json")));

        var again = Store();
        Assert.Equal(["Lab A", "Lab B"], again.List().Select(t => t.Name));
        Assert.Equal(IntakeTemplateJson.Serialize(a), IntakeTemplateJson.Serialize(again.Get(a.Id)!));
        Assert.Empty(again.Skipped);
        Assert.Null(again.Note);
        Assert.NotEqual(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), Path.GetFullPath(again.Folder!).TrimEnd('\\'));
    }

    [Fact]
    public void Replace_Delete_Rename()
    {
        var store = Store();
        var a = Template("Lab A");
        Assert.True(store.Save(a).Ok);
        Assert.True(store.Save(a with { Notes = "second" }).Ok);
        Assert.Single(store.List());
        Assert.Equal("second", Store().Get(a.Id)!.Notes);

        Assert.True(store.Rename(a.Id, "  Lab C  ").Ok);
        Assert.Equal("Lab C", Store().Get(a.Id)!.Name);
        Assert.Equal("The name is empty; type at least one character.", store.Rename(a.Id, " ").Message);

        Assert.True(store.Delete(a.Id).Ok);
        Assert.Empty(store.List());
        Assert.Empty(Directory.GetFiles(Folder));
        Assert.Equal("This saved layout no longer exists.", store.Delete(a.Id).Message);
    }

    [Fact]
    public void DamagedAndFutureFiles_AreSkipped_TheOthersLoad()
    {
        var store = Store();
        var good = Template("Good");
        Assert.True(store.Save(good).Ok);
        File.WriteAllText(Path.Combine(Folder, "x.json"), "{ not json");
        File.WriteAllText(Path.Combine(Folder, "future.json"), IntakeTemplateJson.Serialize(Template("Later")).Replace("\"formatVersion\": 1", "\"formatVersion\": 9"));
        File.WriteAllText(Path.Combine(Folder, "big.json"), new string(' ', 3 * 1024));

        var again = Store(new TemplateSettings { MaxFileKb = 2 });
        Assert.Equal(["Good"], again.List().Select(t => t.Name));
        Assert.Equal(
            [
                "The saved layout 'big.json' is larger than 2 KB, so it was not read.",
                "The saved layout 'future.json' was made by a newer version of the tool.",
                "The saved layout 'x.json' could not be read."
            ],
            again.Skipped);
    }

    [Fact]
    public void TheTemplateAboveTheLimit_IsRefused()
    {
        var store = Store(new TemplateSettings { MaxTemplates = 500 });
        for (var i = 0; i < 500; i++)
        {
            Assert.True(store.Save(Template($"T{i}")).Ok);
        }

        var result = store.Save(Template("One too many"));
        Assert.False(result.Ok);
        Assert.Equal("There are already 500 saved layouts; delete one before saving another.", result.Message);
        Assert.Equal(500, Directory.GetFiles(Folder, "*.json").Length);
    }

    [Fact]
    public void AnUnwritableFolder_UsesTheFallback_WithANote_AndNothingWritableKeepsThemInMemory()
    {
        var store = new FileTemplateStore(new TemplateSettings { Folder = Blocked("blocked") }, Path.Combine(_root, "EtlDesigner", "templates"));
        Assert.True(store.Save(Template("Lab A")).Ok);
        Assert.Equal(Path.Combine(_root, "EtlDesigner", "templates"), store.Folder);
        Assert.Equal("Saved layouts are kept in EtlDesigner\\templates because the program folder is not writable.", store.Note);

        var memory = new FileTemplateStore(new TemplateSettings { Folder = Blocked("blocked2") }, Blocked("blocked3"));
        Assert.True(memory.Save(Template("Lab A")).Ok);
        Assert.Single(memory.List());
        Assert.Null(memory.Folder);
        Assert.Equal("Saved layouts cannot be written to disk, so they are kept only until the program closes.", memory.Note);
    }

    [Fact]
    public void EightThreads_SaveAndRead_WithoutLosingAnyTemplate()
    {
        var store = Store();
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            for (var k = 0; k < 10; k++)
            {
                var t = Template($"T{i}-{k}");
                Assert.True(store.Save(t).Ok);
                Assert.NotNull(store.Get(t.Id));
                _ = store.List();
            }
        });

        Assert.Equal(80, store.List().Count);
        Assert.Equal(80, Store().List().Count);
        Assert.Empty(Directory.GetFiles(Folder, "*.tmp"));
    }

    /// <summary>The replace fails because the target file is held open: the old file is unchanged and no temporary file is left.</summary>
    [Fact]
    public void AFailedReplace_LeavesTheOldFile_AndNoHalfFile()
    {
        var store = Store();
        var a = Template("Lab A");
        Assert.True(store.Save(a).Ok);
        var path = Path.Combine(Folder, a.Id + ".json");
        var before = File.ReadAllText(path);

        TemplateStoreResult result;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = store.Save(a with { Name = "Lab Z" });
        }

        Assert.False(result.Ok);
        Assert.Equal("The layout could not be saved; the file may be open in another program. Close it and try again.", result.Message);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal("Lab A", store.Get(a.Id)!.Name);
        Assert.Equal([path], Directory.GetFiles(Folder));
    }
}
