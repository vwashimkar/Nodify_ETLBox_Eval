using System.Security.AccessControl;
using System.Security.Principal;
using EtlDesigner.Engine.Generation;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>The Generate DLL output folder check, on real temp folders.</summary>
public sealed class OutputFolderCheckTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("taskP1_folder_").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ExistingFolder_IsFine_AndTheCheckLeavesNoFileBehind()
    {
        Assert.Null(OutputFolderCheck.Check(_root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void FolderThatDoesNotExistYet_IsFine_AndIsNotCreatedByTheCheck()
    {
        var folder = Path.Combine(_root, "CsvPipeline");

        Assert.Null(OutputFolderCheck.Check(folder));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void SeveralMissingLevels_UseTheNearestExistingParent()
    {
        Assert.Null(OutputFolderCheck.Check(Path.Combine(_root, "a", "b", "CsvPipeline")));
    }

    [Fact]
    public void FolderUnderDocuments_DoesNotGiveTheOldError()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"taskP1_{Guid.NewGuid():N}", "CsvPipeline");

        Assert.Null(OutputFolderCheck.Check(folder));
    }

    [Theory]
    [InlineData("Instrument.csproj")]
    [InlineData("Instrument.sln")]
    public void MissingFolderInsideAProject_IsRefused(string projectFile)
    {
        File.WriteAllText(Path.Combine(_root, projectFile), string.Empty);

        Assert.Equal(OutputFolderCheck.ProjectFolderMessage, OutputFolderCheck.Check(Path.Combine(_root, "bin", "CsvPipeline")));
        Assert.Equal(OutputFolderCheck.ProjectFolderMessage, OutputFolderCheck.Check(_root));
    }

    [Fact]
    public void ParentThatDoesNotExistAtAll_HasItsOwnMessage()
    {
        var drive = Enumerable.Range('D', 23).Select(c => $"{(char)c}:\\").Last(d => !Directory.Exists(d));

        Assert.Equal(OutputFolderCheck.NoExistingParentMessage, OutputFolderCheck.Check(Path.Combine(drive, "a", "CsvPipeline")));
    }

    [Theory]
    [InlineData("bad|name")]
    [InlineData("bad<name")]
    [InlineData("bad?name")]
    [InlineData("bad*name")]
    [InlineData("bad\"name")]
    [InlineData("bad\0name")]
    public void InvalidCharacters_HaveTheirOwnMessage(string name)
    {
        Assert.Equal(OutputFolderCheck.InvalidPathMessage, OutputFolderCheck.Check(_root + Path.DirectorySeparatorChar + name));
    }

    [Fact]
    public void ReadOnlyParent_HasItsOwnMessage()
    {
        var parent = new DirectoryInfo(_root);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, AccessControlType.Deny);
        var acl = parent.GetAccessControl();
        acl.AddAccessRule(rule);
        parent.SetAccessControl(acl);

        try
        {
            Assert.Equal(OutputFolderCheck.NotWritableMessage, OutputFolderCheck.Check(Path.Combine(_root, "CsvPipeline")));
        }
        finally
        {
            acl.RemoveAccessRule(rule);
            parent.SetAccessControl(acl);
        }
    }

    [Fact]
    public void AFileInThePath_HasItsOwnMessage()
    {
        var file = Path.Combine(_root, "data.txt");
        File.WriteAllText(file, "x");

        Assert.Equal(OutputFolderCheck.NotAFolderMessage, OutputFolderCheck.Check(file));
        Assert.Equal(OutputFolderCheck.NotAFolderMessage, OutputFolderCheck.Check(Path.Combine(file, "CsvPipeline")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyFolder_AsksForOne(string folder)
    {
        Assert.Equal(OutputFolderCheck.EmptyMessage, OutputFolderCheck.Check(folder));
    }

    [Fact]
    public void IsInsideProject_WalksUpFromTheNearestExistingParent()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "project", "sub")).Parent!.FullName;
        File.WriteAllText(Path.Combine(project, "a.csproj"), string.Empty);

        Assert.True(OutputFolderCheck.IsInsideProject(Path.Combine(project, "sub", "new", "deeper")));
        Assert.True(MainViewModel.IsInsideProjectFolder(Path.Combine(project, "sub")));
        Assert.False(OutputFolderCheck.IsInsideProject(Path.Combine(_root, "other", "new")));
    }

    [Fact]
    public void Dialog_AcceptsAMissingFolder_AndTheGeneratorCreatesIt()
    {
        var folder = Path.Combine(_root, "new", "CsvPipeline");
        var definition = PipelineDefinitionTests.C01Definition();
        string[] columns = ["sample_id", "component", "value_a", "value_b", "value_c"];
        var vm = new GenerateDllViewModel(definition, columns, outputFolder: folder);

        Assert.Null(vm.OutputFolderError);
        Assert.True(vm.CanGenerate);

        var result = CsvPipelineDllGenerator.Generate(new CsvPipelineDllRequest(definition, columns, folder, "CsvPipeline", "Generated"));

        Assert.True(result.Success, string.Join(" ", result.Messages));
        Assert.True(File.Exists(Path.Combine(folder, "CsvPipeline.dll")));
    }
}
