using System.Text.Json.Nodes;
using System.Windows;
using EtlDesigner.Engine.Intake;
using EtlDesigner.Engine.Project;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>The File intake node (G4b step 1): the node, its project-file settings, Run and Generate DLL.</summary>
public sealed class FileIntakeNodeTests : IDisposable
{
    private static readonly DateTime Saved = new(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _folder = Directory.CreateTempSubdirectory("intake-node-").FullName;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private static NodeViewModel AddIntake(EditorViewModel editor)
    {
        editor.AddNode(NodeType.FileIntake, new Point(10, 20));
        return editor.Nodes.Last();
    }

    private static string Text(EditorViewModel editor, string? path = null)
        => ProjectSerializer.Serialize(editor.ToProject("p", path, Saved));

    private static EditorViewModel Reopen(string text, string? path = null)
    {
        var editor = new EditorViewModel();
        editor.FromProject(ProjectSerializer.Deserialize(text), path);
        return editor;
    }

    private string CreateFile(string relative)
    {
        var path = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "a,b\n1,2\n");
        return path;
    }

    [Fact]
    public void NewNode_HasTheDefaults_OnePartsOutput_AndSaysNoFileChosen()
    {
        var node = AddIntake(new EditorViewModel());

        Assert.Equal("File intake", node.Title);
        Assert.Equal("Reads a file and finds its parts", node.Subtitle);
        Assert.Empty(node.Inputs);
        // G6c: one output, "Parts", for CSV part nodes.
        var output = Assert.Single(node.Outputs);
        Assert.Equal(("Parts", PortKind.Parts), (output.Name, output.Kind));
        Assert.Equal(NodeViewModel.NoFileChosenText, node.CanvasNote);
        Assert.Null(node.CanvasNoteToolTip);
        Assert.Equal(FileIntakeNodeSettings.Default, node.GetFileIntakeSettings());
        Assert.Equal(500, node.GetFileIntakeSettings().MaxFileSizeMb);
        Assert.NotNull(node.DoubleClickCommand);
    }

    [Fact]
    public void CanvasNote_ShowsTheFileName_AndTheSummary_WhichASettingChangeClears()
    {
        var node = AddIntake(new EditorViewModel());
        var path = CreateFile("sub/data.txt");
        node.SetFileIntakeSettings(node.GetFileIntakeSettings() with { Path = path });
        node.IntakeSummary = "27 containers, 1 warning";

        Assert.Equal("data.txt" + Environment.NewLine + "27 containers, 1 warning", node.CanvasNote);
        Assert.Equal(path, node.CanvasNoteToolTip);

        node.Settings.Single(s => s.Key == "Mode").Value = FileIntakeNodeSettings.MarkersMode;
        Assert.Null(node.IntakeSummary);
        Assert.Equal("data.txt", node.CanvasNote);
    }

    [Fact]
    public void SaveAndOpen_KeepsEverySetting_WithARelativePath()
    {
        var editor = new EditorViewModel();
        var node = AddIntake(editor);
        var file = CreateFile(Path.Combine("data", "i01.txt"));
        var settings = new FileIntakeNodeSettings { Path = file, Mode = "markers", Encoding = "windows-1252", MaxFileSizeMb = 42 };
        node.SetFileIntakeSettings(settings);
        node.IntakeSummary = "not saved";
        var projectPath = Path.Combine(_folder, "p.etlproj");

        var text = Text(editor, projectPath);
        var saved = JsonNode.Parse(text)!["nodes"]![0]!;
        Assert.Equal(ProjectNodeTypes.FileIntake, (string?)saved["type"]);
        Assert.Equal("data/i01.txt", (string?)saved["settings"]!["relativePath"]);
        Assert.Equal(42, (int?)saved["settings"]!["maxFileSizeMb"]);
        Assert.DoesNotContain("not saved", text);
        Assert.Equal(1, (int?)JsonNode.Parse(text)!["formatVersion"]);

        var loaded = Reopen(text, projectPath);
        var back = Assert.Single(loaded.Nodes);
        Assert.Equal(NodeType.FileIntake, back.NodeType);
        Assert.Equal(node.Id, back.Id);
        Assert.Equal(settings, back.GetFileIntakeSettings());
        Assert.Null(back.IntakeSummary);
        Assert.False(loaded.IsDirty);
        Assert.Equal(text, Text(loaded, projectPath));
    }

