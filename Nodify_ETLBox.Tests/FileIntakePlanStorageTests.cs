using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using EtlDesigner.Engine.Intake;
using EtlDesigner.Engine.Project;
using EtlDesigner.UI.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>The saved layout of a File intake node in the .etlproj (G5b step 2).</summary>
public sealed class FileIntakePlanStorageTests : IDisposable
{
    private static readonly DateTime Saved = new(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _folder = Directory.CreateTempSubdirectory("intake-plan-store-").FullName;
    private readonly ITestOutputHelper _output;

    public FileIntakePlanStorageTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private static string Text(EditorViewModel editor) => ProjectSerializer.Serialize(editor.ToProject("p", null, Saved));

    private static EditorViewModel Reopen(string text)
    {
        var editor = new EditorViewModel();
        editor.FromProject(ProjectSerializer.Deserialize(text), null);
        return editor;
    }

    private static NodeViewModel AddIntake(EditorViewModel editor)
    {
        editor.AddNode(NodeType.FileIntake, new Point(10, 20));
        return editor.Nodes.Last();
    }

    /// <summary>A real plan: i01 scanned, one part renamed.</summary>
    private static async Task<string> PlanOf(string path, string mode)
    {
        string? plan = null;
        var vm = new IntakeStudioViewModel(new FileIntakeNodeSettings { Path = path, Mode = mode }, savePlan: json => plan = json);
        await IntakeStudioEditTests.Scan(vm);
        vm.SelectedContainer = vm.Containers[0];
        vm.NewName = "Renamed";
        vm.RenameCommand.Execute(null);
        vm.SaveCommand.Execute(null);
        return plan!;
    }

    private static JsonNode Settings(string projectText) => JsonNode.Parse(projectText)!["nodes"]![0]!["settings"]!;

    [Fact]
    public async Task Plan_RoundTripsInTheProject_AndTheNodeSaysLayoutEdited()
    {
        var plan = await PlanOf(IntakeTestData.PathOf("i01_sample"), FileIntakeNodeSettings.AutoDetect);
        var editor = new EditorViewModel();
        var node = AddIntake(editor);
        node.SetFileIntakeSettings(node.GetFileIntakeSettings() with { Path = IntakeTestData.PathOf("i01_sample") });
        editor.MarkClean();

        node.IntakePlanJson = plan;
        Assert.True(editor.IsDirty);
        Assert.Equal(NodeViewModel.LayoutEditedText, node.IntakeLayoutText);

        var text = Text(editor);
        // Since G5c1 the layout is one JSON string in the project.
        Assert.Equal(plan, Settings(text)[ProjectNodeSettings.LayoutPlan]!.GetValue<string>());

        var loaded = Reopen(text);
        var again = loaded.Nodes.Single();
        Assert.False(loaded.IsDirty);
        Assert.Equal(NodeViewModel.LayoutEditedText, again.IntakeLayoutText);
        Assert.Equal(JsonNode.Parse(plan)!.ToJsonString(), JsonNode.Parse(again.IntakePlanJson!)!.ToJsonString());
        Assert.Equal(text, Text(loaded));

        // Back to the automatic layout: the property is gone, not merged from the loaded file.
        again.IntakePlanJson = null;
        Assert.Null(Settings(Text(loaded))[ProjectNodeSettings.LayoutPlan]);
        Assert.Null(again.IntakeLayoutText);
    }

    [Fact]
    public void ProjectFromG4d_WithoutALayout_StillLoads()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "g4d_file_intake.etlproj");
        var loaded = Reopen(File.ReadAllText(path));

        var node = loaded.Nodes.Single();
        Assert.Equal(NodeType.FileIntake, node.NodeType);
        Assert.Equal(new FileIntakeNodeSettings { Path = @"C:\nowhere\i01_sample.txt", Mode = FileIntakeNodeSettings.MarkersMode, Encoding = IntakeEncodings.Utf8, MaxFileSizeMb = 200 },
            node.GetFileIntakeSettings());
        Assert.Null(node.IntakePlanJson);
        Assert.Null(node.IntakeLayoutText);
        Assert.Equal(["No issues found."], loaded.Issues);
        Assert.Null(Settings(Text(loaded))[ProjectNodeSettings.LayoutPlan]);
    }

    [Theory]
    [InlineData("{ \"formatVersion\": 1, \"sha256\": 5 }")]
    [InlineData("\"not a layout\"")]
    [InlineData("[1, 2]")]
    public async Task DamagedPlan_LoadsTheNodeWithoutIt_AndTheStudioSaysItCouldNotBeRead(string damaged)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "g4d_file_intake.etlproj"));
        var json = JsonNode.Parse(text)!;
        json["nodes"]![0]!["settings"]![ProjectNodeSettings.LayoutPlan] = JsonNode.Parse(damaged);
        json["nodes"]![0]!["settings"]!["path"] = IntakeTestData.PathOf("i01_sample");
        json["nodes"]![0]!["settings"]!["mode"] = FileIntakeNodeSettings.AutoDetect;

        var loaded = Reopen(json.ToJsonString());
        var node = loaded.Nodes.Single();
        Assert.Equal(NodeType.FileIntake, node.NodeType);
        Assert.Null(node.IntakeLayoutText);
        Assert.Equal(["No issues found."], loaded.Issues);

        var vm = new IntakeStudioViewModel(node.GetFileIntakeSettings(), savedPlanJson: node.IntakePlanJson);
        await IntakeStudioEditTests.Scan(vm);
        Assert.Equal($"{IntakeStudioViewModel.UnreadablePlanText} {IntakeStudioViewModel.AutomaticResultText}", vm.EditStatus);
        Assert.Equal(27, vm.Containers.Count);
    }

    /// <summary>The size of a saved layout: the plan JSON and the project file with it (reported, not limited).</summary>
    [Fact]
    public Task PlanSizes_I01_ForTheReport()
        => PlanSizes(
            ("i01 Auto-detect", IntakeTestData.PathOf("i01_sample"), FileIntakeNodeSettings.AutoDetect),
            ("i01 Markers", IntakeTestData.PathOf("i01_sample"), FileIntakeNodeSettings.MarkersMode));

    /// <summary>About 21 million characters of layout: slow and memory-hungry, so only with INTAKE_SLOW_TESTS=1.</summary>
    [SlowFact]
    public Task PlanSizes_36001Parts_ForTheReport()
    {
        var large = Path.Combine(_folder, "large.txt");
        var bytes = File.ReadAllBytes(IntakeTestData.PathOf("i01_sample"));
        using (var stream = File.Create(large))
        {
            for (var i = 0; i < 2000; i++)
            {
                stream.Write(bytes);
            }
        }

        return PlanSizes(("36,001 parts (Markers)", large, FileIntakeNodeSettings.MarkersMode));
    }

    private async Task PlanSizes(params (string Name, string Path, string Mode)[] cases)
    {
        foreach (var (name, path, mode) in cases)
        {
            var plan = await PlanOf(path, mode);
            var editor = new EditorViewModel();
            var node = AddIntake(editor);
            var empty = Text(editor).Length;
            node.IntakePlanJson = plan;
            var project = Text(editor);
            _output.WriteLine($"{name}: plan {plan.Length:N0} characters, project grows by {project.Length - empty:N0} characters to {project.Length:N0}");
            Assert.True(project.Length > empty);
        }
    }
}
