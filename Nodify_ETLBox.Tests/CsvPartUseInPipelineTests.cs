using System.IO;
using System.Windows;
using EtlDesigner.Engine.Intake;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.UI.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>
/// Task G6d: "Use in pipeline" from Intake Studio, the CSV part settings dialog, the column choice of a part and the
/// lineage events. The Studio is opened through MainViewModel.ShowIntakeStudio (the app's wiring) with fake dialogs.
/// No test here goes through the static NodeFactory handlers; the main view model's methods are called directly.
/// </summary>
public sealed class CsvPartUseInPipelineTests : IDisposable
{
    private const string KeyValueSentence = "Part 1 is not a CSV or TSV table, so a pipeline cannot read it; choose a table or change its format first.";

    private readonly ITestOutputHelper _output;
    private readonly string _folder = Directory.CreateTempSubdirectory("g6d_use_").FullName;

    public CsvPartUseInPipelineTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    internal sealed record Canvas(MainViewModel Main, ProjectEditorTests.FakeDialogs Dialogs, IntakeStudioOpenTests.MemoryLineage Lineage, NodeViewModel Intake)
    {
        public EditorViewModel Editor => Main.Editor;

        public IReadOnlyList<NodeViewModel> Parts => Editor.Nodes.Where(n => n.NodeType == NodeType.CsvPart).ToList();

        public IReadOnlyList<LineageEvent> Events(string name) => Lineage.Events.Where(e => e.Event == name).ToList();
    }

    /// <summary>A main view model with fake dialogs and a memory log, and one File intake node on <paramref name="path"/>.</summary>
    internal static Canvas NewCanvas(string path)
    {
        var dialogs = new ProjectEditorTests.FakeDialogs();
        var lineage = new IntakeStudioOpenTests.MemoryLineage();
        var main = new MainViewModel(dialogs, new ProjectEditorTests.MemoryRecent(), lineage);
        var intake = G6cCanvas.Add(main.Editor, NodeType.FileIntake, 10);
        intake.SetFileIntakeSettings(intake.GetFileIntakeSettings() with { Path = path });
        return new Canvas(main, dialogs, lineage, intake);
    }

    /// <summary>Opens Intake Studio of <paramref name="intake"/> the way the app does, scans and selects part <paramref name="partNumber"/>.</summary>
    internal static async Task<IntakeStudioViewModel> StudioAsync(Canvas canvas, NodeViewModel intake, int partNumber)
    {
        IntakeStudioViewModel? studio = null;
        canvas.Dialogs.OnIntakeStudio = vm => studio = vm;
        Assert.True(canvas.Main.ShowIntakeStudio(intake));
        Assert.NotNull(studio);
        await TestTimeout.Run(studio!.ScanAsync);
        studio.SelectedContainer = studio.Containers[partNumber - 1];
        return studio;
    }

    private string I10Copy(string name = "i10.txt", string kind = "example")
        => G6bParts.Write(_folder, name, G6bParts.Variant(kind));

    private static int TablePart(IntakeStudioViewModel studio, int nth = 0)
        => studio.Containers.Where(r => r.Info.Format is "csv" or "tsv").Select(r => r.Number).ElementAt(nth);

    // ----- Step 1: the button -----

    [Fact]
    public async Task Button_IsDisabled_WithTheSelectorsSentence_OnKeyValueAndTextParts()
    {
        var canvas = NewCanvas(I10Copy());
        var studio = await StudioAsync(canvas, canvas.Intake, 1);
        Assert.True(studio.CanUsePipeline);
        _output.WriteLine(string.Join(" | ", studio.Containers.Select(r => $"{r.Number} {r.Info.Format} {r.Lines}")));

        Assert.Equal("keyvalue", studio.SelectedContainer!.Info.Format);
        Assert.False(studio.UseInPipelineCommand.CanExecute(null));
        Assert.Equal(KeyValueSentence, studio.UseInPipelineToolTip);

        var text = studio.Containers.First(r => r.Info.Format == "text");
        studio.SelectedContainer = text;
        CsvPartSelector.FromSession(studio.Session!, text.Number - 1, out var expected);
        Assert.Equal($"Part {text.Number} is not a CSV or TSV table, so a pipeline cannot read it; choose a table or change its format first.", expected);
        Assert.False(studio.UseInPipelineCommand.CanExecute(null));
        Assert.Equal(expected, studio.UseInPipelineToolTip);

        var table = TablePart(studio);
        studio.SelectedContainer = studio.Containers[table - 1];
        Assert.True(studio.UseInPipelineCommand.CanExecute(null));
        Assert.Equal($"Make part {table} the CSV part of this pipeline.", studio.UseInPipelineToolTip);
        Assert.Empty(canvas.Parts);
    }