    [Fact]
    public void AMovedProjectFolder_StillFindsTheFile()
    {
        var editor = new EditorViewModel();
        var node = AddIntake(editor);
        node.SetFileIntakeSettings(new FileIntakeNodeSettings { Path = CreateFile(Path.Combine("lab", "data", "x.txt")) });
        var text = Text(editor, Path.Combine(_folder, "lab", "p.etlproj"));

        Directory.Move(Path.Combine(_folder, "lab"), Path.Combine(_folder, "moved"));
        var loaded = Reopen(text, Path.Combine(_folder, "moved", "p.etlproj"));

        Assert.Equal(Path.Combine(_folder, "moved", "data", "x.txt"), loaded.Nodes[0].GetFileIntakeSettings().Path);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"path\":\"\"}")]
    public void AMissingPath_OpensWithNoFileChosen_AndNoException(string settings)
    {
        var text = $$$"""{"formatVersion":1,"projectName":"p","nodes":[{"id":"{{{Guid.NewGuid()}}}","type":"FileIntake","name":"File intake","x":0,"y":0,"settings":{{{settings}}}}],"connections":[]}""";

        var loaded = Reopen(text);

        var node = Assert.Single(loaded.Nodes);
        Assert.Equal(NodeType.FileIntake, node.NodeType);
        Assert.Equal(NodeViewModel.NoFileChosenText, node.CanvasNote);
        Assert.Equal(["No issues found."], loaded.Issues);
    }

    [Fact]
    public void AFileThatIsGone_ShowsItsName_AndKeepsTheSavedRelativePath()
    {
        var text = $$$"""{"formatVersion":1,"projectName":"p","nodes":[{"id":"{{{Guid.NewGuid()}}}","type":"FileIntake","name":"File intake","x":0,"y":0,"settings":{"path":"C:\\gone\\lost.txt","relativePath":"gone/lost.txt","mode":"autodetect","encoding":"auto","maxFileSizeMb":500}}],"connections":[]}""";
        var projectPath = Path.Combine(_folder, "p.etlproj");

        var loaded = Reopen(text, projectPath);

        Assert.Equal("lost.txt", loaded.Nodes[0].CanvasNote);
        Assert.Equal("gone/lost.txt", (string?)JsonNode.Parse(Text(loaded, projectPath))!["nodes"]![0]!["settings"]!["relativePath"]);
    }

    [Fact]
    public void DirtyTracking_ASettingChangeIsDirty_AScanSummaryIsNot()
    {
        var editor = new EditorViewModel();
        AddIntake(editor);
        var loaded = Reopen(Text(editor));
        var node = loaded.Nodes[0];

        node.IntakeSummary = "3 containers, 0 errors, 0 warnings";
        Assert.False(loaded.IsDirty);

        node.Settings.Single(s => s.Key == "Encoding").Value = "utf-8";
        Assert.True(loaded.IsDirty);

        loaded.MarkClean();
        node.SetFileIntakeSettings(node.GetFileIntakeSettings());
        Assert.False(loaded.IsDirty);
    }

    [Fact]
    public void AnUnknownSettingsField_IsWrittenBack()
    {
        var text = $$$"""{"formatVersion":1,"projectName":"p","nodes":[{"id":"{{{Guid.NewGuid()}}}","type":"FileIntake","name":"Mine","x":0,"y":0,"settings":{"path":"","mode":"markers","encoding":"utf-8","maxFileSizeMb":10,"futureLayout":{"a":1} }}],"connections":[]}""";

        var loaded = Reopen(text);
        loaded.Nodes[0].Settings.Single(s => s.Key == "Mode").Value = "autodetect";
        var settings = JsonNode.Parse(Text(loaded))!["nodes"]![0]!["settings"]!;

        Assert.Equal("Mine", loaded.Nodes[0].Title);
        Assert.Equal("autodetect", (string?)settings["mode"]);
        Assert.Equal(1, (int?)settings["futureLayout"]!["a"]);
    }

    [Theory]
    [InlineData("\"mode\":\"regex\"")]
    [InlineData("\"encoding\":\"utf-32\"")]
    [InlineData("\"maxFileSizeMb\":0")]
    [InlineData("\"maxFileSizeMb\":\"big\"")]
    public void AValueThisVersionDoesNotKnow_KeepsTheNodeAsUnknown_AndWritesItBackExactly(string field)
    {
        var text = $$$"""{"formatVersion":1,"projectName":"p","nodes":[{"id":"{{{Guid.NewGuid()}}}","type":"FileIntake","name":"File intake","x":5,"y":6,"settings":{"path":"",{{{field}}}}}],"connections":[]}""";

        var loaded = Reopen(text);

        Assert.Equal(NodeType.Unknown, loaded.Nodes[0].NodeType);
        Assert.Contains(loaded.Issues, i => i.Contains("settings could not be read", StringComparison.Ordinal));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text)!["nodes"]![0], JsonNode.Parse(Text(loaded))!["nodes"]![0]));
    }

    [Fact]
    public void AProjectFromBeforeThisTask_OpensAndSavesIdentically()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var sample = Path.Combine(root, "Nodify_ETLBox", "SampleData", "Projects", "c01_pipeline.etlproj");
        var text = File.ReadAllText(sample);
        var file = ProjectSerializer.Deserialize(text);

        var loaded = Reopen(text, sample);
        var again = ProjectSerializer.Serialize(loaded.ToProject(file.ProjectName, sample, file.SavedUtc));

        Assert.Equal(text.ReplaceLineEndings("\n").TrimEnd(), again.ReplaceLineEndings("\n").TrimEnd());
    }

    [Fact]
    public void Run_WithOnlyAFileIntakeNode_AddsTheLineOnce()
    {
        var main = new MainViewModel(new ProjectEditorTests.FakeDialogs(), new ProjectEditorTests.MemoryRecent());
        AddIntake(main.Editor);

        main.RunCommand.Execute(null);
        main.RunCommand.Execute(null);

        Assert.Single(main.Editor.Issues, MainViewModel.FileIntakeNotRunMessage);
    }

    [Fact]
    public void Run_WithNewStyleNodes_KeepsItsLine_AndAddsTheIntakeLine()
    {
        var main = new MainViewModel(new ProjectEditorTests.FakeDialogs(), new ProjectEditorTests.MemoryRecent());
        main.Editor.AddNode(NodeType.CsvFile, new Point(0, 0));
        AddIntake(main.Editor);

        main.RunCommand.Execute(null);

        Assert.Equal([MainViewModel.RunIsForLegacyChainMessage, MainViewModel.FileIntakeNotRunMessage], main.Editor.Issues);
    }

    [Fact]
    public void TheLegacyChainCheck_DoesNotCountAFileIntakeNode()
    {
        var editor = new EditorViewModel();
        foreach (var type in new[] { NodeType.CsvSource, NodeType.FilterByCountry, NodeType.ToJson, NodeType.ParseJson, NodeType.ResultJson })
        {
            editor.AddNode(type, new Point(0, 0));
        }

        AddIntake(editor);
        editor.Validate();

        Assert.DoesNotContain(editor.Issues, i => i.Contains("Found 6 nodes", StringComparison.Ordinal));
        Assert.DoesNotContain(editor.Issues, i => i.Contains("Found 5 nodes", StringComparison.Ordinal));
    }

    private static (MainViewModel Main, EtlDesigner.Engine.Pipeline.PipelineDefinition Definition) C01Graph()
    {
        var main = new MainViewModel(new ProjectEditorTests.FakeDialogs(), new ProjectEditorTests.MemoryRecent());
        var editor = main.Editor;
        editor.AddNode(NodeType.CsvFile, new Point(0, 0));
        editor.AddNode(NodeType.ParseValue, new Point(200, 0));
        editor.AddNode(NodeType.DotNetObject, new Point(400, 0));
        var (csv, parse, output) = (editor.Nodes[0], editor.Nodes[1], editor.Nodes[2]);
        csv.Settings.First(s => s.Key == "FilePath").Value = PipelineDefinitionTests.CompositePath("c01_basic.csv");
        csv.KnownColumns = ["id", "batch", "name", "sample", .. Enumerable.Range(1, 18).Select(i => $"r{i:00}")];
        parse.ParseValueSettings = PipelineDefinitionTests.C01Definition().ParseValue;
        editor.StartConnection(csv.Outputs[0]);
        editor.CompleteConnection(parse.Inputs[0]);
        editor.StartConnection(parse.Outputs[0]);
        editor.CompleteConnection(output.Inputs[0]);
        editor.Validate();
        Assert.True(main.TryBuildExecuteDefinition(out var definition, out var reason), reason);
        return (main, definition!);
    }

    [Fact]
    public void ExecuteAndTheGenerateDllGate_IgnoreAFileIntakeNode()
    {
        var (main, before) = C01Graph();
        var gateBefore = main.GenerateDllBlockedReason;

        AddIntake(main.Editor);
        main.Editor.Validate();

        Assert.True(main.TryBuildExecuteDefinition(out var after, out var reason), reason);
        Assert.Equal(MainViewModel.PipelineHash(before), MainViewModel.PipelineHash(after!));
        Assert.Equal(gateBefore, main.GenerateDllBlockedReason);
    }

    [Fact]
    public void GenerateDll_AfterASuccessfulExecute_WithAFileIntakeNode_SaysTheLine()
    {
        var (main, definition) = C01Graph();
        AddIntake(main.Editor);
        main.Editor.Validate();
        main.OnExecuteRunFinished(definition, new EtlDesigner.Engine.Pipeline.PipelineResult(["sample_id"], new(), [], [],
            new EtlDesigner.Engine.Pipeline.PipelineCounts(4, 4, 0, 0, 24), false, 5, null, false));
        Assert.Null(main.GenerateDllBlockedReason);

        main.GenerateDllCommand.Execute(null);

        Assert.Contains("Generate DLL: " + MainViewModel.FileIntakeNotRunMessage, main.Editor.Issues);
    }

    [Fact]
    public void GenerateDll_IsStillBlocked_ByTheLegacyRule_WhenOnlyAFileIntakeNodeIsThere()
    {
        var main = new MainViewModel(new ProjectEditorTests.FakeDialogs(), new ProjectEditorTests.MemoryRecent());
        AddIntake(main.Editor);

        Assert.Equal(MainViewModel.GenerateDllLegacyHint, main.GenerateDllBlockedReason);
        Assert.False(main.GenerateDllCommand.CanExecute(null));
    }

    [Fact]
    public void IntakeStudio_SaveWritesTheSettings_AndMarksTheProjectDirty_OnlyWhenSomethingChanged()
    {
        var dialogs = new ProjectEditorTests.FakeDialogs();
        var main = new MainViewModel(dialogs, new ProjectEditorTests.MemoryRecent(), new IntakeStudioOpenTests.MemoryLineage());
        var node = AddIntake(main.Editor);
        main.Editor.MarkClean();

        dialogs.OnIntakeStudio = vm => { vm.Save(); vm.CloseCommand.Execute(null); };
        Assert.True(main.ShowIntakeStudio(node));
        Assert.False(main.Editor.IsDirty);
        Assert.Empty(dialogs.Asked);

        dialogs.OnIntakeStudio = vm => { vm.Mode = FileIntakeNodeSettings.MarkersMode; vm.Encoding = "utf-16le"; vm.SaveCommand.Execute(null); };
        main.ShowIntakeStudio(node);
        Assert.True(main.Editor.IsDirty);
        Assert.Equal(FileIntakeNodeSettings.MarkersMode, node.GetFileIntakeSettings().Mode);
        Assert.Equal("utf-16le", node.GetFileIntakeSettings().Encoding);
    }

    [Fact]
    public void IntakeStudio_UnsavedChanges_AskOnce_AndDontSaveKeepsTheNode()
    {
        var dialogs = new ProjectEditorTests.FakeDialogs { Answer = EtlDesigner.UI.Services.UnsavedChangesChoice.DontSave };
        var main = new MainViewModel(dialogs, new ProjectEditorTests.MemoryRecent(), new IntakeStudioOpenTests.MemoryLineage());
        var node = AddIntake(main.Editor);
        var closed = 0;

        dialogs.OnIntakeStudio = vm =>
        {
            vm.CloseRequested += (_, _) => closed++;
            vm.Mode = FileIntakeNodeSettings.MarkersMode;
            vm.EscapeCommand.Execute(null);
            Assert.True(vm.ConfirmClose());
        };
        main.ShowIntakeStudio(node);

        Assert.Equal(1, closed);
        Assert.Single(dialogs.Asked);
        Assert.Equal(FileIntakeNodeSettings.AutoDetect, node.GetFileIntakeSettings().Mode);
    }

    [Fact]
    public void IntakeStudio_AScanWithTheNodesSettings_PutsTheTotalsOnTheNode_AScanWithUnsavedSettingsDoesNot()
    {
        var dialogs = new ProjectEditorTests.FakeDialogs();
        var main = new MainViewModel(dialogs, new ProjectEditorTests.MemoryRecent(), new IntakeStudioOpenTests.MemoryLineage());
        var node = AddIntake(main.Editor);
        node.SetFileIntakeSettings(new FileIntakeNodeSettings { Path = IntakeTestData.PathOf("i01_sample") });
        main.Editor.MarkClean();

        dialogs.OnIntakeStudio = vm => Task.Run(vm.ScanAsync).Wait(TimeSpan.FromSeconds(30));
        main.ShowIntakeStudio(node);
        Assert.Equal("27 containers, 3 warnings", node.IntakeSummary);
        Assert.Equal("i01_sample.txt" + Environment.NewLine + "27 containers, 3 warnings", node.CanvasNote);
        Assert.False(main.Editor.IsDirty);

        dialogs.OnIntakeStudio = vm => { vm.Mode = FileIntakeNodeSettings.MarkersMode; Task.Run(vm.ScanAsync).Wait(TimeSpan.FromSeconds(30)); };
        main.ShowIntakeStudio(node);
        Assert.Equal("27 containers, 3 warnings", node.IntakeSummary);
    }
}

