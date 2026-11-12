using System.IO;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Generation;
using EtlDesigner.Engine.Intake;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.Engine.Project;
using EtlDesigner.UI.Services;
using EtlDesigner.UI.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>
/// Task G7a: the guided builder's view model. Every file is a temp copy; the templates and the lineage are a temp
/// folder and memory. No test goes through the static NodeFactory handlers.
/// </summary>
public sealed class GuidedBuilderTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _folder = Directory.CreateTempSubdirectory("g7a_guided_").FullName;

    public GuidedBuilderTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    internal sealed record Builder(GuidedBuilderViewModel Vm, MainViewModel Main, ProjectEditorTests.FakeDialogs Dialogs,
        IntakeStudioOpenTests.MemoryLineage Lineage, FileTemplateStore Store, List<string> Questions, List<CsvPipelineDllRequest> Generated)
    {
        public bool Answer { get; set; }
    }

    internal static Builder New(string folder, FileTemplateStore? store = null, bool answer = false)
    {
        var dialogs = new ProjectEditorTests.FakeDialogs();
        var lineage = new IntakeStudioOpenTests.MemoryLineage();
        store ??= Store(folder);
        var main = new MainViewModel(dialogs, new ProjectEditorTests.MemoryRecent(), lineage, store);
        var questions = new List<string>();
        var generated = new List<CsvPipelineDllRequest>();
        Builder? builder = null;
        var vm = new GuidedBuilderViewModel(main, lineage, store, new TemplateSettings(),
            pickFile: null, pickFolder: null,
            confirm: q => { questions.Add(q); return builder!.Answer; },
            showStudio: null,
            generate: request =>
            {
                generated.Add(request);
                Directory.CreateDirectory(request.OutputFolder);
                var dll = Path.Combine(request.OutputFolder, request.AssemblyName + ".dll");
                var readme = Path.Combine(request.OutputFolder, CsvPipelineDllGenerator.ReadmeFileName);
                File.WriteAllText(dll, "fake");
                File.WriteAllText(readme, "fake");
                return new CsvPipelineDllResult(true, dll, readme, [], []);
            });
        builder = new Builder(vm, main, dialogs, lineage, store, questions, generated) { Answer = answer };
        return builder;
    }

    internal static FileTemplateStore Store(string folder)
        => new(new TemplateSettings { Folder = Path.Combine(folder, "templates") }, Path.Combine(folder, "fallback"));

    /// <summary>Chooses the file and waits for the scan and the table checks.</summary>
    internal static async Task ScanAsync(GuidedBuilderViewModel vm, string path)
    {
        vm.FilePath = path;
        await TestTimeout.Run(() => vm.ScanTask!, TimeSpan.FromSeconds(30));
        if (vm.TablesTask is { } tables)
        {
            await TestTimeout.Run(() => tables, TimeSpan.FromSeconds(30));
        }
    }

    internal static async Task TryAsync(GuidedBuilderViewModel vm)
    {
        vm.TryCommand.Execute(null);
        await TestTimeout.Run(() => vm.TryTask!, TimeSpan.FromSeconds(30));
    }

    /// <summary>File -> Table (the chosen one) -> Columns -> Try it, with a successful try.</summary>
    internal static async Task<Builder> ToTryAsync(string folder, string path, int? table = null, Action<GuidedBuilderViewModel>? columns = null, FileTemplateStore? store = null)
    {
        var b = New(folder, store);
        await ScanAsync(b.Vm, path);
        b.Vm.NextCommand.Execute(null);
        if (table is { } number)
        {
            b.Vm.SelectedTable = b.Vm.Tables.Single(t => t.Number == number);
        }

        Assert.Null(b.Vm.NextProblem);
        b.Vm.NextCommand.Execute(null);
        columns?.Invoke(b.Vm);
        Assert.Null(b.Vm.NextProblem);
        b.Vm.NextCommand.Execute(null);
        Assert.Equal(GuidedStep.Try, b.Vm.Step);
        await TryAsync(b.Vm);
        Assert.True(b.Vm.TrySucceeded, $"{b.Vm.TryMessage} {b.Vm.TryDetails} Issues: {string.Join(" / ", b.Main.Editor.Issues)}");
        return b;
    }

    private string Copy(string name, string text) => G6bParts.Write(_folder, name, text);

    private string I10(string kind = "example", string name = "i10.txt") => Copy(name, G6bParts.Variant(kind));

    private string Real(string name)
    {
        var path = Path.Combine(_folder, name);
        File.Copy(Path.Combine(IntakeTestData.Folder, "real", name), path, overwrite: true);
        return path;
    }

    // ----- step transitions -----

    [Fact]
    public async Task Steps_NextIsDisabledUntilEachStepIsDone_AndBackKeepsTheChoices()
    {
        var b = New(_folder);
        var vm = b.Vm;
        Assert.Equal(GuidedStep.File, vm.Step);
        Assert.False(vm.NextCommand.CanExecute(null));
        Assert.Equal("Choose a file first.", vm.NextProblem);
        Assert.False(vm.BackCommand.CanExecute(null));

        await ScanAsync(vm, I10());
        Assert.True(vm.NextCommand.CanExecute(null));
        vm.NextCommand.Execute(null);
        Assert.Equal(GuidedStep.Table, vm.Step);
        Assert.Equal(GuidedBuilderViewModel.TableQuestion, vm.TableMessage);
        Assert.Equal(2, vm.SelectedTable!.Number);
        Assert.NotNull(vm.TablePreview);
        Assert.Equal(5, vm.TablePreview!.Count);

        vm.NextCommand.Execute(null);
        Assert.Equal(GuidedStep.Columns, vm.Step);
        Assert.Equal(["sample_id", "weight_g", "volume_ml", "chloride_pct", "result"], vm.Columns.Select(c => c.Name));
        Assert.All(vm.Columns, c => Assert.True(c.IsKept));
        foreach (var column in vm.Columns)
        {
            column.IsKept = false;
        }

        Assert.Equal("Keep at least one column.", vm.NextProblem);
        vm.Columns[0].IsKept = true;
        vm.Columns[4].IsKept = true;
        vm.SplitEnabled = true;
        vm.SplitColumn = "sample_id";
        vm.SelectedSeparator = "pipe";
        Assert.Equal("Nothing to split: '|' was not found in column sample_id.", vm.NextProblem);
        vm.SelectedSeparator = "other";
        vm.OtherSeparator = "-";
        Assert.Equal("Part1, Part2", vm.PartNamesText);
        Assert.Null(vm.NextProblem);

        vm.NextCommand.Execute(null);
        Assert.Equal(GuidedStep.Try, vm.Step);
        Assert.Equal("Press \"Try on the sample file\" first; it must work with the settings as they are now.", vm.NextProblem);

        // Back keeps every choice.
        vm.BackCommand.Execute(null);
        vm.BackCommand.Execute(null);
        Assert.Equal(GuidedStep.Table, vm.Step);
        Assert.Equal(2, vm.SelectedTable!.Number);
        vm.NextCommand.Execute(null);
        Assert.Equal(["sample_id", "result"], vm.KeptColumns);
        Assert.True(vm.SplitEnabled);
        Assert.Equal("-", vm.Separator);
        Assert.Equal(["Part1", "Part2"], vm.PartNames);
    }

    [Fact]
    public async Task AFileWithoutATable_SaysSo_AndNextStaysDisabled()
    {
        var b = New(_folder);
        await ScanAsync(b.Vm, Copy("prose.txt", "Just some words.\nNothing else here.\n"));
        Assert.Empty(b.Vm.Tables);
        Assert.Equal(GuidedBuilderViewModel.NoTableText, b.Vm.NextProblem);
        Assert.Equal(GuidedBuilderViewModel.NoTableText, b.Vm.FileMessage);
    }

    [Fact]
    public async Task AMissingFile_GivesAPlainSentence_WithTheTechnicalTextAsDetails()
    {
        var b = New(_folder);
        await ScanAsync(b.Vm, Path.Combine(_folder, "nothing_here.csv"));
        Assert.Null(b.Vm.Session);
        Assert.NotNull(b.Vm.FileMessage);
        Assert.DoesNotContain("Exception", b.Vm.FileMessage);
        _output.WriteLine($"{b.Vm.FileMessage} | {b.Vm.FileDetails}");
    }

    // ----- the neighbour table on r03 -----

    [Fact]
    public async Task R03_ATableMergedWithTheNextTwo_CannotBeRead_AndTheCleanTableOnLines9To18_IsSuggested()
    {
        var b = New(_folder);
        var vm = b.Vm;
        await ScanAsync(vm, Real("r03_perkinelmer_envision.csv"));
        var results = vm.Tables.Single(t => t.Number == 3);
        Assert.True(results.ReadsCleanly, results.Problem);
        // The detector's part ends on the blank line after the table (19); the truth file says 18.
        var cleanLast = vm.Session!.Containers[2].ContentLastLine!.Value;
        Assert.Equal($"lines 9 to {cleanLast}", results.Lines);

        // The layout a user (or an old saved layout) made: lines 9 to 26 in one part.
        Assert.True(vm.Session!.Merge(2).Ok);
        Assert.True(vm.Session.Merge(2).Ok);
        Assert.Equal(9L, vm.Session.Containers[2].ContentFirstLine!.Value);
        Assert.True(vm.Session.Containers[2].ContentLastLine >= 26);
        await TestTimeout.Run(() => vm.RefreshTablesAsync(3), TimeSpan.FromSeconds(30));
        vm.NextCommand.Execute(null);
        vm.SelectedTable = vm.Tables.Single(t => t.Number == 3);
        var merged = vm.SelectedTable!;
        _output.WriteLine($"{merged.Summary}: {merged.Problem} | {merged.Suggestion} | {merged.Details}");
        Assert.False(merged.ReadsCleanly);
        Assert.Equal(cleanLast + 1, merged.SplitBefore);
        Assert.StartsWith("This table includes lines from another table. Pick ", merged.Suggestion);
        Assert.Contains($"lines 9 to {cleanLast}", merged.Suggestion);
        Assert.Equal(merged.Suggestion, vm.TableMessage);
        Assert.False(vm.NextCommand.CanExecute(null));
        Assert.True(vm.HasSuggestion);

        vm.UseSuggestionCommand.Execute(null);
        await TestTimeout.Run(() => vm.TablesTask!, TimeSpan.FromSeconds(30));
        // A split on a blank line moves to the next line (engine rule), so the blank line stays with the table.
        Assert.StartsWith("lines 9 to ", vm.SelectedTable!.Lines);
        Assert.True(vm.Session.Containers[2].ContentLastLine <= cleanLast + 1);
        Assert.True(vm.SelectedTable.ReadsCleanly);
        Assert.True(vm.NextCommand.CanExecute(null));
    }

    // ----- no splitting, one separator, try -----

    [Fact]
    public async Task NoSplitting_Try_PutsFileIntakeCsvPartDotNetObjectOnTheCanvas_AndGivesTheTableRows()
    {
        var path = I10("more rows");
        var b = await ToTryAsync(_folder, path);
        var vm = b.Vm;
        Assert.Equal("Rows read 8 · Rows kept 8 · Rows left out 0", vm.TryCountsText);
        Assert.Equal(8, vm.TryRows!.Count);
        Assert.Empty(vm.TryReasons);
        Assert.Equal("The try worked.", vm.TryMessage);
        Assert.Null(vm.TriedDefinition!.Guided!.Split);

        var chain = b.Main.FindGuidedChain();
        Assert.NotNull(chain);
        Assert.Equal(path, chain!.Value.Intake.GetFileIntakeSettings().Path);
        Assert.Null(b.Main.ExecuteBlockedReason);
        Assert.Null(b.Main.GenerateDllBlockedReason);

        var table = G6bParts.TableOf(File.ReadAllText(path)).Split('\n');
        Assert.Equal(table[0].Split(','), vm.TriedResult!.Columns);
        Assert.Equal(table.Skip(1).Select(l => l.Replace(',', '|')), G6bParts.RowTexts(vm.TriedResult.Columns, vm.TriedResult.Rows));
        Assert.Contains(b.Lineage.Events, e => e.Event == "pipeline.executed");
    }

    [Fact]
    public async Task OneSeparatorSplit_Try_SplitsTheColumn_AndAChangeAfterwards_NeedsANewTry()
    {
        var b = await ToTryAsync(_folder, I10(), columns: vm =>
        {
            vm.Columns.Single(c => c.Name == "volume_ml").IsKept = false;
            vm.SplitEnabled = true;
            vm.SplitColumn = "sample_id";
            vm.SelectedSeparator = "other";
            vm.OtherSeparator = "-";
            vm.PartNamesText = "Prefix, Number";
        });
        var vm = b.Vm;
        Assert.Equal(["Prefix", "Number", "weight_g", "chloride_pct", "result"], vm.TriedResult!.Columns);
        Assert.Equal(("S", "001"), (vm.TriedResult.Rows[0]["Prefix"], vm.TriedResult.Rows[0]["Number"]));
        var split = b.Main.FindGuidedChain()!.Value.Part.ColumnSplit;
        Assert.Equal(new[] { "Prefix", "Number" }, split!.PartNames);
        Assert.Equal(["volume_ml"], b.Main.FindGuidedChain()!.Value.Part.ColumnSelection.Names);
        Assert.True(vm.NextCommand.CanExecute(null));

        vm.BackCommand.Execute(null);
        vm.PartNamesText = "Letter, Digits";
        Assert.False(vm.TrySucceeded);
        vm.NextCommand.Execute(null);
        Assert.False(vm.NextCommand.CanExecute(null));
        await TryAsync(vm);
        Assert.True(vm.TrySucceeded);
        Assert.Contains("Letter", vm.TriedResult!.Columns);
    }

    // ----- test on another file -----

    [Fact]
    public async Task AnotherFileWithMoreRows_SameLayoutFound_AndAFileWithoutTheTable_GivesThePlainReason()
    {
        var b = await ToTryAsync(_folder, I10());
        var other = I10("many rows", "second.txt");
        await TestTimeout.Run(() => b.Vm.TestOtherFileAsync(other), TimeSpan.FromSeconds(30));
        Assert.Equal("Same layout found. 4,005 rows.", b.Vm.OtherFileText);

        var missing = I10("table missing", "third.txt");
        await TestTimeout.Run(() => b.Vm.TestOtherFileAsync(missing), TimeSpan.FromSeconds(30));
        _output.WriteLine($"{b.Vm.OtherFileText} | {b.Vm.OtherFileDetails}");
        Assert.Contains(b.Vm.OtherFileText, new[] { PlainErrorText.NotFound, PlainErrorText.NotSameInstrument });
        Assert.NotNull(b.Vm.OtherFileDetails);
    }

    // ----- save as DLL -----

    [Fact]
    public async Task CreateDll_UsesTheGate_AndTheGenerator_ListsTheFiles_AndLogsWithoutPaths()
    {
        var path = I10();
        var b = await ToTryAsync(_folder, path);
        var vm = b.Vm;
        Assert.Null(vm.Dll);
        vm.NextCommand.Execute(null);
        Assert.Equal(GuidedStep.Save, vm.Step);
        Assert.Equal("i10Reader", vm.Dll!.AssemblyName);
        vm.Dll.OutputFolder = Path.Combine(_folder, "out");
        Assert.True(vm.CreateDllCommand.CanExecute(null));

        await TestTimeout.Run(vm.CreateDllAsync, TimeSpan.FromSeconds(30));
        var request = Assert.Single(b.Generated);
        Assert.Equal(MainViewModel.PipelineHash(vm.TriedDefinition!), MainViewModel.PipelineHash(request.Definition));
        Assert.Equal(vm.TriedResult!.Columns, request.OutputColumns);
        Assert.Equal([vm.Dll.DllPath!, vm.Dll.ReadmePath!, GuidedBuilderViewModel.HelperFilesNote], vm.WrittenFiles);
        Assert.Equal(GuidedBuilderViewModel.LicenceNote, vm.SaveMessage);

        var saved = Assert.Single(b.Lineage.Events, e => e.Event == "guided.saved-dll");
        Assert.Equal("i10.txt", saved.File);
        Assert.Equal(["templateId", "templateName", "partNumber", "columns"], saved.Details.Keys);
        Assert.Single(b.Lineage.Events, e => e.Event == "guided.started");
        foreach (var e in b.Lineage.Events.Where(e => e.Event.StartsWith("guided.", StringComparison.Ordinal)))
        {
            Assert.All(e.Details.Values.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? ""), v => Assert.DoesNotContain(_folder, v));
        }
    }

    [Fact]
    public async Task CreateDll_AfterTheCanvasChanged_IsRefusedByTheGate_WithAPlainSentence()
    {
        var b = await ToTryAsync(_folder, I10());
        b.Vm.NextCommand.Execute(null);
        b.Vm.Dll!.OutputFolder = Path.Combine(_folder, "out");
        // A change on the canvas behind the window: the tested run no longer matches.
        b.Main.FindGuidedChain()!.Value.Part.ColumnSplit = new ColumnSplitSettings("sample_id", "-", ["A", "B"]);
        await TestTimeout.Run(b.Vm.CreateDllAsync, TimeSpan.FromSeconds(30));
        Assert.Empty(b.Generated);
        Assert.Equal(MainViewModel.GenerateDllNotTestedHint, b.Vm.SaveMessage);
    }

    // ----- templates -----

    [Fact]
    public async Task TheLayoutIsSavedAsATemplate_AndTheNextFileOfTheInstrument_IsAKnownLayout_WithItsTablePreselected()
    {
        var store = Store(_folder);
        var first = New(_folder, store);
        await ScanAsync(first.Vm, I10());
        first.Vm.NextCommand.Execute(null);
        first.Vm.NextCommand.Execute(null);
        var template = Assert.Single(store.List());
        Assert.Equal("i10", template.Name);
        Assert.Equal(GuidedBuilderViewModel.GuidedNotePrefix + "2", template.Notes);
        Assert.Equal("Layout saved as 'i10'. The next file of this instrument will be recognised.", first.Vm.TemplateText);
        Assert.Contains(first.Lineage.Events, e => e.Event == "template.saved");

        var second = New(_folder, store);
        await ScanAsync(second.Vm, I10("more rows", "next_run.txt"));
        Assert.Equal("Known layout: i10", second.Vm.KnownLayoutText);
        Assert.Equal(2, second.Vm.SelectedTable!.Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASavedLayoutWithTheSameName_IsReplacedOnlyAfterYes(bool yes)
    {
        var store = Store(_folder);
        var first = New(_folder, store);
        await ScanAsync(first.Vm, I10());
        first.Vm.NextCommand.Execute(null);
        first.Vm.NextCommand.Execute(null);
        var old = Assert.Single(store.List());

        // Another builder (a later session) on a file of the same name.
        var second = New(_folder, store, answer: yes);
        Directory.CreateDirectory(Path.Combine(_folder, "sub"));
        var samePath = Path.Combine(_folder, "sub", "i10.txt");
        File.Copy(Path.Combine(_folder, "i10.txt"), samePath);
        await ScanAsync(second.Vm, samePath);
        second.Vm.NextCommand.Execute(null);
        second.Vm.NextCommand.Execute(null);

        Assert.Equal(["A saved layout named 'i10' already exists. Replace it with this one?"], second.Questions);
        var now = Assert.Single(store.List());
        if (yes)
        {
            Assert.NotEqual(old.Id, now.Id);
            Assert.StartsWith("Layout saved as 'i10'.", second.Vm.TemplateText);
        }
        else
        {
            Assert.Equal(old.Id, now.Id);
            Assert.Equal("The saved layout 'i10' was kept as it was.", second.Vm.TemplateText);
        }
    }

    [Fact]
    public async Task R03_TheResultsTable_IsInTheFileTwice_SoTryGivesThePlainSentence_NotNotConfigured()
    {
        var b = New(_folder);
        var vm = b.Vm;
        await ScanAsync(vm, Real("r03_perkinelmer_envision.csv"));
        vm.NextCommand.Execute(null);
        vm.SelectedTable = vm.Tables.Single(t => t.Number == 3);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        await TryAsync(vm);
        Assert.False(vm.TrySucceeded);
        Assert.Equal(PlainErrorText.SeveralCopies, vm.TryMessage);
        Assert.StartsWith("This file has 2 CSV parts like 'Results for A450(1) - channel 1 (A)'", vm.TryDetails);
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    // ----- the canvas -----

    [Fact]
    public void OpeningOnACanvasWithNodes_AsksFirst_DefaultNo()
    {
        var b = New(_folder);
        G6cCanvas.Add(b.Main.Editor, NodeType.CsvFile, 10);
        Assert.Null(b.Main.ShowGuidedBuilder());
        Assert.Equal([MainViewModel.GuidedReplaceQuestion], b.Dialogs.YesNoQuestions);

        b.Dialogs.YesNoAnswer = true;
        Assert.NotNull(b.Main.ShowGuidedBuilder());
        Assert.Equal(2, b.Dialogs.YesNoQuestions.Count);

        var empty = New(_folder);
        Assert.NotNull(empty.Main.ShowGuidedBuilder());
        Assert.Empty(empty.Dialogs.YesNoQuestions);
    }

    [Fact]
    public async Task ColumnChoiceAndSplit_AreSavedInTheProjectFile_AndReadBack()
    {
        var b = await ToTryAsync(_folder, I10(), columns: vm =>
        {
            vm.Columns.Single(c => c.Name == "volume_ml").IsKept = false;
            vm.SplitEnabled = true;
            vm.SplitColumn = "sample_id";
            vm.SelectedSeparator = "other";
            vm.OtherSeparator = "-";
        });
        var part = b.Main.FindGuidedChain()!.Value.Part;
        var settings = ProjectNodeSettings.WriteCsvPart(part.CsvPartSelectorJson, null, part.ColumnSelection, part.ColumnSplit);
        Assert.Equal(["volume_ml"], ProjectNodeSettings.ReadCsvPartColumns(settings).Names);
        Assert.Equal(ColumnSplit.SerializeOrNull(part.ColumnSplit), ColumnSplit.SerializeOrNull(ProjectNodeSettings.ReadCsvPartSplit(settings)));

        var plain = ProjectNodeSettings.WriteCsvPart(part.CsvPartSelectorJson);
        Assert.Equal(["selector"], plain.EnumerateObject().Select(p => p.Name));
    }

    // ----- parity: the builder's pipeline equals the same pipeline built by hand on the canvas -----

    public static TheoryData<string, int?> ParityFiles() => new()
    {
        { "i10", null },
        { "r03_perkinelmer_envision.csv", null },
        { "r04_quantstudio.txt", null }
    };

    [Theory]
    [MemberData(nameof(ParityFiles))]
    public async Task ThePipelineOfTheBuilder_EqualsTheSamePipelineConnectedByHand(string file, int? table)
    {
        var path = file == "i10" ? I10() : Real(file);
        var b = await ToTryAsync(_folder, path, table);
        var tried = b.Vm.TriedDefinition!;
        if (file == "r04_quantstudio.txt")
        {
            Assert.Equal("\t", tried.CsvPart!.Expected.Delimiter);
        }

        // "Open in the canvas": Execute there gives the same definition and rows.
        Assert.True(b.Main.TryBuildExecuteDefinition(out var onCanvas, out var reason), reason);
        Assert.Equal(MainViewModel.PipelineHash(tried), MainViewModel.PipelineHash(onCanvas!));
        var execute = new ExecuteViewModel(onCanvas!);
        await TestTimeout.Run(execute.RunAsync);
        Assert.Equal(G6bParts.RowTexts(b.Vm.TriedResult!.Columns, b.Vm.TriedResult.Rows), G6bParts.RowTexts(execute.Result!.Columns, execute.Result.Rows));

        // A user connecting the nodes by hand, with the same part ("Use in pipeline") and the same column choice.
        var hand = new MainViewModel(new ProjectEditorTests.FakeDialogs(), new ProjectEditorTests.MemoryRecent(), new IntakeStudioOpenTests.MemoryLineage());
        var intake = G6cCanvas.Add(hand.Editor, NodeType.FileIntake, 10);
        intake.SetFileIntakeSettings(intake.GetFileIntakeSettings() with { Path = path });
        Assert.True(hand.UseCsvPart(intake, tried.CsvPart!).Changed);
        var part = hand.Editor.Nodes.Single(n => n.NodeType == NodeType.CsvPart);
        var output = G6cCanvas.Add(hand.Editor, NodeType.DotNetObject, 500);
        G6cCanvas.Connect(hand.Editor, part.Outputs[0], output.Inputs[0]);
        hand.Editor.Validate();
        await TestTimeout.Run(() => hand.CsvPartRefresh ?? Task.CompletedTask);
        hand.Editor.Validate();
        Assert.True(hand.TryBuildExecuteDefinition(out var byHand, out var handReason), handReason);
        Assert.Equal(MainViewModel.PipelineHash(tried), MainViewModel.PipelineHash(byHand!));
        var handRun = await G6bParts.Run(byHand!, null);
        Assert.Equal(G6bParts.RowTexts(b.Vm.TriedResult.Columns, b.Vm.TriedResult.Rows), G6bParts.RowTexts(handRun.Columns, handRun.Rows));
        _output.WriteLine($"{file}: part {tried.CsvPart!.PartIndex + 1}, {handRun.Counts}");
    }
}
