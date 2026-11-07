using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.Engine.Project;
using EtlDesigner.UI.Services;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>Save and Open in the view models, with a fake dialog service and real files.</summary>
public sealed class ProjectEditorTests : IDisposable
{
    private static readonly DateTime Saved = new(2026, 11, 6, 9, 30, 0, DateTimeKind.Utc);

    private readonly string _folder = Directory.CreateTempSubdirectory("etlproj-").FullName;

    public void Dispose() => DeleteTempFolder(_folder);

    /// <summary>
    /// MainViewModel reads a new graph's CSV columns in the background and no test waits for that
    /// read, so the file can still be open here. In 400 rounds on 2026-11-06 a delete right after
    /// building a graph failed 306 times, and right after a plain read 0 times. Five seconds is far
    /// longer than that read, so a file that stays locked still fails the test.
    /// </summary>
    internal static void DeleteTempFolder(string folder)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 100)
            {
                Thread.Sleep(50);
            }
        }
    }

    private string InFolder(string name) => Path.Combine(_folder, name);

    internal sealed class FakeDialogs : IDialogService
    {
        public UnsavedChangesChoice Answer { get; set; } = UnsavedChangesChoice.Cancel;
        public string? PathToOpen { get; set; }
        public string? PathToSave { get; set; }
        public Exception? AskFails { get; set; }
        public List<string> Asked { get; } = [];
        public List<string> Errors { get; } = [];

        public void ShowCsvPreview(NodeViewModel node, CsvFileSettings settings) { }
        public void ShowParseValue(NodeViewModel node, ParseValueUpstream upstream) { }
        public void ShowExecute(ExecuteViewModel viewModel) { }
        public string? PickCsvFile(string? currentPath) => null;
        public void ShowGenerateDll(GenerateDllViewModel viewModel) { }
        public string? PickFolder(string? currentFolder) => null;
        public string? PickProjectToOpen(string? currentPath) => PathToOpen;
        public string? PickProjectToSave(string? currentPath, string projectName) => PathToSave;
        public void ShowError(string title, string message) => Errors.Add(message);

        public UnsavedChangesChoice AskUnsavedChanges(string projectName)
        {
            Asked.Add(projectName);
            return AskFails is null ? Answer : throw AskFails;
        }
    }

    internal sealed class MemoryRecent : IRecentProjects
    {
        public List<string> Entries { get; } = [];
        public IReadOnlyList<string> Load() => Entries.ToList();
        public void Add(string path) { Entries.Remove(path); Entries.Insert(0, path); }
        public void Remove(string path) => Entries.Remove(path);
    }

    private static (MainViewModel Main, FakeDialogs Dialogs, MemoryRecent Recent) NewMain()
    {
        var dialogs = new FakeDialogs();
        var recent = new MemoryRecent();
        return (new MainViewModel(dialogs, recent), dialogs, recent);
    }

    private static void Connect(EditorViewModel editor, ConnectorViewModel source, ConnectorViewModel target)
    {
        editor.StartConnection(source);
        editor.CompleteConnection(target);
    }

    /// <summary>CsvFile -> ParseValue -> DotNetObject, as a user builds it, on the C01 file.</summary>
    internal static (NodeViewModel Csv, NodeViewModel Parse, NodeViewModel Output) BuildC01(EditorViewModel editor, string csvPath)
    {
        editor.AddNode(NodeType.CsvFile, new Point(40, 60));
        editor.AddNode(NodeType.ParseValue, new Point(320, 60));
        editor.AddNode(NodeType.DotNetObject, new Point(640, 80.5));
        var (csv, parse, output) = (editor.Nodes[0], editor.Nodes[1], editor.Nodes[2]);

        csv.Settings.Single(s => s.Key == "FilePath").Value = csvPath;
        csv.Settings.Single(s => s.Key == "Separator").Value = "Comma";
        csv.Settings.Single(s => s.Key == "PreviewRows").Value = "7";
        csv.ColumnSelection.Mode = CsvColumnSelectionMode.Ignore;
        csv.ColumnSelection.Names.Add("extra_01");
        parse.ParseValueSettings = PipelineDefinitionTests.C01Definition().ParseValue;

        Connect(editor, csv.Outputs[0], parse.Inputs[0]);
        Connect(editor, parse.Outputs[0], output.Inputs[0]);
        return (csv, parse, output);
    }

    private static string Text(EditorViewModel editor, string? path = null, string name = "p")
        => ProjectSerializer.Serialize(editor.ToProject(name, path, Saved));

    private static EditorViewModel Reopen(string text, string? path = null)
    {
        var editor = new EditorViewModel();
        editor.FromProject(ProjectSerializer.Deserialize(text), path);
        return editor;
    }

    private string CreateCsv(string name = "in.csv")
    {
        File.Copy(PipelineDefinitionTests.CompositePath("c01_basic.csv"), InFolder(name), overwrite: true);
        return InFolder(name);
    }

    private static Task Settled(MainViewModel main) => TestTimeout.Run(() => main.ProjectRefresh);

    [Fact]
    public void NewGraph_RoundTrips_ThroughTheCanvas_ByteForByte()
    {
        var editor = new EditorViewModel();
        var (csv, parse, output) = BuildC01(editor, CreateCsv());
        var projectFile = InFolder("p.etlproj");
        var text = Text(editor, projectFile);

        var loaded = Reopen(text, projectFile);

        Assert.Equal(text, Text(loaded, projectFile));
        Assert.Equal(new[] { csv.Id, parse.Id, output.Id }, loaded.Nodes.Select(n => n.Id));
        Assert.Equal(new[] { new Point(40, 60), new Point(320, 60), new Point(640, 80.5) }, loaded.Nodes.Select(n => n.Location));
        Assert.Equal(2, loaded.Connections.Count);
        Assert.True(loaded.Nodes[0].Outputs[0].IsConnected);
        Assert.True(loaded.Nodes[2].Inputs[0].IsConnected);
    }

    [Fact]
    public void LoadedNode_IsIdenticalToANewNodeOfTheSameType()
    {
        var editor = new EditorViewModel();
        var (csv, parse, output) = BuildC01(editor, CreateCsv());

        var loaded = Reopen(Text(editor));

        Assert.Equal(csv.GetCsvFileSettings(), loaded.Nodes[0].GetCsvFileSettings());
        Assert.Equal("Comma", loaded.Nodes[0].Settings.Single(s => s.Key == "Separator").Value);
        Assert.Equal(csv.ColumnSelection.Names, loaded.Nodes[0].ColumnSelection.Names);
        Assert.Equal(SettingsJson.Serialize(parse.ParseValueSettings!), SettingsJson.Serialize(loaded.Nodes[1].ParseValueSettings!));
        Assert.Equal("sample", loaded.Nodes[1].Settings.Single(s => s.Key == "SourceColumn").Value);
        Assert.Equal(output.GetOutputSettings(), loaded.Nodes[2].GetOutputSettings());
        Assert.Equal(csv.Settings.Select(s => s.Key), loaded.Nodes[0].Settings.Select(s => s.Key));
        Assert.Equal(csv.Outputs.Select(c => c.Kind), loaded.Nodes[0].Outputs.Select(c => c.Kind));
    }

    [Fact]
    public void Loading_SuggestsNothingFromTheFile_AndKeepsTheSavedSettings()
    {
        var editor = new EditorViewModel();
        editor.AddNode(NodeType.CsvFile, new Point());
        var csv = editor.Nodes[0];
        csv.Settings.Single(s => s.Key == "FilePath").Value = CreateCsv();
        csv.Settings.Single(s => s.Key == "Separator").Value = "Semicolon";
        csv.Settings.Single(s => s.Key == "HasHeader").BoolValue = false;

        var loaded = Reopen(Text(editor)).Nodes[0];

        Assert.Equal("Semicolon", loaded.Settings.Single(s => s.Key == "Separator").Value);
        Assert.False(loaded.Settings.Single(s => s.Key == "HasHeader").BoolValue);
        Assert.All(loaded.Settings, s => Assert.Null(s.InfoMessage));
    }

    [Fact]
    public void LegacySample_RoundTrips_ByteForByte_WithItsKeys()
    {
        var editor = new EditorViewModel();
        editor.LoadSampleGraph();
        var text = Text(editor);

        var loaded = Reopen(text);

        Assert.Equal(text, Text(loaded));
        Assert.Equal(4, loaded.Connections.Count);
        Assert.Equal("United States", loaded.Nodes[1].Settings.Single(s => s.Key == "Country").Value);
        Assert.Equal("employee_list.csv", (string?)JsonNode.Parse(text)!["nodes"]![0]!["settings"]!["FilePath"]);
    }

    [Fact]
    public void ANodeTitle_IsSaved_AndTheTypeIsNot_TheLabel()
    {
        var editor = new EditorViewModel();
        editor.AddNode(NodeType.ToJson, new Point());
        editor.Nodes[0].Title = "My step";
        var text = Text(editor);

        Assert.Equal("ToJson", (string?)JsonNode.Parse(text)!["nodes"]![0]!["type"]);
        Assert.Equal("My step", Reopen(text).Nodes[0].Title);
    }

    [Fact]
    public void Viewport_IsSaved_AndRestored()
    {
        var editor = new EditorViewModel { ViewportZoom = 0.5, ViewportLocation = new Point(-30, 12) };

        var loaded = Reopen(Text(editor));

        Assert.Equal(0.5, loaded.ViewportZoom);
        Assert.Equal(new Point(-30, 12), loaded.ViewportLocation);
    }

    [Fact]
    public void Connections_SurviveAChangeOfTheNodeOrder()
    {
        var editor = new EditorViewModel();
        BuildC01(editor, CreateCsv());
        var json = JsonNode.Parse(Text(editor))!.AsObject();
        var nodes = json["nodes"]!.AsArray();
        var first = nodes[0]!.DeepClone();
        nodes.RemoveAt(0);
        nodes.Add(first);

        var loaded = Reopen(json.ToJsonString());

        Assert.Equal(NodeType.CsvFile, loaded.Nodes[2].NodeType);
        Assert.Contains(loaded.Connections, c => c.Source.ParentNode.NodeType == NodeType.CsvFile && c.Target.ParentNode.NodeType == NodeType.ParseValue);
        Assert.Contains(loaded.Connections, c => c.Source.ParentNode.NodeType == NodeType.ParseValue && c.Target.ParentNode.NodeType == NodeType.DotNetObject);
    }

    [Fact]
    public void RuleViolation_InAFile_IsAnIssuesLine_NotAnException_AndStaysInTheFile()
    {
        var editor = new EditorViewModel();
        var (csv, parse, output) = BuildC01(editor, CreateCsv());
        var json = JsonNode.Parse(Text(editor))!.AsObject();
        json["connections"]!.AsArray().Add(new JsonObject
        {
            ["fromNode"] = csv.Id.ToString(), ["fromConnector"] = "Rows",
            ["toNode"] = output.Id.ToString(), ["toConnector"] = "Parsed"
        });

        var loaded = Reopen(json.ToJsonString());

        Assert.Equal(2, loaded.Connections.Count);
        var issue = Assert.Single(loaded.Issues, i => i.StartsWith("Project:"));
        Assert.Contains("was not restored", issue);
        Assert.Contains("accepts only a ParseValue node", issue);

        loaded.Validate();
        Assert.Contains(issue, loaded.Issues);
        Assert.Equal(3, JsonNode.Parse(Text(loaded))!["connections"]!.AsArray().Count);
    }

    [Fact]
    public void DanglingConnection_AndUnknownConnectorKey_AreIssuesLines()
    {
        var editor = new EditorViewModel();
        var (csv, parse, _) = BuildC01(editor, CreateCsv());
        var json = JsonNode.Parse(Text(editor))!.AsObject();
        var connections = json["connections"]!.AsArray();
        connections.Add(new JsonObject { ["fromNode"] = Guid.NewGuid().ToString(), ["fromConnector"] = "Rows", ["toNode"] = parse.Id.ToString(), ["toConnector"] = "Rows" });
        connections.Add(new JsonObject { ["fromNode"] = csv.Id.ToString(), ["fromConnector"] = "Nope", ["toNode"] = parse.Id.ToString(), ["toConnector"] = "Rows" });

        var loaded = Reopen(json.ToJsonString());

        Assert.Contains(loaded.Issues, i => i.Contains("which is not in the file"));
        Assert.Contains(loaded.Issues, i => i.Contains("has no connector 'Nope'"));
    }

    private string WithUnknownNode(EditorViewModel editor, NodeViewModel connectedTo)
    {
        var id = Guid.NewGuid();
        var json = JsonNode.Parse(Text(editor))!.AsObject();
        var nodes = json["nodes"]!.AsArray();
        nodes.Insert(1, JsonNode.Parse($$"""{ "id": "{{id}}", "type": "JsonCheckpoint", "name": "Keep", "x": 5.0, "y": 6, "settings": { "path": "w.json" }, "note": "n" }"""));
        json["connections"]!.AsArray().Insert(0, new JsonObject
        {
            ["fromNode"] = id.ToString(), ["fromConnector"] = "Out",
            ["toNode"] = connectedTo.Id.ToString(), ["toConnector"] = "In"
        });
        return json.ToJsonString(new JsonSerializerOptions(ProjectSerializer.Options));
    }

    [Fact]
    public void UnknownNode_IsAPlaceholder_WithItsIssuesLine_AndIsWrittenBackExactly()
    {
        var editor = new EditorViewModel();
        var (_, _, output) = BuildC01(editor, CreateCsv());
        var text = WithUnknownNode(editor, output);

        var loaded = Reopen(text);

        var node = loaded.Nodes[1];
        Assert.Equal(NodeType.Unknown, node.NodeType);
        Assert.Equal("Unknown node (JsonCheckpoint)", node.Title);
        Assert.Equal(new Point(5, 6), node.Location);
        const string line = "Node 'Keep' has the type 'JsonCheckpoint', which this version does not know. It is kept in the project and ignored.";
        Assert.Contains(line, loaded.Issues);

        loaded.Validate();
        Assert.Contains(line, loaded.Issues);
        Assert.Equal(text, Text(loaded));
    }

    [Fact]
    public void DeletingAnUnknownNode_RemovesItAndItsConnectionsFromTheFile()
    {
        var editor = new EditorViewModel();
        var (_, _, output) = BuildC01(editor, CreateCsv());
        var loaded = Reopen(WithUnknownNode(editor, output));

        loaded.SelectedNodes.Add(loaded.Nodes[1]);
        loaded.DeleteSelected();

        var json = JsonNode.Parse(Text(loaded))!;
        Assert.Equal(3, json["nodes"]!.AsArray().Count);
        Assert.Equal(2, json["connections"]!.AsArray().Count);
        Assert.DoesNotContain(loaded.Issues, i => i.Contains("JsonCheckpoint"));
    }

    [Fact]
    public void Settings_ThatCannotBeRead_MakeAPlaceholder_NotAnException()
    {
        var editor = new EditorViewModel();
        BuildC01(editor, CreateCsv());
        var json = JsonNode.Parse(Text(editor))!.AsObject();
        json["nodes"]![2]!["settings"]!["kind"] = "SomethingNew";
        var text = json.ToJsonString(new JsonSerializerOptions(ProjectSerializer.Options));

        var loaded = Reopen(text);

        Assert.Equal(NodeType.Unknown, loaded.Nodes[2].NodeType);
        Assert.Contains(loaded.Issues, i => i.Contains("the settings could not be read"));
        Assert.Equal(text, Text(loaded));
    }

    [Fact]
    public void UnknownSettings_AndExtras_OfAKnownNode_SurviveTheCanvas()
    {
        var editor = new EditorViewModel();
        BuildC01(editor, CreateCsv());
        var json = JsonNode.Parse(Text(editor))!.AsObject();
        json["nodes"]![0]!["settings"]!["quoteCharacter"] = "'";
        json["nodes"]![0]!["colour"] = "blue";
        json["connections"]![0]!["label"] = "main";
        json["reviewedBy"] = "QA";

        var written = JsonNode.Parse(Text(Reopen(json.ToJsonString())))!;

        Assert.Equal("'", (string?)written["nodes"]![0]!["settings"]!["quoteCharacter"]);
        Assert.Equal("blue", (string?)written["nodes"]![0]!["colour"]);
        Assert.Equal("main", (string?)written["connections"]![0]!["label"]);
        Assert.Equal("QA", (string?)written["reviewedBy"]);
    }

    [Fact]
    public void ARelativePath_StaysInTheFile_WhileTheFileIsMissing()
    {
        var editor = new EditorViewModel();
        var csvPath = CreateCsv();
        BuildC01(editor, csvPath);
        var projectFile = InFolder("p.etlproj");
        var text = Text(editor, projectFile);
        File.Delete(csvPath);

        var movedProject = Path.Combine(_folder, "other", "p.etlproj");

        Assert.Equal("in.csv", (string?)JsonNode.Parse(Text(Reopen(text, movedProject), movedProject))!["nodes"]![0]!["settings"]!["relativePath"]);
    }

    [Fact]
    public void DirtyFlag_IsSet_ByEveryKindOfChange()
    {
        var editor = new EditorViewModel();
        var (csv, parse, output) = BuildC01(editor, CreateCsv());
        var connection = editor.Connections[0];

        void Check(Action change)
        {
            editor.MarkClean();
            change();
            Assert.True(editor.IsDirty);
        }

        Check(() => editor.AddNode(NodeType.ToJson, new Point()));
        Check(() => csv.Location = new Point(1, 2));
        Check(() => csv.Title = "Renamed");
        Check(() => csv.Settings.Single(s => s.Key == "SkipLines").Value = "1");
        Check(() => output.Settings.Single().Value = nameof(ObjectKind.DataTable));
        Check(() => parse.ParseValueSettings = parse.ParseValueSettings! with { Trim = false });
        Check(csv.NotifyColumnSelectionChanged);
        Check(() => editor.Disconnect(connection));
        Check(() => Connect(editor, csv.Outputs[0], parse.Inputs[0]));
        Check(() => { editor.SelectedNodes.Add(output); editor.DeleteSelected(); });
        Check(editor.LoadSampleGraph);
    }

    [Fact]
    public void DirtyFlag_IsNotSet_ByValidationSelectionOrPanning()
    {
        var editor = new EditorViewModel();
        var (csv, _, _) = BuildC01(editor, CreateCsv());
        editor.MarkClean();

        editor.Validate();
        csv.IsSelected = true;
        csv.KnownColumns = ["a"];
        csv.IsRefreshing = true;
        editor.ViewportZoom = 0.5;
        editor.ViewportLocation = new Point(9, 9);

        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task SaveThenOpen_LeavesTheProjectClean_AndTheTitleFollows()
    {
        var (main, dialogs, recent) = NewMain();
        Assert.Equal("Untitled - Data Flow ETL Designer", main.WindowTitle);

        BuildC01(main.Editor, CreateCsv());
        Assert.Equal("Untitled* - Data Flow ETL Designer", main.WindowTitle);

        dialogs.PathToSave = InFolder("first run.etlproj");
        Assert.True(main.SaveProject());
        Assert.False(main.Editor.IsDirty);
        Assert.Equal("first run - Data Flow ETL Designer", main.WindowTitle);

        main.Editor.Nodes[0].Location = new Point(1, 1);
        Assert.Equal("first run* - Data Flow ETL Designer", main.WindowTitle);

        dialogs.Answer = UnsavedChangesChoice.DontSave;
        await main.OpenProjectAsync(dialogs.PathToSave);
        await Settled(main);

        Assert.False(main.Editor.IsDirty);
        Assert.Equal("first run - Data Flow ETL Designer", main.WindowTitle);
        Assert.Equal(new Point(40, 60), main.Editor.Nodes[0].Location);
        Assert.Equal([dialogs.PathToSave], recent.Entries);
    }

    [Fact]
    public void Save_UsesTheOpenFile_WithoutAsking_AndSaveAsAsks()
    {
        var (main, dialogs, _) = NewMain();
        dialogs.PathToSave = InFolder("a.etlproj");
        main.SaveProject();
        dialogs.PathToSave = null;
        main.Editor.AddNode(NodeType.ToJson, new Point());

        Assert.True(main.SaveProject());
        Assert.False(main.SaveProjectAs());
        Assert.True(File.Exists(InFolder("a.etlproj.bak")));
    }

    [Fact]
    public void FailedSave_KeepsTheOldFile_TheDirtyFlag_AndGivesTheReason()
    {
        var (main, dialogs, _) = NewMain();
        dialogs.PathToSave = InFolder("a.etlproj");
        main.SaveProject();
        var saved = File.ReadAllText(InFolder("a.etlproj"));
        main.Editor.AddNode(NodeType.ToJson, new Point());
        main.ProjectFileSteps = new ProjectFilesTests.FailingSteps(nameof(IProjectFileSteps.Replace));

        Assert.False(main.SaveProject());

        Assert.Equal(saved, File.ReadAllText(InFolder("a.etlproj")));
        Assert.True(main.Editor.IsDirty);
        Assert.Contains("The file is in use.", Assert.Single(dialogs.Errors));
    }

    public static TheoryData<string> Actions() => new() { "New", "Open", "Close" };

    private static Task<bool> Run(MainViewModel main, string action, string openPath) => action switch
    {
        "New" => Task.Run(() => { main.NewProject(); return main.Editor.Nodes.Count == 0; }),
        "Open" => main.OpenProjectAsync(openPath).ContinueWith(t => { t.GetAwaiter().GetResult(); return main.ProjectPath == openPath; }),
        _ => Task.FromResult(main.ConfirmClose())
    };

    private (MainViewModel Main, FakeDialogs Dialogs, string OpenPath) DirtyMain()
    {
        var (main, dialogs, _) = NewMain();
        var other = NewMain();
        other.Dialogs.PathToSave = InFolder("other.etlproj");
        other.Main.SaveProject();
        main.Editor.AddNode(NodeType.ToJson, new Point());
        return (main, dialogs, InFolder("other.etlproj"));
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task Cancel_StopsTheAction(string action)
    {
        var (main, dialogs, openPath) = DirtyMain();
        dialogs.Answer = UnsavedChangesChoice.Cancel;

        var done = await Run(main, action, openPath);

        Assert.False(done);
        Assert.Equal(["Untitled"], dialogs.Asked);
        Assert.Single(main.Editor.Nodes);
        Assert.True(main.Editor.IsDirty);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task DontSave_GoesOn_WithoutSaving(string action)
    {
        var (main, dialogs, openPath) = DirtyMain();
        dialogs.Answer = UnsavedChangesChoice.DontSave;
        dialogs.PathToSave = InFolder("should-not-exist.etlproj");

        Assert.True(await Run(main, action, openPath));

        Assert.False(File.Exists(dialogs.PathToSave));
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task Save_SavesFirst_ThenGoesOn(string action)
    {
        var (main, dialogs, openPath) = DirtyMain();
        dialogs.Answer = UnsavedChangesChoice.Save;
        dialogs.PathToSave = InFolder("saved.etlproj");

        Assert.True(await Run(main, action, openPath));

        Assert.Contains("ToJson", File.ReadAllText(dialogs.PathToSave));
    }

    [Fact]
    public void Save_WithoutAFileName_ThatTheUserCancels_StopsTheAction()
    {
        var (main, dialogs, _) = DirtyMain();
        dialogs.Answer = UnsavedChangesChoice.Save;
        dialogs.PathToSave = null;

        Assert.False(main.ConfirmClose());
    }

    [Fact]
    public async Task NothingToSave_AsksNothing()
    {
        var (main, dialogs, recent) = NewMain();

        Assert.True(main.ConfirmClose());
        main.NewProject();
        dialogs.PathToOpen = null;
        await main.OpenProjectAsync();

        Assert.Empty(dialogs.Asked);
        Assert.Empty(recent.Entries);
    }

    [Fact]
    public void CloseHandler_ShowsAndLogsAnException_AndKeepsTheWindowOpen()
    {
        var (main, dialogs, _) = DirtyMain();
        dialogs.AskFails = new InvalidOperationException("dialog broke " + Guid.NewGuid());

        Assert.False(main.ConfirmClose());

        Assert.Contains(dialogs.AskFails.Message, Assert.Single(dialogs.Errors));
        Assert.Contains(dialogs.AskFails.Message, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "unhandled-exception.log")));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("{ not json", "(line 1)")]
    [InlineData("{ \"formatVersion\": 99, \"nodes\": [] }", "newer version")]
    public async Task OpenOfABadFile_ChangesNothing_AndGivesTheReason(string content, string expected)
    {
        var (main, dialogs, recent) = NewMain();
        dialogs.PathToSave = InFolder("good.etlproj");
        main.Editor.AddNode(NodeType.ToJson, new Point(7, 7));
        main.SaveProject();
        File.WriteAllText(InFolder("bad.etlproj"), content);

        await main.OpenProjectAsync(InFolder("bad.etlproj"));

        Assert.Contains(expected, Assert.Single(dialogs.Errors));
        Assert.Equal(new Point(7, 7), main.Editor.Nodes.Single().Location);
        Assert.Equal(InFolder("good.etlproj"), main.ProjectPath);
        Assert.False(main.Editor.IsDirty);
        Assert.Equal([InFolder("good.etlproj")], recent.Entries);
    }

    [Fact]
    public async Task NewProject_ClearsTheCanvas_AndTheSampleButtonStillCountsAsAChange()
    {
        var (main, dialogs, _) = NewMain();
        dialogs.PathToSave = InFolder("a.etlproj");
        BuildC01(main.Editor, CreateCsv());
        main.SaveProject();

        main.NewProject();

        Assert.Empty(main.Editor.Nodes);
        Assert.Empty(main.Editor.Connections);
        Assert.Null(main.ProjectPath);
        Assert.Equal("Untitled - Data Flow ETL Designer", main.WindowTitle);

        main.Editor.LoadSampleGraph();
        Assert.Equal(5, main.Editor.Nodes.Count);
        Assert.True(main.Editor.IsDirty);
        await Settled(main);
    }

    [Fact]
    public void Recent_ShowsAMissingFile_AsNotOpenable_AndItCanBeRemoved()
    {
        var (main, _, recent) = NewMain();
        var there = CreateCsv("there.etlproj");
        recent.Add(InFolder("gone.etlproj"));
        recent.Add(there);

        main.IsRecentOpen = true;

        Assert.Equal(["there", "gone"], main.RecentProjects.Select(r => r.DisplayName));
        Assert.False(main.RecentProjects[0].IsMissing);
        Assert.True(main.RecentProjects[1].IsMissing);
        Assert.False(main.RecentProjects[1].OpenCommand.CanExecute(null));
        Assert.False(main.HasNoRecentProjects);

        main.RecentProjects[1].RemoveCommand.Execute(null);

        Assert.Equal([there], recent.Entries);
        Assert.Single(main.RecentProjects);
    }

    [Fact]
    public async Task BackgroundRefresh_WithAMissingFile_GivesTheNormalMessage_AndDoesNotThrow()
    {
        var (main, dialogs, _) = NewMain();
        dialogs.PathToSave = InFolder("a.etlproj");
        var csvPath = CreateCsv();
        BuildC01(main.Editor, csvPath);
        main.SaveProject();
        File.Delete(csvPath);

        await main.OpenProjectAsync(dialogs.PathToSave);
        await Settled(main);

        var csv = main.Editor.Nodes[0];
        Assert.Equal("Not configured", csv.ConfigurationStatus);
        Assert.False(csv.IsRefreshing);
        Assert.Contains(main.Editor.Issues, i => i.StartsWith("CsvFile:") && i.Contains("File was not found"));
        Assert.Empty(dialogs.Errors);
        Assert.False(main.Editor.IsDirty);
    }

    [Fact]
    public async Task BackgroundRefresh_ReadsTheColumns_AndConfiguresTheGraph()
    {
        var (main, dialogs, _) = NewMain();
        dialogs.PathToSave = InFolder("a.etlproj");
        BuildC01(main.Editor, CreateCsv());
        main.SaveProject();

        await main.OpenProjectAsync(dialogs.PathToSave);
        Assert.Contains(main.Editor.Nodes, n => n.IsRefreshing);
        await Settled(main);

        Assert.All(main.Editor.Nodes, n => Assert.Equal("Configured", n.ConfigurationStatus));
        Assert.All(main.Editor.Nodes, n => Assert.False(n.IsRefreshing));
        Assert.NotNull(main.Editor.Nodes[0].KnownColumns);
        Assert.Null(main.ExecuteBlockedReason);
        Assert.False(main.Editor.IsDirty);
    }

    [Fact]
    public async Task GenerateDll_IsDisabledAfterAnOpen_EvenForACompleteTestedGraph()
    {
        var (main, dialogs, _) = NewMain();
        dialogs.PathToSave = InFolder("a.etlproj");
        BuildC01(main.Editor, CreateCsv());
        main.SaveProject();
        await main.OpenProjectAsync(dialogs.PathToSave);
        await Settled(main);
        Assert.True(main.TryBuildExecuteDefinition(out var definition, out _));
        main.OnExecuteRunFinished(definition!, await TestTimeout.Run(() => PipelineRunner.RunAsync(definition!, null, null, CancellationToken.None)));
        Assert.Null(main.GenerateDllBlockedReason);

        await main.OpenProjectAsync(dialogs.PathToSave);
        await Settled(main);

        Assert.Null(main.ExecuteBlockedReason);
        Assert.Equal(MainViewModel.GenerateDllNotTestedHint, main.GenerateDllBlockedReason);
        Assert.False(main.GenerateDllCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnUnknownNode_IsIgnored_ByExecute()
    {
        var (main, dialogs, _) = NewMain();
        var editor = main.Editor;
        var (_, _, output) = BuildC01(editor, CreateCsv());
        File.WriteAllText(InFolder("a.etlproj"), WithUnknownNode(editor, output));
        dialogs.Answer = UnsavedChangesChoice.DontSave;

        await main.OpenProjectAsync(InFolder("a.etlproj"));
        await Settled(main);

        Assert.Equal(4, editor.Nodes.Count);
        Assert.Null(main.ExecuteBlockedReason);
    }

    [Fact]
    public void AProjectFile_HoldsNoLicenceText()
    {
        var editor = new EditorViewModel();
        BuildC01(editor, CreateCsv());

        Assert.DoesNotContain("licen", Text(editor), StringComparison.OrdinalIgnoreCase);
    }
}
