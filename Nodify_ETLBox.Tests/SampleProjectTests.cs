using System.Text.Json.Nodes;
using System.Windows;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.Engine.Project;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>The checked-in sample project, opened and run from end to end.</summary>
[Collection(WorkingDirectoryCollection.Name)]
public sealed class SampleProjectTests : IDisposable
{
    private const string RegenerateVariable = "ETLPROJ_WRITE_SAMPLE";

    private readonly string _folder = Directory.CreateTempSubdirectory("etlproj-").FullName;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private static string SamplePath => Path.Combine(AppContext.BaseDirectory, "SampleData", "Projects", "c01_pipeline.etlproj");

    private static string SourceTreeSample(string repositoryRoot)
        => Path.Combine(repositoryRoot, "Nodify_ETLBox", "SampleData", "Projects", "c01_pipeline.etlproj");

    /// <summary>The sample as the serializer writes it, built on the canvas as a user does.</summary>
    private static string BuildSample(string csvPath, string projectFile)
    {
        var editor = new EditorViewModel();
        editor.AddNode(NodeType.CsvFile, new Point(40, 80));
        editor.AddNode(NodeType.ParseValue, new Point(320, 80));
        editor.AddNode(NodeType.DotNetObject, new Point(600, 80));
        var (csv, parse, output) = (editor.Nodes[0], editor.Nodes[1], editor.Nodes[2]);

        csv.Settings.Single(s => s.Key == "FilePath").Value = csvPath;
        parse.ParseValueSettings = new ParseValueSettings
        {
            SourceColumn = "sample",
            DelimiterLevels = [new DelimiterLevel(":", SplitMode.Once), new DelimiterLevel("|", SplitMode.All)],
            Slots = ParseValueSettings.SlotsFromGroups(Enumerable.Range(1, 18).Select(i => $"r{i:00}").ToList(), 3),
            CarryColumns = CarryColumns.None
        };
        output.Settings.Single(s => s.Key == "OutputType").Value = ObjectKinds.All[0].Kind.ToString();

        editor.StartConnection(csv.Outputs[0]);
        editor.CompleteConnection(parse.Inputs[0]);
        editor.StartConnection(parse.Outputs[0]);
        editor.CompleteConnection(output.Inputs[0]);

        return ProjectSerializer.Serialize(editor.ToProject("c01_pipeline", projectFile, new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary>
    /// Writes the sample into the source tree when ETLPROJ_WRITE_SAMPLE=1. The node ids are new
    /// each time, so it stays an explicit step. Otherwise it checks the file against the helper.
    /// </summary>
    [Fact]
    public void SampleFile_IsWhatTheSerializerWrites()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        if (Environment.GetEnvironmentVariable(RegenerateVariable) == "1")
        {
            var target = SourceTreeSample(root);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, BuildSample(Path.Combine(root, "Nodify_ETLBox", "SampleData", "TestData", "composite", "c01_basic.csv"), target));
            return;
        }

        var sample = ProjectSerializer.Deserialize(File.ReadAllText(SamplePath));
        var expected = ProjectSerializer.Deserialize(BuildSample(PipelineDefinitionTests.CompositePath("c01_basic.csv"), SamplePath));

        Assert.Equal(expected.Nodes.Select(n => (n.Type, n.Name, n.X, n.Y)), sample.Nodes.Select(n => (n.Type, n.Name, n.X, n.Y)));
        Assert.Equal(expected.Connections.Select(c => (c.FromConnector, c.ToConnector)), sample.Connections.Select(c => (c.FromConnector, c.ToConnector)));
        Assert.Equal("../TestData/composite/c01_basic.csv", ProjectNodeSettings.ReadRelativePath(sample.Nodes[0].Settings!.Value));
        Assert.Equal(expected.Nodes[1].Settings!.Value.GetRawText(), sample.Nodes[1].Settings!.Value.GetRawText());
        Assert.Equal(expected.Nodes[2].Settings!.Value.GetRawText(), sample.Nodes[2].Settings!.Value.GetRawText());
    }

    private static async Task OpenRunAndCompare(string projectFile, string expectedCsvPath)
    {
        var dialogs = new ProjectEditorTests.FakeDialogs();
        var main = new MainViewModel(dialogs, new ProjectEditorTests.MemoryRecent());

        await main.OpenProjectAsync(projectFile);
        await TestTimeout.Run(() => main.ProjectRefresh);

        Assert.Empty(dialogs.Errors);
        Assert.Equal(3, main.Editor.Nodes.Count);
        Assert.All(main.Editor.Nodes, n => Assert.Equal("Configured", n.ConfigurationStatus));
        Assert.False(main.Editor.IsDirty);

        Assert.True(main.TryBuildExecuteDefinition(out var definition, out var reason), reason);
        Assert.Equal(expectedCsvPath, definition!.CsvFile.Path);

        var result = await TestTimeout.Run(() => PipelineRunner.RunAsync(definition, null, null, CancellationToken.None));

        Assert.Null(result.Error);
        Assert.Equal(4, result.Counts.Read);
        Assert.Equal(24, result.Counts.Out);
        var expected = await CompositeManifestTests.ReadCsvAsync("expected/C01_output.csv", ",", true, 0);
        Assert.Equal(expected.Columns, result.Columns);
        Assert.Equal(expected.Rows.Count, result.Rows.Count);
        for (var i = 0; i < expected.Rows.Count; i++)
        {
            Assert.Equal(expected.Rows[i], result.Columns.Select(c => result.Rows[i][c]));
        }
    }

    [Fact]
    public async Task SampleProject_Opens_AndGivesTheC01Output()
    {
        var sample = ProjectSerializer.Deserialize(File.ReadAllText(SamplePath));
        var (settings, _, _) = ProjectNodeSettings.ReadCsvFile(sample.Nodes[0].Settings!.Value);

        // The absolute path in the file, else the copy next to the tests through the relative path.
        var expected = File.Exists(settings.Path) ? settings.Path : PipelineDefinitionTests.CompositePath("c01_basic.csv");

        await OpenRunAndCompare(SamplePath, expected);
    }

    [Fact]
    public async Task SampleProject_FromAnotherWorkingDirectory_FindsTheFileThroughTheRelativePath()
    {
        var projects = Directory.CreateDirectory(Path.Combine(_folder, "lab", "Projects")).FullName;
        var composite = Directory.CreateDirectory(Path.Combine(_folder, "lab", "TestData", "composite")).FullName;
        var workingDirectory = Directory.CreateDirectory(Path.Combine(_folder, "elsewhere")).FullName;
        var csvPath = Path.Combine(composite, "c01_basic.csv");
        File.Copy(PipelineDefinitionTests.CompositePath("c01_basic.csv"), csvPath);

        // A copy whose absolute path is gone, as after moving the folder to another computer.
        var json = JsonNode.Parse(File.ReadAllText(SamplePath))!;
        json["nodes"]![0]!["settings"]!["path"] = Path.Combine(_folder, "gone", "c01_basic.csv");
        var projectFile = Path.Combine(projects, "c01_pipeline.etlproj");
        File.WriteAllText(projectFile, json.ToJsonString());

        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(workingDirectory);
        try
        {
            await OpenRunAndCompare(projectFile, csvPath);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }
}

/// <summary>A test here changes the process's working directory, so it must not run beside the others.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkingDirectoryCollection
{
    public const string Name = "working directory";
}