    [Fact]
    public async Task Button_IsDisabled_UntilTheScannedSettingsAreSaved_AndWithoutAHostNode()
    {
        var canvas = NewCanvas(I10Copy());
        var studio = await StudioAsync(canvas, canvas.Intake, 1);
        studio.Encoding = IntakeEncodings.Utf8;
        await TestTimeout.Run(studio.ScanAsync);
        studio.SelectedContainer = studio.Containers[TablePart(studio) - 1];
        Assert.Equal(IntakeStudioViewModel.SettingsNotSavedText, studio.UseInPipelineToolTip);
        Assert.False(studio.UseInPipelineCommand.CanExecute(null));

        studio.Save();
        Assert.True(studio.UseInPipelineCommand.CanExecute(null));

        var alone = new IntakeStudioViewModel(new FileIntakeNodeSettings { Path = canvas.Intake.GetFileIntakeSettings().Path });
        Assert.False(alone.CanUsePipeline);
        Assert.Equal(IntakeStudioViewModel.NoPipelineText, alone.UseInPipelineToolTip);
    }

    [Fact]
    public async Task Use_CreatesTheCsvPartNode_NextToTheFileIntake_Connected_StartsTheRead_AndLogsPartChosen()
    {
        var path = I10Copy();
        var canvas = NewCanvas(path);
        var studio = await StudioAsync(canvas, canvas.Intake, 1);
        var table = TablePart(studio);
        studio.SelectedContainer = studio.Containers[table - 1];
        var expected = CsvPartSelector.FromSession(studio.Session!, table - 1, out _)!;

        studio.UseInPipelineCommand.Execute(null);

        Assert.Equal($"Part {table} is now the CSV part of this pipeline.", studio.EditStatus);
        var part = Assert.Single(canvas.Parts);
        Assert.Same(canvas.Intake, canvas.Editor.FileIntakeOf(part));
        Assert.Equal(canvas.Intake.Location + new Vector(200, 0), part.Location);
        Assert.True(MainViewModel.SamePart(part.CsvPartSelectorJson!, expected));
        Assert.Empty(canvas.Dialogs.YesNoQuestions);
        Assert.Equal(2, canvas.Editor.Nodes.Count); // File intake and CSV part only: no ParseValue or DotNetObject added.

        Assert.NotNull(canvas.Main.CsvPartRefresh);
        await TestTimeout.Run(() => canvas.Main.CsvPartRefresh!);
        // Only the CSV part was added, so its first line asks for a ParseValue; the columns are read.
        Assert.Equal(EditorViewModel.CsvPartNotToParseValue, part.CsvPartStatus);
        Assert.NotNull(part.KnownColumns);

        var chosen = Assert.Single(canvas.Events("pipeline.part-chosen"));
        Assert.Equal("i10.txt", chosen.File);
        Assert.Equal(studio.Session!.Sha256, chosen.Sha256);
        Assert.Equal(studio.Session.IntakeId, chosen.IntakeId);
        Assert.Equal(new[] { "templateId", "templateName", "partNumber" }, chosen.Details.Keys);
        Assert.Equal(table, chosen.Details["partNumber"]);
        Assert.Equal("i10", chosen.Details["templateName"]);
        Assert.True(Guid.TryParse((string)chosen.Details["templateId"]!, out _));
        AssertNoPathOrContent(chosen, path);
    }