/// <summary>The File intake handler and labels are static, so these tests do not run beside the classes that create a MainViewModel.</summary>
[Collection(NodeFactoryHandlerCollection.Name)]
public class FileIntakeStaticHandlerTests
{
    [Fact]
    public void ConfigureCommand_AndDoubleClick_CallTheHandlerWithTheNode()
    {
        var node = NodeFactory.CreateFileIntake(new Point(0, 0));
        var opened = new List<NodeViewModel>();
        var previous = NodeFactory.FileIntakeConfigureHandler;
        NodeFactory.FileIntakeConfigureHandler = n => { opened.Add(n); return true; };
        try
        {
            node.ConfigureCommand!.Execute(null);
            node.DoubleClickCommand!.Execute(null);
        }
        finally
        {
            NodeFactory.FileIntakeConfigureHandler = previous;
        }

        Assert.Equal([node, node], opened);
        Assert.Null(NodeFactory.CreateParseValue(new Point(0, 0)).DoubleClickCommand);
    }

    [Fact]
    public void TheTitleAndSubtitle_ComeFromTheLabels_AndCreatingANodeCallsTheFirstUseHook()
    {
        var (labels, hook) = (NodeFactory.Labels, NodeFactory.FileIntakeCreated);
        var created = 0;
        NodeFactory.Labels = Labels.Parse("node.fileintake.title = Instrument file\nnode.fileintake.subtitle = Splits exports");
        NodeFactory.FileIntakeCreated = () => created++;
        try
        {
            var node = NodeFactory.CreateFileIntake(new Point(0, 0));
            Assert.Equal("Instrument file", node.Title);
            Assert.Equal("Splits exports", node.Subtitle);
            Assert.Equal(1, created);
        }
        finally
        {
            (NodeFactory.Labels, NodeFactory.FileIntakeCreated) = (labels, hook);
        }
    }

