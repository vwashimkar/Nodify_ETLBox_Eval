using System.IO;
using System.Windows;
using EtlDesigner.Engine.Intake;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.Engine.Project;
using EtlDesigner.UI.Services;
using EtlDesigner.UI.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>A File intake -> CSV part -> ParseValue -> DotNetObject canvas, built in code as a user would connect it (G6c).</summary>
internal sealed record G6cCanvas(MainViewModel Main, NodeViewModel Intake, NodeViewModel Part, NodeViewModel Parse, NodeViewModel Output)
{
    public EditorViewModel Editor => Main.Editor;

    public static G6cCanvas Build(string? path, CsvPartSelector? selector, ParseValueSettings? parseValue = null, MainViewModel? main = null)
    {
        main ??= new MainViewModel(new ProjectEditorTests.FakeDialogs(), new ProjectEditorTests.MemoryRecent(), new IntakeStudioOpenTests.MemoryLineage());
        var editor = main.Editor;
        var intake = Add(editor, NodeType.FileIntake, 10);
        var part = Add(editor, NodeType.CsvPart, 180);
        var parse = Add(editor, NodeType.ParseValue, 385);
        var output = Add(editor, NodeType.DotNetObject, 552);
        if (path is not null)
        {
            intake.SetFileIntakeSettings(intake.GetFileIntakeSettings() with { Path = path });
        }

        if (selector is not null)
        {
            part.CsvPartSelectorJson = CsvPartSelectorJson.Serialize(selector);
        }

        parse.ParseValueSettings = parseValue ?? G6bParts.I10ParseValue();
        Connect(editor, intake.Outputs[0], part.Inputs[0]);
        Connect(editor, part.Outputs[0], parse.Inputs[0]);
        Connect(editor, parse.Outputs[0], output.Inputs[0]);
        editor.Validate();
        return new G6cCanvas(main, intake, part, parse, output);
    }

    public static NodeViewModel Add(EditorViewModel editor, NodeType type, double x)
    {
        editor.AddNode(type, new Point(x, 60));
        return editor.Nodes.Last();
    }

    public static void Connect(EditorViewModel editor, ConnectorViewModel source, ConnectorViewModel target)
    {
        editor.StartConnection(source);
        editor.CompleteConnection(target);
    }

    /// <summary>Waits for the part's background column read (if one started) and validates once more.</summary>
    public async Task SettleAsync()
    {
        await TestTimeout.Run(() => Main.CsvPartRefresh ?? Task.CompletedTask);
        Editor.Validate();
        await TestTimeout.Run(() => Main.CsvPartRefresh ?? Task.CompletedTask);
    }
}