    [Fact]
    public async Task Use_ReusesAnUnconnectedCsvPartNode_InsteadOfAddingASecond()
    {
        var canvas = NewCanvas(I10Copy());
        var dragged = G6cCanvas.Add(canvas.Editor, NodeType.CsvPart, 400);
        var studio = await StudioAsync(canvas, canvas.Intake, 1);
        studio.SelectedContainer = studio.Containers[TablePart(studio) - 1];
        studio.UseInPipelineCommand.Execute(null);

        Assert.Same(dragged, Assert.Single(canvas.Parts));
        Assert.Same(canvas.Intake, canvas.Editor.FileIntakeOf(dragged));
        Assert.NotNull(dragged.CsvPartSelectorJson);
        await TestTimeout.Run(() => canvas.Main.CsvPartRefresh!);
    }

    [Fact]
    public async Task Use_TheSamePartAgain_ChangesNothing_AsksNothing_AndLogsNothing()
    {
        var canvas = NewCanvas(I10Copy());
        var studio = await StudioAsync(canvas, canvas.Intake, 1);
        studio.SelectedContainer = studio.Containers[TablePart(studio) - 1];
        studio.UseInPipelineCommand.Execute(null);
        var part = Assert.Single(canvas.Parts);
        var json = part.CsvPartSelectorJson;
        var dirty = canvas.Editor.IsDirty;
        await TestTimeout.Run(() => canvas.Main.CsvPartRefresh!);

        // A second build of the same part has a new template id and time; it is still the same part.
        studio.UseInPipelineCommand.Execute(null);

        Assert.Equal(MainViewModel.PartAlreadyUsedText, studio.EditStatus);
        Assert.Same(part, Assert.Single(canvas.Parts));
        Assert.Equal(json, part.CsvPartSelectorJson);
        Assert.Equal(dirty, canvas.Editor.IsDirty);
        Assert.Empty(canvas.Dialogs.YesNoQuestions);
        Assert.Single(canvas.Events("pipeline.part-chosen"));
        Assert.Equal(EditorViewModel.CsvPartNotToParseValue, part.CsvPartStatus);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Use_AnotherPart_AsksToReplace_YesReplacesTheSelector_NoChangesNothing(bool yes)
    {
        var canvas = NewCanvas(I10Copy(kind: "three copies"));
        var studio = await StudioAsync(canvas, canvas.Intake, 1);
        var first = TablePart(studio, 0);
        var second = TablePart(studio, 1);
        studio.SelectedContainer = studio.Containers[first - 1];
        studio.UseInPipelineCommand.Execute(null);
        var part = Assert.Single(canvas.Parts);
        var oldJson = part.CsvPartSelectorJson;
        await TestTimeout.Run(() => canvas.Main.CsvPartRefresh!);

        canvas.Dialogs.YesNoAnswer = yes;
        studio.SelectedContainer = studio.Containers[second - 1];
        var expected = CsvPartSelector.FromSession(studio.Session!, second - 1, out _)!;
        studio.UseInPipelineCommand.Execute(null);

        Assert.Equal(new[] { $"Replace the part chosen in '{part.Title}' with this one?" }, canvas.Dialogs.YesNoQuestions);
        Assert.Same(part, Assert.Single(canvas.Parts));
        if (yes)
        {
            Assert.Equal($"Part {second} is now the CSV part of this pipeline.", studio.EditStatus);
            Assert.True(MainViewModel.SamePart(part.CsvPartSelectorJson!, expected));
            Assert.Equal(2, canvas.Events("pipeline.part-chosen").Count);
            Assert.Equal(second, canvas.Events("pipeline.part-chosen")[1].Details["partNumber"]);
            await TestTimeout.Run(() => canvas.Main.CsvPartRefresh!);
            Assert.Equal(second - 1, part.CsvPartSelector!.PartIndex);
        }
        else
        {
            Assert.Equal(MainViewModel.PartNotChangedText, studio.EditStatus);
            Assert.Equal(oldJson, part.CsvPartSelectorJson);
            Assert.Single(canvas.Events("pipeline.part-chosen"));
        }
    }

    [Fact]
    public async Task Use_FromASecondFileIntake_WhenTheFirstFeedsACsvPart_SaysSo_AndChangesNothing()
    {
        var canvas = NewCanvas(I10Copy());
        var studio = await StudioAsync(canvas, canvas.Intake, 1);
        studio.SelectedContainer = studio.Containers[TablePart(studio) - 1];
        studio.UseInPipelineCommand.Execute(null);
        var part = Assert.Single(canvas.Parts);
        var json = part.CsvPartSelectorJson;
        await TestTimeout.Run(() => canvas.Main.CsvPartRefresh!);

        var second = G6cCanvas.Add(canvas.Editor, NodeType.FileIntake, 10);
        second.SetFileIntakeSettings(second.GetFileIntakeSettings() with { Path = I10Copy("other.txt") });
        var other = await StudioAsync(canvas, second, 1);
        other.SelectedContainer = other.Containers[TablePart(other) - 1];
        other.UseInPipelineCommand.Execute(null);

        Assert.Equal(MainViewModel.OtherIntakeText(canvas.Intake.Title, part.Title), other.EditStatus);
        Assert.Same(part, Assert.Single(canvas.Parts));
        Assert.Equal(json, part.CsvPartSelectorJson);
        Assert.Same(canvas.Intake, canvas.Editor.FileIntakeOf(part));
        Assert.Empty(canvas.Dialogs.YesNoQuestions);
        Assert.Single(canvas.Events("pipeline.part-chosen"));
    }

    [Fact]
    public async Task Use_KeepsTheOriginalFileUnchanged()
    {
        var path = I10Copy();
        var before = (G6bParts.Sha(path), File.GetLastWriteTimeUtc(path));
        var canvas = NewCanvas(path);
        var studio = await StudioAsync(canvas, canvas.Intake, 1);
        studio.SelectedContainer = studio.Containers[TablePart(studio) - 1];
        studio.UseInPipelineCommand.Execute(null);
        await TestTimeout.Run(() => canvas.Main.CsvPartRefresh!);
        Assert.Equal(before, (G6bParts.Sha(path), File.GetLastWriteTimeUtc(path)));
    }

    // ----- The node after "Use in pipeline" runs like the standalone CSV of the same part -----

    /// <summary>Uses the table part of <paramref name="path"/>, adds ParseValue and DotNetObject as a user would, and waits until it is ready.</summary>
    internal static async Task<(Canvas Canvas, NodeViewModel Part)> UsedAsync(string path)
    {
        var canvas = NewCanvas(path);
        var studio = await StudioAsync(canvas, canvas.Intake, 1);
        studio.SelectedContainer = studio.Containers[TablePart(studio) - 1];
        studio.UseInPipelineCommand.Execute(null);
        var part = Assert.Single(canvas.Parts);
        var parse = G6cCanvas.Add(canvas.Editor, NodeType.ParseValue, 420);
        var output = G6cCanvas.Add(canvas.Editor, NodeType.DotNetObject, 620);
        parse.ParseValueSettings = G6bParts.I10ParseValue();
        G6cCanvas.Connect(canvas.Editor, part.Outputs[0], parse.Inputs[0]);
        G6cCanvas.Connect(canvas.Editor, parse.Outputs[0], output.Inputs[0]);
        canvas.Editor.Validate();
        await TestTimeout.Run(() => canvas.Main.CsvPartRefresh!);
        canvas.Editor.Validate();
        Assert.Equal(EditorViewModel.CsvPartReady, part.CsvPartStatus);
        return (canvas, part);
    }

    private static async Task<(PipelineDefinition Definition, PipelineResult Run)> ExecuteAsync(Canvas canvas)
    {
        Assert.True(canvas.Main.TryBuildExecuteDefinition(out var definition, out var reason), $"{reason} Issues: {string.Join(" / ", canvas.Editor.Issues)}");
        var execute = new ExecuteViewModel(definition!, runFinished: r => canvas.Main.OnExecuteRunFinished(definition!, r));
        await TestTimeout.Run(execute.RunAsync);
        return (definition!, execute.Result!);
    }

    [Theory]
    [InlineData("example")]
    [InlineData("many rows")]
    public async Task UsedPart_Executes_AndEqualsTheStandaloneCsvOfThePart_AndLogsPipelineExecuted(string kind)
    {
        var path = I10Copy(kind: kind);
        var (canvas, part) = await UsedAsync(path);
        var (_, run) = await ExecuteAsync(canvas);
        Assert.Null(run.Error);

        var standalone = G6bParts.Write(_folder, "table.csv", G6bParts.TableOf(File.ReadAllText(path)));
        var whole = await G6bParts.Run(G6bParts.StandaloneDefinition(standalone), null);
        Assert.Equal(whole.Columns, run.Columns);
        Assert.Equal(G6bParts.RowTexts(whole.Columns, whole.Rows), G6bParts.RowTexts(run.Columns, run.Rows));
        Assert.Equal(whole.Counts, run.Counts);
        _output.WriteLine($"{kind}: {run.Counts}");

        var executed = Assert.Single(canvas.Events("pipeline.executed"));
        Assert.Equal("i10.txt", executed.File);
        Assert.Equal(run.CsvPart!.Sha256, executed.Sha256);
        Assert.True(Guid.TryParse(executed.IntakeId, out _));
        Assert.Equal(new[] { "templateId", "templateName", "match", "similarity", "partNumber", "firstLine", "lastLine",
            "read", "parsed", "rejected", "flagged", "out", "elapsedMs", "cancelled" }, executed.Details.Keys);
        Assert.Equal(part.CsvPartSelector!.Template.Id.ToString("D"), executed.Details["templateId"]);
        Assert.Equal("i10", executed.Details["templateName"]);
        Assert.Equal("Exact", executed.Details["match"]);
        Assert.Equal(run.CsvPart.PartNumber, executed.Details["partNumber"]);
        Assert.Equal(run.CsvPart.FirstLine, executed.Details["firstLine"]);
        Assert.Equal(run.CsvPart.LastLine, executed.Details["lastLine"]);
        Assert.Equal((run.Counts.Read, run.Counts.Parsed, run.Counts.Rejected, run.Counts.Flagged, run.Counts.Out),
            ((int)executed.Details["read"]!, (int)executed.Details["parsed"]!, (int)executed.Details["rejected"]!, (int)executed.Details["flagged"]!, (int)executed.Details["out"]!));
        Assert.Equal(run.ElapsedMs, executed.Details["elapsedMs"]);
        Assert.Equal(false, executed.Details["cancelled"]);
        AssertNoPathOrContent(executed, path);
    }

    [Fact]
    public async Task PartNotFound_LogsPipelineExecuted_WithTheFailureKind_AndNoCounts()
    {
        var path = I10Copy();
        var (canvas, _) = await UsedAsync(path);
        Assert.True(canvas.Main.TryBuildExecuteDefinition(out var definition, out var reason), reason);

        // The table loses a column after the pipeline was set up: the locator stops with its column-count sentence.
        File.WriteAllText(path, G6bParts.Variant("example").Split('\n').Select((l, i) => i >= 21 && i < 27 ? l[..l.LastIndexOf(',')] : l).Aggregate((a, b) => a + "\n" + b)); // Same lines as G6c's error canvas.
        var execute = new ExecuteViewModel(definition!, runFinished: r => canvas.Main.OnExecuteRunFinished(definition!, r));
        await TestTimeout.Run(execute.RunAsync);
        var run = execute.Result!;
        Assert.NotNull(run.Error);
        Assert.Equal(nameof(CsvPartFailure.ColumnCountDiffers), run.CsvPart!.Failure);

        var executed = Assert.Single(canvas.Events("pipeline.executed"));
        Assert.Equal(nameof(CsvPartFailure.ColumnCountDiffers), executed.Details["failure"]);
        foreach (var count in new[] { "read", "parsed", "rejected", "flagged", "out" })
        {
            Assert.False(executed.Details.ContainsKey(count), count);
        }

        Assert.Equal(false, executed.Details["cancelled"]);
        Assert.Empty(canvas.Events("pipeline.part-chosen").Skip(1));
        AssertNoPathOrContent(executed, path);
    }

    [Fact]
    public async Task CsvFilePipeline_WritesNoPipelineExecutedEvent()
    {
        var csv = G6bParts.Write(_folder, "table.csv", G6bParts.TableOf(G6bParts.Variant("example")));
        var lineage = new IntakeStudioOpenTests.MemoryLineage();
        var main = new MainViewModel(new ProjectEditorTests.FakeDialogs(), new ProjectEditorTests.MemoryRecent(), lineage);
        var definition = G6bParts.StandaloneDefinition(csv);
        main.OnExecuteRunFinished(definition, await G6bParts.Run(definition, null));
        Assert.Empty(lineage.Events);
    }

    /// <summary>No folder, no line of the file and no user or machine name in any field of the event.</summary>
    private static void AssertNoPathOrContent(LineageEvent entry, string path)
    {
        var texts = new[] { entry.File, entry.Sha256, entry.IntakeId }.Concat(entry.Details.Values.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)))
            .Where(t => t is not null).Select(t => t!).ToList();
        var folder = Path.GetDirectoryName(path)!;
        var lines = File.ReadAllText(G6bParts.I10Path).Split('\n').Where(l => l.Length > 6).ToList();
        foreach (var text in texts)
        {
            Assert.DoesNotContain(folder, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('\\', text);
            Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(lines, l => text.Contains(l, StringComparison.Ordinal));
        }
    }