    [Fact]
    public void LabelsStartup_ReadsTheFile_WritesTheDefaultOnFirstUseOnly_AndSurvivesGarbage()
    {
        var (labels, hook) = (NodeFactory.Labels, NodeFactory.FileIntakeCreated);
        var folder = Directory.CreateTempSubdirectory("labels-").FullName;
        try
        {
            var path = Path.Combine(folder, "EtlDesigner", "labels.txt");
            EtlDesigner.UI.Services.LabelsStartup.Install(path);
            Assert.False(File.Exists(path));
            Assert.Equal("File intake", NodeFactory.CreateFileIntake(new Point(0, 0)).Title);
            Assert.True(File.Exists(path));

            File.WriteAllText(path, "node.fileintake.title = My intake\n");
            NodeFactory.CreateFileIntake(new Point(0, 0));
            Assert.Equal("node.fileintake.title = My intake\n", File.ReadAllText(path));

            EtlDesigner.UI.Services.LabelsStartup.Install(path);
            Assert.Equal("My intake", NodeFactory.CreateFileIntake(new Point(0, 0)).Title);

            File.WriteAllBytes(path, [0xFF, 0x00, 0x3D, 0x0A, 0xC3, 0x28, 0x3D, 0x3D, 0x0A]);
            EtlDesigner.UI.Services.LabelsStartup.Install(path);
            Assert.Equal("File intake", NodeFactory.CreateFileIntake(new Point(0, 0)).Title);
        }
        finally
        {
            (NodeFactory.Labels, NodeFactory.FileIntakeCreated) = (labels, hook);
            ProjectEditorTests.DeleteTempFolder(folder);
        }
    }
}
