using System.Windows;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Generation;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.UI.Services;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>The Generate DLL dialog's view model (no UI) and when the button is enabled.</summary>
public class GenerateDllViewModelTests
{
    private static readonly IReadOnlyList<string> C01Columns = ["sample_id", "component", "value_a", "value_b", "value_c"];

    private static string TempFolder() => Path.Combine(Path.GetTempPath(), $"taskE_vm_{Guid.NewGuid():N}");

    private static GenerateDllViewModel Vm(PipelineDefinition? definition = null, Func<CsvPipelineDllRequest, CsvPipelineDllResult>? generate = null,
        string? folder = null, Action<string>? copy = null)
        => new(definition ?? PipelineDefinitionTests.C01Definition(), C01Columns, generate, copyText: copy,
            isInsideProject: _ => false, outputFolder: folder ?? TempFolder());

    [Fact]
    public void Defaults_AreValid_AndTheSummaryShowsWhatIsBaked()
    {
        var vm = Vm();

        Assert.Equal("CsvPipeline", vm.AssemblyName);
        Assert.Equal("Generated", vm.Namespace);
        Assert.True(vm.CanGenerate);
        Assert.True(vm.GenerateCommand.CanExecute(null));
        Assert.Equal(["Source column", "Levels", "Slots", "Carry", "Output type"], vm.Summary.Select(s => s.Name));
        Assert.Equal("sample", vm.Summary[0].Value);
        Assert.StartsWith("6 (S1: r01, r02, r03;", vm.Summary[2].Value);
        Assert.Equal("none", vm.Summary[3].Value);
        Assert.Equal("List<Dictionary<string, string>>", vm.Summary[4].Value);
    }