    // ----- Step 2: settings dialog and columns -----

    [Fact]
    public async Task SettingsDialog_ShowsThePart_ReadOnly_AndOpenInIntakeStudio_OpensTheConnectedFileIntake()
    {
        var path = I10Copy();
        var (canvas, part) = await UsedAsync(path);
        var selector = part.CsvPartSelector!;
        var location = canvas.Editor.CurrentCsvPartRead(part)!.Result.Location;

        CsvPartSettingsViewModel? dialog = null;
        canvas.Dialogs.OnCsvPartSettings = vm =>
        {
            dialog = vm;
            vm.OpenIntakeStudioCommand.Execute(null);
        };
        IntakeStudioViewModel? studio = null;
        canvas.Dialogs.OnIntakeStudio = vm => studio = vm;
        Assert.True(canvas.Main.ShowCsvPartSettings(part));

        Assert.NotNull(dialog);
        Assert.Equal("i10.txt", dialog!.ExampleFile);
        Assert.Equal("i10", dialog.TemplateName);
        Assert.Equal($"Part {location.PartNumber}, lines {location.FirstLine} to {location.LastLine}", dialog.PartText);
        Assert.Equal("Comma", dialog.Delimiter);
        Assert.Equal("Yes", dialog.Header);
        Assert.Equal("0", dialog.SkipLines);
        Assert.Equal(selector.Expected.ColumnCount.ToString(System.Globalization.CultureInfo.InvariantCulture), dialog.Columns);
        Assert.Equal(string.Join(", ", selector.Expected.ColumnNames), dialog.ColumnNames);
        Assert.Equal(selector.ExampleSha256[..12], dialog.ExampleSha256);
        Assert.Equal(12, dialog.ExampleSha256.Length);
        Assert.Equal("All columns", dialog.KeptText);
        Assert.Equal(EditorViewModel.CsvPartReady, dialog.Status);
        Assert.True(dialog.ChooseColumnsCommand.CanExecute(null));

        Assert.Equal(CsvPartSettingsNext.OpenIntakeStudio, dialog.Next);
        Assert.NotNull(studio);
        Assert.Equal(path, studio!.FilePath);
        Assert.True(studio.CanUsePipeline);
    }

