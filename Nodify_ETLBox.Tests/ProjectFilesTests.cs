using EtlDesigner.Engine.Project;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>Paths, safe save and the recent list, on real files in a temporary folder.</summary>
public sealed class ProjectFilesTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("etlproj-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string InFolder(params string[] parts) => Path.Combine([_folder, .. parts]);

    private string CreateFile(params string[] parts)
    {
        var path = InFolder(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private string[] FileNames() => Directory.GetFiles(_folder).Select(f => Path.GetFileName(f)).Order().ToArray();

    [Fact]
    public void Relative_IsMadeFromTheProjectFolder_WithForwardSlashes()
    {
        Assert.Equal("../data/in.csv", ProjectPaths.ToRelative(InFolder("data", "in.csv"), InFolder("proj", "a.etlproj")));
    }

    [Fact]
    public void Relative_IsNull_WithoutAProjectFile_OrOnAnotherDrive_OrForAnEmptyPath()
    {
        Assert.Null(ProjectPaths.ToRelative(@"C:\data\in.csv", null));
        Assert.Null(ProjectPaths.ToRelative(@"Z:\data\in.csv", @"C:\proj\a.etlproj"));
        Assert.Null(ProjectPaths.ToRelative("", InFolder("a.etlproj")));
    }

    [Fact]
    public void Resolve_UsesTheAbsolutePath_WhenItExists()
    {
        var absolute = CreateFile("data", "in.csv");
        CreateFile("proj", "in.csv");

        Assert.Equal(absolute, ProjectPaths.Resolve(absolute, "in.csv", InFolder("proj", "a.etlproj")));
    }

    [Fact]
    public void Resolve_UsesTheRelativePath_WhenTheAbsoluteOneIsGone()
    {
        var moved = CreateFile("proj", "data", "in.csv");

        Assert.Equal(moved, ProjectPaths.Resolve(InFolder("old", "in.csv"), "data/in.csv", InFolder("proj", "a.etlproj")));
    }

    [Fact]
    public void Resolve_KeepsTheAbsolutePath_WhenNeitherExists()
    {
        var gone = InFolder("old", "in.csv");

        Assert.Equal(gone, ProjectPaths.Resolve(gone, "data/in.csv", InFolder("proj", "a.etlproj")));
    }

    [Fact]
    public void Resolve_FindsATypedRelativePath_InTheProjectFolder()
    {
        var typed = CreateFile("proj", "in.csv");

        Assert.Equal(typed, ProjectPaths.Resolve("in.csv", null, InFolder("proj", "a.etlproj")));
    }

    [Fact]
    public void Save_NewFile_WritesUtf8WithoutBom_AndNoBackup_AndNoTempFile()
    {
        var path = InFolder("a.etlproj");

        ProjectFileStore.Save(path, "{ \"name\": \"µg\" }");

        Assert.Equal("{ \"name\": \"µg\" }", File.ReadAllText(path));
        Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]);
        Assert.Equal(["a.etlproj"], FileNames());
    }

    [Fact]
    public void Save_OverAFile_KeepsThePreviousVersionAsBak_OneBackupOnly()
    {
        var path = InFolder("a.etlproj");
        ProjectFileStore.Save(path, "one");
        ProjectFileStore.Save(path, "two");
        ProjectFileStore.Save(path, "three");

        Assert.Equal("three", File.ReadAllText(path));
        Assert.Equal("two", File.ReadAllText(path + ".bak"));
        Assert.Equal(["a.etlproj", "a.etlproj.bak"], FileNames());
    }

    internal sealed class FailingSteps(string failAt) : IProjectFileSteps
    {
        public void WriteAndFlush(string path, string text)
        {
            File.WriteAllText(path, text[..1]);
            if (failAt == nameof(WriteAndFlush)) throw new IOException("The disk is full.");
        }

        public void Replace(string newFile, string target, string backup)
        {
            if (failAt == nameof(Replace)) throw new IOException("The file is in use.");
        }

        public void Move(string newFile, string target) => throw new IOException("The file is in use.");
    }

    [Theory]
    [InlineData(nameof(IProjectFileSteps.WriteAndFlush), "The disk is full.")]
    [InlineData(nameof(IProjectFileSteps.Replace), "The file is in use.")]
    public void Save_WithAFailingStep_LeavesTheOldFileAndBak_AndGivesTheReason(string step, string reason)
    {
        var path = InFolder("a.etlproj");
        ProjectFileStore.Save(path, "one");
        ProjectFileStore.Save(path, "two");

        var ex = Assert.Throws<IOException>(() => ProjectFileStore.Save(path, "three", new FailingSteps(step)));

        Assert.Equal(reason, ex.Message);
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal("one", File.ReadAllText(path + ".bak"));
        Assert.Equal(["a.etlproj", "a.etlproj.bak"], FileNames());
    }

    [Fact]
    public void Read_OfAMissingFile_GivesAClearMessage()
    {
        var ex = Assert.Throws<ProjectLoadException>(() => ProjectFileStore.Read(InFolder("none.etlproj")));

        Assert.Contains("could not be read", ex.Message);
    }

    [Fact]
    public void Recent_AddsNewestFirst_MovesToTop_AndCapsAtFive()
    {
        var recent = new JsonRecentProjects(InFolder("sub", "recent.json"));

        foreach (var name in new[] { "a", "b", "c", "d", "e", "f" })
        {
            recent.Add(name);
        }

        Assert.Equal(["f", "e", "d", "c", "b"], recent.Load());

        recent.Add("C");

        Assert.Equal(["C", "f", "e", "d", "b"], recent.Load());
    }

    [Fact]
    public void Recent_Remove_TakesOutOneEntry()
    {
        var recent = new JsonRecentProjects(InFolder("recent.json"));
        recent.Add("a");
        recent.Add("b");

        recent.Remove("a");

        Assert.Equal(["b"], recent.Load());
    }

    [Fact]
    public void Recent_DamagedFile_IsAnEmptyList()
    {
        var file = CreateFile("recent.json");

        Assert.Empty(new JsonRecentProjects(file).Load());
    }
}