    [Theory]
    [InlineData("1Pipeline")]
    [InlineData("my pipeline")]
    [InlineData("")]
    [InlineData("class")]
    public void BadAssemblyName_GivesItsMessage_AndDisablesGenerate(string name)
    {
        var vm = Vm();

        vm.AssemblyName = name;

        Assert.Equal(CsvPipelineDllGenerator.CheckAssemblyName(name), vm.AssemblyNameError);
        Assert.NotNull(vm.AssemblyNameError);
        Assert.False(vm.CanGenerate);
        Assert.False(vm.GenerateCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("Lab..Pipelines")]
    [InlineData("Lab.1Pipelines")]
    [InlineData("Lab Pipelines")]
    public void BadNamespace_GivesItsMessage_AndDisablesGenerate(string name)
    {
        var vm = Vm();

        vm.Namespace = name;

        Assert.NotNull(vm.NamespaceError);
        Assert.False(vm.CanGenerate);
    }

    [Fact]
    public void FolderInsideAProject_IsRefused()
    {
        var vm = new GenerateDllViewModel(PipelineDefinitionTests.C01Definition(), C01Columns, isInsideProject: _ => true, outputFolder: TempFolder());

        Assert.Equal(GenerateDllViewModel.ProjectFolderMessage, vm.OutputFolderError);
        Assert.False(vm.CanGenerate);
    }

    [Fact]
    public async Task InvalidDefinition_IsRefused_WithTheValidatorMessages()
    {
        var folder = TempFolder();
        var bad = PipelineDefinitionTests.C01Definition() with { ParseValue = PipelineDefinitionTests.C01Definition().ParseValue with { SourceColumn = "" } };
        var vm = Vm(bad, folder: folder);

        await vm.GenerateAsync();

        Assert.False(vm.Succeeded);
        Assert.Contains($"ParseValue: {ParseValueValidator.NoSourceColumn}", vm.Messages);
        Assert.Null(vm.DllPath);
        Assert.False(File.Exists(Path.Combine(folder, "CsvPipeline.dll")));
    }

    [Fact]
    public async Task Success_WritesTheDllAndTheReadme()
    {
        var folder = TempFolder();
        try
        {
            var vm = Vm(folder: folder);
            vm.AssemblyName = "LabPipeline";
            vm.Namespace = "Lab.Pipelines";

            await vm.GenerateAsync();

            Assert.True(vm.Succeeded, string.Join(" | ", vm.Messages.Concat(vm.CompileErrors.Select(e => e.Message))));
            Assert.Empty(vm.CompileErrors);
            Assert.True(File.Exists(vm.DllPath), vm.DllPath);
            Assert.True(File.Exists(vm.ReadmePath), vm.ReadmePath);
            Assert.Equal("LabPipeline.dll", Path.GetFileName(vm.DllPath));
            Assert.False(vm.IsGenerating);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task CompileErrors_AreListed()
    {
        var errors = new[] { new CompileError("CsvPipeline.cs", 12, "CS1002: ; expected") };
        var vm = Vm(generate: _ => new CsvPipelineDllResult(false, null, null, ["The DLL did not compile."], errors));

        await vm.GenerateAsync();

        Assert.False(vm.Succeeded);
        Assert.Equal(errors, vm.CompileErrors);
        Assert.Equal(["The DLL did not compile."], vm.Messages);
    }

    [Fact]
    public async Task GeneratorThrows_GivesAMessage_NoException()
    {
        var vm = Vm(generate: _ => throw new IOException("disk full"));

        await vm.GenerateAsync();

        Assert.False(vm.Succeeded);
        Assert.Contains("disk full", Assert.Single(vm.Messages));
    }

    [Fact]
    public void CopyUsage_CopiesTheExampleWithTheChosenNames()
    {
        string? copied = null;
        var vm = Vm(copy: t => copied = t);
        vm.AssemblyName = "LabPipeline";

        vm.CopyUsageCommand.Execute(null);

        Assert.Equal(CsvPipelineDllGenerator.UsageExample("LabPipeline", "Generated"), copied);
        Assert.Contains("RunAsync", copied);
    }
}

/// <summary>Generate DLL is enabled only after a successful Execute of the current settings.</summary>
public class GenerateDllEnablingTests
{
    private sealed class NoDialogs : IDialogService
    {
        public GenerateDllViewModel? Shown { get; private set; }
        public void ShowCsvPreview(NodeViewModel node, CsvFileSettings settings) { }
        public void ShowParseValue(NodeViewModel node, ParseValueUpstream upstream) { }
        public void ShowExecute(ExecuteViewModel viewModel) { }
        public string? PickCsvFile(string? currentPath) => null;
        public void ShowGenerateDll(GenerateDllViewModel viewModel) => Shown = viewModel;
        public string? PickFolder(string? currentFolder) => null;
        public string? PickProjectToOpen(string? currentPath) => null;
        public string? PickProjectToSave(string? currentPath, string projectName) => null;
        public UnsavedChangesChoice AskUnsavedChanges(string projectName) => UnsavedChangesChoice.Cancel;
        public void ShowError(string title, string message) { }
    }

    private static (MainViewModel Main, NodeViewModel Csv, NoDialogs Dialogs, PipelineDefinition Definition) Graph()
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
        editor.StartConnection(parse.Outputs[0]);
        editor.CompleteConnection(output.Inputs[0]);
        editor.Validate();

        Assert.True(main.TryBuildExecuteDefinition(out var definition, out var reason), reason);
        return (main, csv, dialogs, definition!);
    }

    private static PipelineResult Result(bool cancelled = false, string? error = null, bool licence = false)
        => new(["sample_id"], new(), [], [], new PipelineCounts(4, 4, 0, 0, 24), cancelled, 5, error, licence);

    [Fact]
    public void BeforeASuccessfulExecute_IsDisabled_WithTheTooltip()
    {
        var (main, _, _, _) = Graph();

        Assert.False(main.GenerateDllCommand.CanExecute(null));
        Assert.Equal("Run Execute successfully first. The DLL is generated only from a pipeline that has been tested.", main.GenerateDllHint);
    }

    [Fact]
    public void AfterASuccessfulExecute_IsEnabled_AndOpensTheDialog()
    {
        var (main, _, dialogs, definition) = Graph();

        main.OnExecuteRunFinished(definition, Result());

        Assert.Null(main.GenerateDllBlockedReason);
        Assert.Equal(MainViewModel.GenerateDllReadyHint, main.GenerateDllHint);
        Assert.True(main.GenerateDllCommand.CanExecute(null));

        main.GenerateDllCommand.Execute(null);
        Assert.NotNull(dialogs.Shown);
    }

    [Theory]
    [InlineData(true, null, false)]
    [InlineData(false, "File was not found. Check the path and try again.", false)]
    [InlineData(false, "The ETLBox licence limit was reached after 5,001 rows.", true)]
    public void CancelledErrorOrLicenceRun_DoesNotCount(bool cancelled, string? error, bool licence)
    {
        var (main, _, _, definition) = Graph();

        main.OnExecuteRunFinished(definition, Result(cancelled, error, licence));

        Assert.False(main.GenerateDllCommand.CanExecute(null));
        Assert.Equal(MainViewModel.GenerateDllNotTestedHint, main.GenerateDllHint);
    }

    [Fact]
    public void ChangingASetting_DisablesItAgain()
    {
        var (main, csv, _, definition) = Graph();
        main.OnExecuteRunFinished(definition, Result());

        csv.Settings.First(s => s.Key == "SkipLines").Value = "1";
        csv.Settings.First(s => s.Key == "SkipLines").Value = "0";

        Assert.False(main.GenerateDllCommand.CanExecute(null));

        // A setting change also forgets the CsvFile columns; once they are known again, the
        // settings equal the tested ones, but the run still has to be repeated.
        csv.KnownColumns = ["id", "batch", "name", "sample", .. Enumerable.Range(1, 18).Select(i => $"r{i:00}")];
        main.Editor.Validate();
        Assert.Equal(MainViewModel.GenerateDllNotTestedHint, main.GenerateDllHint);
    }

    [Fact]
    public void ChangingTheParseValueSettings_OrAConnection_DisablesItAgain()
    {
        var (main, _, _, definition) = Graph();
        var parse = main.Editor.Nodes[1];

        main.OnExecuteRunFinished(definition, Result());
        parse.ParseValueSettings = parse.ParseValueSettings! with { Trim = false };
        Assert.False(main.GenerateDllCommand.CanExecute(null));

        main.OnExecuteRunFinished(definition with { ParseValue = parse.ParseValueSettings }, Result());
        main.Editor.Disconnect(main.Editor.Connections[1]);
        Assert.False(main.GenerateDllCommand.CanExecute(null));
    }

    [Fact]
    public void EmptyCanvas_KeepsTheLegacyRule()
    {
        var main = new MainViewModel(new NoDialogs());

        Assert.False(main.GenerateDllCommand.CanExecute(null));
        Assert.Equal(MainViewModel.GenerateDllLegacyHint, main.GenerateDllHint);
    }
}