    [Fact]
    public void SettingsDialog_WithoutFileIntakeOrPart_DisablesBothButtons_WithTheReason()
    {
        var editor = new EditorViewModel();
        var part = G6cCanvas.Add(editor, NodeType.CsvPart, 0);
        var dialog = new CsvPartSettingsViewModel(part, null, hasFileIntake: false, read: null);
        Assert.False(dialog.OpenIntakeStudioCommand.CanExecute(null));
        Assert.Equal(CsvPartSettingsViewModel.NoFileIntakeText, dialog.OpenIntakeStudioToolTip);
        Assert.False(dialog.ChooseColumnsCommand.CanExecute(null));
        Assert.Equal(CsvPartSettingsViewModel.PartNotReadText, dialog.ChooseColumnsToolTip);
        Assert.Equal(CsvPartSettingsViewModel.NotSet, dialog.TemplateName);
        Assert.NotNull(part.DoubleClickCommand);
    }

    [Fact]
    public async Task ChooseColumns_ReadsThePartOnly_WithAFixedDialect_AndExecuteHonoursTheChoice()
    {
        var path = I10Copy();
        var (canvas, part) = await UsedAsync(path);
        EtlDesigner.Engine.Csv.CsvPartSource? opened = null;
        canvas.Dialogs.OnCsvPartSettings = vm => vm.ChooseColumnsCommand.Execute(null);
        canvas.Dialogs.OnCsvPartColumns = (_, source) => opened = source;
        Assert.True(canvas.Main.ShowCsvPartSettings(part));
        Assert.Equal(canvas.Editor.CurrentCsvPartRead(part)!.Result.Location.Part, opened);

        // What DialogService.ShowCsvPartColumns builds, without the window.
        var reader = new EtlDesigner.Engine.Csv.CsvPreviewReader();
        var settings = new EtlDesigner.Engine.Csv.CsvFileSettings(opened!.Path, opened.Delimiter, opened.HasHeader, opened.SkipLines, opened.EncodingName, 1000);
        var studio = new DataStudioViewModel(part, settings, read: (s, t) => reader.ReadAsync(opened, s.PreviewRows, t), dialectFixed: true);
        await TestTimeout.Run(() => studio.ReloadNowAsync());
        Assert.False(studio.IsDialectEditable);
        Assert.Equal(DataStudioViewModel.DialectFixedText, studio.DialectToolTip);
        Assert.Equal(part.KnownColumns, studio.Columns.Select(c => c.Name));
        Assert.Equal(5, studio.LoadedRowCount);

        studio.IsIgnoreMode = true;
        studio.Columns.First(c => c.Name == "result").IsSelected = true;
        studio.SaveCommand.Execute(null);
        canvas.Editor.Validate();
        Assert.Equal(EditorViewModel.CsvPartReady, part.CsvPartStatus);

        // The i10 slot uses 'result', so ParseValue refuses until the slot no longer needs it.
        Assert.Contains("ParseValue: Slot S1 uses the column 'result', which is not among the kept columns. Edit the slot, or keep the column in Data Studio.", canvas.Editor.Issues);
        var parse = canvas.Editor.Nodes.Single(n => n.NodeType == NodeType.ParseValue);
        parse.ParseValueSettings = G6bParts.I10ParseValue() with { Slots = [new EtlDesigner.Engine.Parsing.Slot("S1", ["weight_g", "volume_ml", "chloride_pct"])] };
        canvas.Editor.Validate();

        var (definition, run) = await ExecuteAsync(canvas);
        Assert.Contains("result", definition.Columns.Names);
        Assert.Null(run.Error);
        Assert.DoesNotContain("result", run.Columns);
        Assert.Contains("sample_id", run.Columns);
        Assert.Equal("4 of 5 columns", new CsvPartSettingsViewModel(part, path, true, canvas.Editor.CurrentCsvPartRead(part)).KeptText);
    }
}