/// <summary>Task G6c steps 1 to 3: the CSV part node, its rules, project file, refresh, Issues and Execute.</summary>
public sealed class CsvPartNodeTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _folder = Directory.CreateTempSubdirectory("g6c_node_").FullName;

    public CsvPartNodeTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private string I10Copy(string name = "i10.txt")
    {
        var path = Path.Combine(_folder, name);
        File.Copy(G6bParts.I10Path, path, overwrite: true);
        return path;
    }

    // ----- Step 1: node, ports, palette, connection rules -----

    [Fact]
    public void NewNode_HasOnePartsInput_OneRowsOutput_AndNoPartChosen()
    {
        var editor = new EditorViewModel();
        var node = G6cCanvas.Add(editor, NodeType.CsvPart, 0);
        Assert.Equal(NodeFactory.CsvPartTitle, node.Title);
        Assert.Equal(("Parts", PortKind.Parts, true), (node.Inputs.Single().Name, node.Inputs.Single().Kind, node.Inputs.Single().IsInput));
        Assert.Equal(("Rows", PortKind.Rows, false), (node.Outputs.Single().Name, node.Outputs.Single().Kind, node.Outputs.Single().IsInput));
        Assert.Empty(node.Settings);
        Assert.Equal(NodeViewModel.NoPartChosenText, node.CsvPartText);
        // G6d: a double-click opens the CSV part settings dialog.
        Assert.Same(node.ConfigureCommand, node.DoubleClickCommand);
        Assert.Null(G6cCanvas.Add(editor, NodeType.CsvFile, 100).CsvPartText);
    }

    public static TheoryData<NodeType> AllTypes()
    {
        var data = new TheoryData<NodeType>();
        foreach (var type in Enum.GetValues<NodeType>().Where(t => t != NodeType.Unknown))
        {
            data.Add(type);
        }

        return data;
    }

    /// <summary>Every source type into a CSV part, and a File intake and a CSV part into every target type.</summary>
    [Theory]
    [MemberData(nameof(AllTypes))]
    public void ConnectionRules_EveryPairWithAFileIntakeOrCsvPart(NodeType other)
    {
        // other -> CSV part: only a File intake.
        var editor = new EditorViewModel();
        var source = G6cCanvas.Add(editor, other, 0);
        var part = G6cCanvas.Add(editor, NodeType.CsvPart, 200);
        if (source.Outputs.Count > 0)
        {
            G6cCanvas.Connect(editor, source.Outputs[0], part.Inputs[0]);
            if (other == NodeType.FileIntake)
            {
                Assert.Single(editor.Connections);
            }
            else
            {
                Assert.Empty(editor.Connections);
                Assert.Equal(["CSV part: the input accepts only a File intake node. Connect the output of a File intake node."], editor.Issues);
            }
        }

        // File intake -> other: only a CSV part.
        editor = new EditorViewModel();
        var intake = G6cCanvas.Add(editor, NodeType.FileIntake, 0);
        var target = G6cCanvas.Add(editor, other, 200);
        if (target.Inputs.Count > 0)
        {
            G6cCanvas.Connect(editor, intake.Outputs[0], target.Inputs[0]);
            if (other == NodeType.CsvPart)
            {
                Assert.Single(editor.Connections);
            }
            else
            {
                Assert.Empty(editor.Connections);
                Assert.Equal(["File intake: the output feeds only a CSV part node for now. Connect it to the input of a CSV part node."], editor.Issues);
            }
        }

        // CSV part -> other: only ParseValue.
        editor = new EditorViewModel();
        part = G6cCanvas.Add(editor, NodeType.CsvPart, 0);
        target = G6cCanvas.Add(editor, other, 200);
        if (target.Inputs.Count > 0)
        {
            G6cCanvas.Connect(editor, part.Outputs[0], target.Inputs[0]);
            if (other == NodeType.ParseValue)
            {
                Assert.Single(editor.Connections);
            }
            else
            {
                Assert.Empty(editor.Connections);
                Assert.Equal(other == NodeType.CsvPart
                    ? ["CSV part: the input accepts only a File intake node. Connect the output of a File intake node."]
                    : ["CSV part: the output feeds only a ParseValue node. Connect it to the input of a ParseValue node."], editor.Issues);
            }
        }
    }

    [Fact]
    public void ConnectionRules_OneFileIntakePerCsvPart_OneSourcePerParseValue_AndAFileIntakeMayFeedTwoParts()
    {
        var editor = new EditorViewModel();
        var intake1 = G6cCanvas.Add(editor, NodeType.FileIntake, 0);
        var intake2 = G6cCanvas.Add(editor, NodeType.FileIntake, 0);
        var part1 = G6cCanvas.Add(editor, NodeType.CsvPart, 200);
        var part2 = G6cCanvas.Add(editor, NodeType.CsvPart, 200);
        var parse = G6cCanvas.Add(editor, NodeType.ParseValue, 400);
        var csv = G6cCanvas.Add(editor, NodeType.CsvFile, 0);

        G6cCanvas.Connect(editor, intake1.Outputs[0], part1.Inputs[0]);
        G6cCanvas.Connect(editor, intake2.Outputs[0], part1.Inputs[0]);
        Assert.Contains("CSV part: the input already has a connection. Remove it before connecting another node.", editor.Issues);

        G6cCanvas.Connect(editor, intake1.Outputs[0], part2.Inputs[0]);
        Assert.Equal(2, editor.Connections.Count);

        G6cCanvas.Connect(editor, part1.Outputs[0], parse.Inputs[0]);
        G6cCanvas.Connect(editor, csv.Outputs[0], parse.Inputs[0]);
        Assert.Equal(3, editor.Connections.Count);
        Assert.Contains("ParseValue: the input already has a connection. Remove it before connecting another node.", editor.Issues);
    }

    // ----- Step 2: node data, project file -----

    private static (ProjectFile Project, string Text) Save(EditorViewModel editor, string path)
    {
        var project = editor.ToProject("p", path, new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc));
        return (project, ProjectSerializer.Serialize(project));
    }

    private static EditorViewModel Open(string text, string path)
    {
        var editor = new EditorViewModel();
        editor.FromProject(ProjectSerializer.Deserialize(text), path);
        return editor;
    }

    [Fact]
    public async Task Project_RoundTripsTheSelector_AndTheConnections_AndSavesNothingDerived()
    {
        var path = I10Copy();
        var selector = await CsvPartLocatorTests.SelectorAsync(path);
        var canvas = G6cCanvas.Build(path, selector);
        await canvas.SettleAsync();
        Assert.NotNull(canvas.Part.KnownColumns);

        var projectPath = Path.Combine(_folder, "p.etlproj");
        var (project, text) = Save(canvas.Editor, projectPath);
        var saved = project.Nodes.Single(n => n.Type == ProjectNodeTypes.CsvPart);
        Assert.Equal(["selector"], saved.Settings!.Value.EnumerateObject().Select(p => p.Name));
        Assert.Equal(CsvPartSelectorJson.Serialize(selector), ProjectNodeSettings.ReadCsvPartSelector(saved.Settings.Value));

        var opened = Open(text, projectPath);
        var part = opened.Nodes.Single(n => n.NodeType == NodeType.CsvPart);
        Assert.Equal(canvas.Part.CsvPartSelectorJson, part.CsvPartSelectorJson);
        Assert.Equal(CsvPartSelectorJson.Serialize(selector), CsvPartSelectorJson.Serialize(part.CsvPartSelector!));
        Assert.Equal(3, opened.Connections.Count);
        Assert.Null(part.KnownColumns);
        Assert.Equal(text, Save(opened, projectPath).Text);
    }

    private static string RepoRoot()
        => Path.GetDirectoryName(WpfHost.ReportsFolder())!;

    /// <summary>
    /// The sample project (written by the serializer, with relative paths) is written byte for byte as it is
    /// stored, opened from its place in the source tree.
    /// </summary>
    [Fact]
    public void SampleProjectWithoutACsvPart_IsWrittenByteIdentical()
    {
        var path = Path.Combine(RepoRoot(), "Nodify_ETLBox", "SampleData", "Projects", "c01_pipeline.etlproj");
        var text = File.ReadAllText(path);
        var original = ProjectSerializer.Deserialize(text);
        var again = ProjectSerializer.Serialize(Open(text, path).ToProject(original.ProjectName, path, original.SavedUtc));
        Assert.Equal(text.ReplaceLineEndings("\n"), again.ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// The G4d project (File intake, hand-written without relativePath) gains relativePath on its first save, as
    /// before G6c (RelativePathFor is unchanged); after that, open and save give the same bytes, and the File intake
    /// settings have no new property (the output port is not saved).
    /// </summary>
    [Fact]
    public void FileIntakeProject_SavesTheSameBytesEachTime_AndNoNewProperty()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "TestData", "g4d_file_intake.etlproj");
        var path = Path.Combine(_folder, "g4d.etlproj");
        var original = ProjectSerializer.Deserialize(File.ReadAllText(source));
        var first = ProjectSerializer.Serialize(Open(File.ReadAllText(source), path).ToProject(original.ProjectName, path, original.SavedUtc));
        var second = ProjectSerializer.Serialize(Open(first, path).ToProject(original.ProjectName, path, original.SavedUtc));
        Assert.Equal(first, second);
        var settings = ProjectSerializer.Deserialize(second).Nodes.Single(n => n.Type == ProjectNodeTypes.FileIntake).Settings!.Value;
        Assert.Equal(["path", "relativePath", "mode", "encoding", "maxFileSizeMb"], settings.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void CorruptSelector_OpensWithoutACrash_GivesAnIssuesLine_AndIsWrittenBackAsItWas()
    {
        var editor = new EditorViewModel();
        var part = G6cCanvas.Add(editor, NodeType.CsvPart, 0);
        part.CsvPartSelectorJson = "{\"formatVersion\":1,\"template\":";
        var projectPath = Path.Combine(_folder, "corrupt.etlproj");
        var (_, text) = Save(editor, projectPath);

        var opened = Open(text, projectPath);
        var node = opened.Nodes.Single();
        Assert.Equal(NodeType.CsvPart, node.NodeType);
        Assert.Equal(CsvPartSelectorJson.UnreadableMessage, node.CsvPartSelectorError);
        Assert.Contains("CSV part: " + CsvPartSelectorJson.UnreadableMessage, opened.Issues);
        Assert.Equal(CsvPartSelectorJson.UnreadableMessage, node.CsvPartStatus);
        Assert.Equal("Not configured", node.ConfigurationStatus);
        Assert.Equal(text, Save(opened, projectPath).Text);
    }

    [Fact]
    public void MissingSelector_GivesItsIssuesLine()
    {
        var text = "{\"formatVersion\":1,\"projectName\":\"p\",\"nodes\":[{\"id\":\"6f0f7d3e-46a7-4b0a-9d55-0a9b2b8f0a11\",\"type\":\"CsvPart\",\"name\":\"CSV part\",\"x\":0,\"y\":0,\"settings\":{}}],\"connections\":[]}";
        var opened = Open(text, Path.Combine(_folder, "missing.etlproj"));
        var node = opened.Nodes.Single();
        Assert.Equal(NodeType.CsvPart, node.NodeType);
        Assert.Null(node.CsvPartSelectorJson);
        Assert.Contains("CSV part: " + EditorViewModel.CsvPartNoSelector, opened.Issues);
        Assert.Contains("CSV part: " + EditorViewModel.CsvPartNoFileIntake, opened.Issues);
    }

    // ----- Step 3: refresh, Issues, Execute, Generate DLL gate -----

    [Fact]
    public async Task Refresh_ReadsTheRunsColumns_ShowsThePart_AndExecuteEqualsTheStandaloneCsv()
    {
        var path = I10Copy();
        var selector = await CsvPartLocatorTests.SelectorAsync(path);
        var canvas = G6cCanvas.Build(path, selector);
        await canvas.SettleAsync();

        Assert.Equal("Configured", canvas.Part.ConfigurationStatus);
        Assert.Equal(EditorViewModel.CsvPartReady, canvas.Part.CsvPartStatus);
        var design = await EtlDesigner.Engine.Csv.CsvPartColumns.ReadAsync(path, selector, CancellationToken.None);
        Assert.Equal(design.Columns, canvas.Part.KnownColumns);
        Assert.Equal($"Part {selector.PartIndex + 1} of the file{Environment.NewLine}Template: {selector.Template.Name}{Environment.NewLine}Comma table, {design.Columns.Count} columns",
            canvas.Part.CsvPartText);
        Assert.Equal("Configured", canvas.Parse.ConfigurationStatus);
        Assert.DoesNotContain(canvas.Editor.Issues, i => i.StartsWith("CSV part", StringComparison.Ordinal));

        Assert.True(canvas.Main.TryBuildExecuteDefinition(out var definition, out var reason), reason);
        Assert.Equal(CsvPartSelectorJson.Serialize(selector), CsvPartSelectorJson.Serialize(definition!.CsvPart!));
        Assert.Equal(path, definition.CsvFile.Path);

        var execute = new ExecuteViewModel(definition);
        await TestTimeout.Run(execute.RunAsync);
        var run = execute.Result!;
        Assert.Null(run.Error);
        var standalone = G6bParts.Write(_folder, "table.csv", G6bParts.TableOf(File.ReadAllText(path)));
        var whole = await G6bParts.Run(G6bParts.StandaloneDefinition(standalone), null);
        Assert.Equal(whole.Columns, run.Columns);
        Assert.Equal(G6bParts.RowTexts(whole.Columns, whole.Rows), G6bParts.RowTexts(run.Columns, run.Rows));
        Assert.Equal(whole.Counts, run.Counts);
        _output.WriteLine(execute.PartFactsText);
        Assert.StartsWith("CSV part: file i10.txt · template '", execute.PartFactsText);
        Assert.Contains($"part {run.CsvPart!.PartNumber}, lines {run.CsvPart.FirstLine} to {run.CsvPart.LastLine}", execute.PartFactsText);
    }

    [Fact]
    public async Task ParseValueUpstream_IsThePart_WithTheRunsColumns_AndItsSampleComesFromThePartOnly()
    {
        var path = I10Copy();
        var canvas = G6cCanvas.Build(path, await CsvPartLocatorTests.SelectorAsync(path));
        await canvas.SettleAsync();

        var upstream = ParseValueUpstreamResolver.Resolve(canvas.Parse, canvas.Editor.Connections);
        Assert.True(upstream.IsResolved, upstream.Message);
        Assert.Same(canvas.Part, upstream.CsvNode);
        Assert.Equal(canvas.Part.KnownColumns, upstream.KeptColumns);
        Assert.NotNull(upstream.Part);

        var studio = new ParseStudioViewModel(canvas.Parse, upstream);
        await TestTimeout.Run(() => studio.ReloadNowAsync());
        var sample = await new EtlDesigner.Engine.Csv.CsvPreviewReader().ReadAsync(upstream.Part!, 100, CancellationToken.None);
        Assert.Null(sample.Error);
        Assert.Equal(canvas.Part.KnownColumns, sample.Columns);
        Assert.Equal(5, sample.Rows.Count);
    }

    public static TheoryData<string, CsvPartFailure> Failures() => PipelineRunnerCsvPartTests.Failures();

    /// <summary>Every locator failure shows its exact sentence (G6a/G6b, unchanged) on the node and in Issues; a missing file has the canvas line.</summary>
    [Theory]
    [MemberData(nameof(Failures))]
    public async Task EachLocatorFailure_ShowsItsSentence_OnTheNode_AndInIssues(string kind, CsvPartFailure reason)
    {
        var selector = await G6bParts.I10SelectorAsync();
        var (path, used) = PipelineRunnerCsvPartTests.FailureCase(kind, selector, _folder);
        var canvas = G6cCanvas.Build(path, used);
        await canvas.SettleAsync();

        var expected = reason == CsvPartFailure.FileMissing
            ? EditorViewModel.CsvPartFileMissing(canvas.Intake.Title, path)
            : (await CsvPartLocator.LocateAsync(path, used, CancellationToken.None)).Message;
        _output.WriteLine($"{kind}: {canvas.Part.CsvPartStatus}");
        Assert.Equal(expected, canvas.Part.CsvPartStatus);
        Assert.Contains("CSV part: " + expected, canvas.Editor.Issues);
        Assert.Equal("Not configured", canvas.Part.ConfigurationStatus);
        Assert.Null(canvas.Part.KnownColumns);
        Assert.False(canvas.Main.TryBuildExecuteDefinition(out _, out _));
    }

    [Fact]
    public async Task IssuesLines_NoFileIntake_NoFile_NotToParseValue_AndTwoCsvParts()
    {
        var editor = new EditorViewModel();
        var part = G6cCanvas.Add(editor, NodeType.CsvPart, 0);
        part.CsvPartSelectorJson = CsvPartSelectorJson.Serialize(await G6bParts.I10SelectorAsync());
        editor.Validate();
        Assert.Contains("CSV part: " + EditorViewModel.CsvPartNoFileIntake, editor.Issues);
        Assert.Contains("CSV part: " + EditorViewModel.CsvPartNotToParseValue, editor.Issues);
        Assert.Equal(EditorViewModel.CsvPartNoFileIntake, part.CsvPartStatus);

        var intake = G6cCanvas.Add(editor, NodeType.FileIntake, 0);
        G6cCanvas.Connect(editor, intake.Outputs[0], part.Inputs[0]);
        editor.Validate();
        Assert.Contains("CSV part: " + EditorViewModel.CsvPartNoFile, editor.Issues);

        G6cCanvas.Add(editor, NodeType.CsvPart, 200);
        editor.Validate();
        Assert.Equal(2, editor.Issues.Count(i => i.EndsWith(EditorViewModel.CsvPartSeveral(2), StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TwoCsvParts_BlockExecute()
    {
        var path = I10Copy();
        var canvas = G6cCanvas.Build(path, await CsvPartLocatorTests.SelectorAsync(path));
        await canvas.SettleAsync();
        Assert.True(canvas.Main.TryBuildExecuteDefinition(out _, out _));

        G6cCanvas.Add(canvas.Editor, NodeType.CsvPart, 300);
        canvas.Editor.Validate();
        Assert.False(canvas.Main.TryBuildExecuteDefinition(out _, out var reason));
        Assert.Equal(MainViewModel.ExecutePartChainReason, reason);
    }

    /// <summary>The gate hash covers the selector and the file: changing either asks for Execute again.</summary>
    [Fact]
    public async Task GenerateDllGate_ChangingTheSelectorOrTheFile_InvalidatesTheTestedRun()
    {
        var path = I10Copy();
        var selector = await CsvPartLocatorTests.SelectorAsync(path);
        var canvas = G6cCanvas.Build(path, selector);
        await canvas.SettleAsync();

        async Task Tested()
        {
            Assert.True(canvas.Main.TryBuildExecuteDefinition(out var definition, out var reason), reason);
            var run = await G6bParts.Run(definition!, null);
            Assert.Null(run.Error);
            canvas.Main.OnExecuteRunFinished(definition!, run);
            Assert.Null(canvas.Main.GenerateDllBlockedReason);
        }

        await Tested();
        Assert.True(canvas.Main.TryBuildExecuteDefinition(out var first, out _));

        // Another selector (the same part, another encoding setting).
        canvas.Part.CsvPartSelectorJson = CsvPartSelectorJson.Serialize(selector with { Encoding = IntakeEncodings.Utf8 });
        await canvas.SettleAsync();
        Assert.True(canvas.Main.TryBuildExecuteDefinition(out var second, out var r2), r2);
        Assert.NotEqual(MainViewModel.PipelineHash(first!), MainViewModel.PipelineHash(second!));
        Assert.Equal(MainViewModel.GenerateDllNotTestedHint, canvas.Main.GenerateDllBlockedReason);

        await Tested();

        // Another file in the File intake node.
        var other = I10Copy("other.txt");
        canvas.Intake.SetFileIntakeSettings(canvas.Intake.GetFileIntakeSettings() with { Path = other });
        await canvas.SettleAsync();
        Assert.True(canvas.Main.TryBuildExecuteDefinition(out var third, out var r3), r3);
        Assert.NotEqual(MainViewModel.PipelineHash(second!), MainViewModel.PipelineHash(third!));
        Assert.Equal(MainViewModel.GenerateDllNotTestedHint, canvas.Main.GenerateDllBlockedReason);
    }

    [Fact]
    public void ACsvFilePipeline_StillBuildsItsOwnDefinition_WithAFileIntakeNodeBeside()
    {
        var main = new MainViewModel(new ProjectEditorTests.FakeDialogs(), new ProjectEditorTests.MemoryRecent());
        var csv = PipelineDefinitionTests.CompositePath("c01_basic.csv");
        var (csvNode, _, _) = ProjectEditorTests.BuildC01(main.Editor, csv);
        // The columns are set here, as in FileIntakeNodeTests.C01Graph: the app's own background read of them is not
        // awaited by anything (CLAUDE.md), and waiting on ProjectRefresh (a project open) failed once in a full run.
        csvNode.KnownColumns = File.ReadLines(csv).First().Split(',');
        G6cCanvas.Add(main.Editor, NodeType.FileIntake, 0);
        main.Editor.Validate();
        Assert.True(main.TryBuildExecuteDefinition(out var definition, out var reason), reason);
        Assert.Null(definition!.CsvPart);
    }

    // ----- The project fixture for the user -----

    private const string FixtureVariable = "G6C_WRITE_FIXTURE";

    /// <summary>
    /// TestData/g6c_i10_part.etlproj with a copy of i10 next to it (g6c_i10_example.txt), so the user can open it.
    /// G6C_WRITE_FIXTURE=1 writes both into the source tree (node ids are new each time); otherwise the copy in
    /// the test folder is opened, refreshed and executed.
    /// </summary>
    [Fact]
    public async Task Fixture_G6cI10Part_OpensReadsThePart_AndExecutes()
    {
        if (Environment.GetEnvironmentVariable(FixtureVariable) == "1")
        {
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData"));
            var example = Path.Combine(source, "g6c_i10_example.txt");
            File.Copy(G6bParts.I10Path, example, overwrite: true);
            var built = G6cCanvas.Build(example, await CsvPartLocatorTests.SelectorAsync(example));
            var projectPath = Path.Combine(source, "g6c_i10_part.etlproj");
            File.WriteAllText(projectPath, ProjectSerializer.Serialize(built.Editor.ToProject("g6c_i10_part", projectPath,
                new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc))));
        }

        // Opened from a temp copy of the folder, so the files in the test output stay as they are.
        foreach (var name in new[] { "g6c_i10_part.etlproj", "g6c_i10_example.txt" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", name), Path.Combine(_folder, name));
        }

        var dialogs = new ProjectEditorTests.FakeDialogs();
        var main = new MainViewModel(dialogs, new ProjectEditorTests.MemoryRecent(), new IntakeStudioOpenTests.MemoryLineage());
        await TestTimeout.Run(() => main.OpenProjectAsync(Path.Combine(_folder, "g6c_i10_part.etlproj")));
        await TestTimeout.Run(() => main.ProjectRefresh);
        Assert.Empty(dialogs.Errors);

        var part = main.Editor.Nodes.Single(n => n.NodeType == NodeType.CsvPart);
        // The saved absolute path wins when it exists (ProjectPaths.Resolve); otherwise the relative one next to the project.
        Assert.Equal("g6c_i10_example.txt", Path.GetFileName(main.Editor.CsvPartFilePath(part)));
        Assert.Equal(EditorViewModel.CsvPartReady, part.CsvPartStatus);
        Assert.True(main.TryBuildExecuteDefinition(out var definition, out var reason), reason);
        var run = await G6bParts.Run(definition!, null);
        Assert.Null(run.Error);
        Assert.True(run.Counts.Out > 0);
    }
}

/// <summary>Task G6c step 3: the DLL generated from a canvas-built CSV part pipeline equals the app.</summary>
[Collection("CSV part DLL")]
public sealed class CsvPartCanvasDllTests
{
    private readonly CsvPartDllFixture _fixture;

    public CsvPartCanvasDllTests(CsvPartDllFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TheCanvasPipeline_GeneratesADll_ThatEqualsTheApp()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_fixture.Folder, "canvas")).FullName;
        var path = Path.Combine(folder, "i10.txt");
        File.Copy(G6bParts.I10Path, path, overwrite: true);
        var canvas = G6cCanvas.Build(path, await CsvPartLocatorTests.SelectorAsync(path));
        await canvas.SettleAsync();
        Assert.True(canvas.Main.TryBuildExecuteDefinition(out var definition, out var reason), reason);

        var app = await G6bParts.Run(definition!, null);
        canvas.Main.OnExecuteRunFinished(definition!, app);
        Assert.Null(canvas.Main.GenerateDllBlockedReason);

        var dll = await _fixture.GetAsync("canvas", definition!);
        var (result, facts) = await PartDll.RunAsync(dll, path);
        PartDll.Equal(app, result, facts);
    }
}
