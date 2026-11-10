using System.Windows;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.UI.Services;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>The Execute dialog's view model, without any window. Every run goes through TestTimeout.</summary>
[Collection(LargeFileCollection.Name)]
public class ExecuteViewModelTests
{
    private static Task Run(ExecuteViewModel vm, TimeSpan? limit = null) => TestTimeout.Run(() => vm.RunAsync(), limit);

    [Fact]
    public async Task C01_In4_Out24_NoRejects()
    {
        var vm = new ExecuteViewModel(PipelineDefinitionTests.C01Definition());

        await Run(vm);

        Assert.Null(vm.Message);
        Assert.Equal(new PipelineCounts(4, 4, 0, 0, 24), vm.Counts);
        Assert.Equal(24, vm.OutputRowsView!.Count);
        Assert.Equal(["sample_id", "component", "value_a", "value_b", "value_c"], vm.OutputRowsView.Table!.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName));
        Assert.Equal("Showing all 24 rows", vm.OutputNote);
        Assert.Empty(vm.Rejects);
        Assert.True(vm.ShowNoRejects);
        Assert.True(vm.ShowNoFlagged);
        Assert.Null(vm.CancelledText);
        Assert.StartsWith("Read 4 · Parsed 4 · Rejected 0 · Flagged 0 · Out 24 · ", vm.Summary);
        Assert.False(vm.IsRunning);
        Assert.False(vm.CopyRejectsCommand.CanExecute(null));
    }

    [Fact]
    public async Task C04_ListsTheRejects_AndCopiesThemTabSeparated()
    {
        string? copied = null;
        var vm = new ExecuteViewModel(await PipelineRunnerTests.ManifestDefinitionAsync("C04"), copyText: t => copied = t);

        await Run(vm);

        Assert.Equal(await CompositeManifestTests.ReadIssuesAsync("expected/C04_rejects.csv"), vm.Rejects);
        Assert.False(vm.ShowNoRejects);
        Assert.True(vm.CopyRejectsCommand.CanExecute(null));

        vm.CopyRejectsCommand.Execute(null);

        Assert.Equal("record_number\treason\r\n2\tno key delimiter\r\n3\tempty value\r\n5\tempty key\r\n".ReplaceLineEndings(), copied);
    }

    [Fact]
    public async Task Cancel_WhileRunning_SaysCancelledAfterNRecords()
    {
        var path = PipelineRunnerTests.PrivateCopyOfC10();
        try
        {
            var vm = new ExecuteViewModel(PipelineDefinitionTests.C01Definition(path: path));

            // RunAsync sets IsRunning before its first await; only the wait goes under the timeout.
            var running = vm.RunAsync();
            Assert.True(vm.IsRunning);
            Assert.True(vm.CancelCommand.CanExecute(null));
            Assert.False(vm.RunCommand.CanExecute(null));
            vm.CancelCommand.Execute(null);
            await TestTimeout.Run(() => running);

            Assert.True(vm.Result!.Cancelled);
            Assert.Equal($"Cancelled after {vm.Counts.Read:N0} records. Rows so far are shown.", vm.CancelledText);
            Assert.False(vm.IsRunning);
            Assert.False(vm.CancelCommand.CanExecute(null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [LicenceLimitFact]
    public async Task C10_WithoutAValidLicence_ShowsTheLicenceMessage_AndKeepsRows()
    {
        var path = PipelineRunnerTests.PrivateCopyOfC10();
        try
        {
            var definition = await PipelineRunnerTests.ManifestDefinitionAsync("C10");
            var vm = new ExecuteViewModel(definition with { CsvFile = definition.CsvFile with { Path = path } });

            await Run(vm, TimeSpan.FromSeconds(30));

            Assert.True(vm.IsLicenceMessage);
            Assert.Equal(PipelineRunner.LicenceLimitMessage(vm.Counts.Read), vm.Message);
            // Without a valid licence the stop point varies (ETLBox reads ahead), so any number of rows
            // up to the limit can arrive, even none. Promised: the rows read are kept, and the grid
            // shows the first of them (at most 200) with a note that matches.
            var kept = vm.Result!.Rows.Count;
            var shown = vm.OutputRowsView!.Count;
            Assert.InRange(shown, 0, ExecuteViewModel.ShownRows);
            Assert.Equal(Math.Min(kept, ExecuteViewModel.ShownRows), shown);
            Assert.Equal(kept == 0 ? ExecuteViewModel.NoRowsText
                : kept > ExecuteViewModel.ShownRows ? $"Showing the first 200 of {kept:N0} rows"
                : $"Showing all {kept:N0} rows", vm.OutputNote);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MissingFile_GivesAClearMessage_NoException()
    {
        var vm = new ExecuteViewModel(PipelineDefinitionTests.C01Definition()) { FilePath = PipelineDefinitionTests.CompositePath("missing.csv") };

        await Run(vm);

        Assert.Equal("File was not found. Check the path and try again.", vm.Message);
        Assert.Equal(ExecuteViewModel.NoRowsText, vm.OutputNote);
        Assert.False(vm.IsLicenceMessage);
    }

    [Fact]
    public async Task ColumnsDoNotMatch_SaysWhichColumnIsMissing()
    {
        var vm = new ExecuteViewModel(PipelineDefinitionTests.C01Definition()) { FilePath = PipelineDefinitionTests.CompositePath("c12_irregular_slots.csv") };

        await Run(vm);

        Assert.Equal(
            "The file does not match the ParseValue settings: Slot S1 uses the column 'r01', which is not in the file. Edit the slot.",
            vm.Message);
    }

    [Fact]
    public void Browse_SetsTheFilePath_CancelLeavesIt()
    {
        string? answer = PipelineDefinitionTests.CompositePath("c04_bad_cells.csv");
        var vm = new ExecuteViewModel(PipelineDefinitionTests.C01Definition(), pickFile: _ => answer);

        vm.BrowseCommand.Execute(null);
        Assert.Equal(PipelineDefinitionTests.CompositePath("c04_bad_cells.csv"), vm.FilePath);

        answer = null;
        vm.BrowseCommand.Execute(null);
        Assert.Equal(PipelineDefinitionTests.CompositePath("c04_bad_cells.csv"), vm.FilePath);

        vm.FilePath = "";
        Assert.False(vm.RunCommand.CanExecute(null));
    }
}

/// <summary>When the Execute button is enabled, and the definition it builds from the graph.</summary>
public class ExecuteEnablingTests
{
    private sealed class NoDialogs : IDialogService
    {
        public ExecuteViewModel? Shown { get; private set; }
        public void ShowCsvPreview(NodeViewModel node, CsvFileSettings settings) { }
        public void ShowParseValue(NodeViewModel node, ParseValueUpstream upstream) { }
        public void ShowExecute(ExecuteViewModel viewModel) => Shown = viewModel;
        public string? PickCsvFile(string? currentPath) => null;
        public GenerateDllViewModel? ShownGenerateDll { get; private set; }
        public void ShowGenerateDll(GenerateDllViewModel viewModel) => ShownGenerateDll = viewModel;
        public string? PickFolder(string? currentFolder) => null;
        public string? PickProjectToOpen(string? currentPath) => null;
        public string? PickProjectToSave(string? currentPath, string projectName) => null;
        public UnsavedChangesChoice AskUnsavedChanges(string projectName) => UnsavedChangesChoice.Cancel;
        public void ShowError(string title, string message) { }
        public void ShowIntakeStudio(IntakeStudioViewModel viewModel) { }
        public string? PickIntakeFile(string? currentPath) => null;
    }

    private static (MainViewModel Main, NodeViewModel Csv, NodeViewModel Parse, NodeViewModel Output, NoDialogs Dialogs) BuildGraph(bool connectOutput = true)
    {
        var dialogs = new NoDialogs();
        var main = new MainViewModel(dialogs);
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
        if (connectOutput)
        {
            editor.StartConnection(parse.Outputs[0]);
            editor.CompleteConnection(output.Inputs[0]);
        }

        editor.Validate();
        return (main, csv, parse, output, dialogs);
    }

    [Fact]
    public void CompleteConfiguredGraph_EnablesExecute_AndOpensTheDialogWithTheDefinition()
    {
        var (main, _, _, _, dialogs) = BuildGraph();

        Assert.Null(main.ExecuteBlockedReason);
        Assert.Equal(MainViewModel.ExecuteReadyHint, main.ExecuteHint);
        Assert.True(main.ExecuteCommand.CanExecute(null));

        main.ExecuteCommand.Execute(null);

        Assert.NotNull(dialogs.Shown);
        Assert.Equal(PipelineDefinitionTests.CompositePath("c01_basic.csv"), dialogs.Shown!.FilePath);
    }

    [Fact]
    public void MissingDotNetObjectConnection_DisablesExecute_WithTheReason()
    {
        var (main, _, _, _, _) = BuildGraph(connectOutput: false);

        Assert.False(main.ExecuteCommand.CanExecute(null));
        Assert.Equal("Execute needs exactly CsvFile -> ParseValue -> DotNetObject on the canvas, connected in that order.", main.ExecuteHint);
    }

    [Fact]
    public void NotConfiguredNode_DisablesExecute_WithTheNodeName()
    {
        var (main, _, _, output, _) = BuildGraph();
        output.Settings[0].Value = nameof(ObjectKind.DataTable);
        main.Editor.Validate();

        Assert.False(main.ExecuteCommand.CanExecute(null));
        Assert.Equal("Configure every node first: DotNetObject is not configured. See the Issues tab.", main.ExecuteBlockedReason);
    }

    [Fact]
    public void ExtraNode_DisablesExecute()
    {
        var (main, _, _, _, _) = BuildGraph();
        main.Editor.AddNode(NodeType.ToJson, new Point(0, 300));

        Assert.False(main.ExecuteCommand.CanExecute(null));
        Assert.StartsWith("Execute needs exactly", main.ExecuteHint);
    }
}
