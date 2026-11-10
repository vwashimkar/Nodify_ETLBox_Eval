using System.Windows;
using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.Services;
using EtlDesigner.UI.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>Layout templates in Intake Studio, without a window (G5c2 step 2). Temp store, temp lineage.</summary>
public sealed class IntakeStudioTemplateTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("intake-templates-").FullName;
    private readonly ITestOutputHelper _output;

    public IntakeStudioTemplateTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private string StoreFolder => Path.Combine(_folder, "templates");

    private FileTemplateStore Store(int max = 500) => new(new TemplateSettings { Folder = StoreFolder, MaxTemplates = max }, Path.Combine(_folder, "fallback"));

    private static IntakeStudioViewModel Studio(string path, ITemplateStore store, ILineageLog? lineage = null, Func<string, bool>? confirm = null,
        string? plan = null, Action<string?>? savePlan = null)
        => new(new FileIntakeNodeSettings { Path = path }, lineage: lineage ?? new IntakeStudioOpenTests.MemoryLineage(),
            confirm: confirm, templates: store, savedPlanJson: plan, savePlan: savePlan);

    /// <summary>Scans and waits for the template suggestion too.</summary>
    internal static async Task Scan(IntakeStudioViewModel vm)
    {
        await IntakeStudioEditTests.Scan(vm);
        if (vm.SuggestTask is { } suggest)
        {
            await TestTimeout.Run(() => suggest, TimeSpan.FromSeconds(30));
        }
    }

    internal static async Task Run(IntakeStudioViewModel vm, Action command)
    {
        command();
        if (vm.TemplateTask is { } task)
        {
            await TestTimeout.Run(() => task, TimeSpan.FromSeconds(30));
        }
    }

    private static void AssertTemplateCommands(IntakeStudioViewModel vm, bool enabled)
    {
        foreach (var command in new[] { vm.SaveTemplateCommand, vm.ApplyTemplateCommand, vm.DeleteTemplateCommand, vm.RenameTemplateCommand, vm.ApplyBannerCommand })
        {
            Assert.Equal(enabled, command.CanExecute(null));
        }
    }

    private string CopyOfI01(string? text = null)
    {
        var path = Path.Combine(_folder, $"i01-{Guid.NewGuid():N}.txt");
        if (text is null)
        {
            File.Copy(IntakeTestData.PathOf("i01_sample"), path);
        }
        else
        {
            File.WriteAllText(path, text);
        }

        return path;
    }

    /// <summary>i01 with its first table of 3 or more lines renamed "Results table", saved as the template "i01 edited".</summary>
    private async Task<(IntakeStudioViewModel Vm, long LastTableLine)> SavedI01TemplateAsync(ITemplateStore store, ILineageLog? lineage = null)
    {
        var vm = Studio(CopyOfI01(), store, lineage);
        await Scan(vm);
        var table = vm.Containers.First(r => r.Info.Format == "csv" && r.Info.ContentLineCount >= 3);
        vm.SelectedContainer = table;
        vm.NewName = "Results table";
        vm.RenameCommand.Execute(null);
        vm.TemplateName = "i01 edited";
        await Run(vm, () => vm.SaveTemplateCommand.Execute(null));
        Assert.Equal("Saved the layout as 'i01 edited'.", vm.TemplateStatus);
        return (vm, table.Info.ContentLastLine!.Value);
    }

    /// <summary>A copy of i01 with one more row in the table that ends at <paramref name="lastRow"/>.</summary>
    private string I01WithMoreRows(long lastRow)
    {
        var lines = File.ReadAllText(IntakeTestData.PathOf("i01_sample")).Split('\n').ToList();
        lines.Insert((int)lastRow, lines[(int)lastRow - 1]);
        return CopyOfI01(string.Join("\n", lines));
    }

    [Fact]
    public async Task EverythingIsDisabled_BeforeAScan_WhileScanning_AndEnabledAfter()
    {
        var store = Store();
        await SavedI01TemplateAsync(store);
        var vm = Studio(CopyOfI01(), store);
        Assert.Single(vm.Templates);
        vm.SelectedTemplate = vm.Templates[0];
        Assert.False(vm.CanUseTemplates);
        AssertTemplateCommands(vm, false);

        vm.ScanCommand.Execute(null);
        Assert.True(vm.IsScanning);
        AssertTemplateCommands(vm, false);
        await TestTimeout.Run(() => vm.ScanTask!, TimeSpan.FromSeconds(60));
        await TestTimeout.Run(() => vm.SuggestTask!, TimeSpan.FromSeconds(30));

        Assert.True(vm.CanUseTemplates);
        Assert.True(vm.SaveTemplateCommand.CanExecute(null));
        Assert.True(vm.IsBannerVisible);
        vm.SelectedTemplate = vm.Templates[0];
        Assert.True(vm.ApplyTemplateCommand.CanExecute(null));
        Assert.True(vm.DeleteTemplateCommand.CanExecute(null));
        Assert.True(vm.RenameTemplateCommand.CanExecute(null));
        Assert.True(vm.ApplyBannerCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_ThenList_SelectsTheNewRow_AndTheNameRules()
    {
        var store = Store();
        var path = CopyOfI01();
        var vm = Studio(path, store);
        await Scan(vm);
        Assert.Equal(Path.GetFileNameWithoutExtension(path), vm.TemplateName);

        vm.TemplateName = "   ";
        Assert.False(vm.SaveTemplateCommand.CanExecute(null));
        vm.TemplateName = new string('x', 81);
        Assert.False(vm.SaveTemplateCommand.CanExecute(null));
        vm.TemplateName = "  " + new string('x', 80) + "  ";
        Assert.True(vm.SaveTemplateCommand.CanExecute(null));

        vm.TemplateName = " Lab A ";
        await Run(vm, () => vm.SaveTemplateCommand.Execute(null));
        Assert.Equal("Saved the layout as 'Lab A'.", vm.TemplateStatus);
        var row = Assert.Single(vm.Templates);
        Assert.Same(row, vm.SelectedTemplate);
        Assert.Equal("Lab A", row.Name);
        Assert.Equal(IntakeText.Count(vm.Containers.Count, "part"), row.Parts);
        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), row.Created);
        Assert.Single(Directory.GetFiles(StoreFolder, "*.json"));
    }

    [Fact]
    public async Task Save_WhenTheStoreRefuses_ShowsTheEngineMessage()
    {
        var store = Store(max: 1);
        var vm = Studio(CopyOfI01(), store);
        await Scan(vm);
        await Run(vm, () => vm.SaveTemplateCommand.Execute(null));
        vm.TemplateName = "Second";
        await Run(vm, () => vm.SaveTemplateCommand.Execute(null));
        Assert.Equal("There are already 1 saved layouts; delete one before saving another.", vm.TemplateStatus);
        Assert.Single(vm.Templates);
    }

    [Fact]
    public async Task Banner_Exact_OnI01WithMoreRows_ApplyGivesTheEditedLayout_UndoIsOneStep()
    {
        var store = Store();
        var (source, lastRow) = await SavedI01TemplateAsync(store);
        var vm = Studio(I01WithMoreRows(lastRow), store);
        await Scan(vm);

        Assert.True(vm.IsBannerVisible);
        Assert.Equal("A saved layout, 'i01 edited', matches this file.", vm.BannerText);
        Assert.Equal("Exact", Assert.Single(vm.Templates).Match);
        Assert.DoesNotContain(vm.Containers, r => r.Info.Name == "Results table");
        Assert.False(vm.HasUnsavedChanges);

        await Run(vm, () => vm.ApplyBannerCommand.Execute(null));
        _output.WriteLine(vm.TemplateStatus);
        Assert.False(vm.IsBannerVisible);
        Assert.StartsWith("Applied 'i01 edited': ", vm.TemplateStatus);
        Assert.Contains(vm.Containers, r => r.Info.Name == "Results table");
        Assert.Equal(source.Containers.Select(r => r.Info.Name), vm.Containers.Select(r => r.Info.Name));
        Assert.NotNull(vm.SelectedContainer);
        Assert.True(vm.UndoCommand.CanExecute(null));
        Assert.True(vm.HasUnsavedChanges);

        vm.UndoCommand.Execute(null);
        Assert.False(vm.Session!.IsModified);
        Assert.DoesNotContain(vm.Containers, r => r.Info.Name == "Results table");
        Assert.False(vm.IsBannerVisible);
    }

    [Fact]
    public async Task NotNow_HidesTheBanner_AndWritesTheEvent()
    {
        var store = Store();
        var (_, lastRow) = await SavedI01TemplateAsync(store);
        var lineage = new IntakeStudioOpenTests.MemoryLineage();
        var vm = Studio(I01WithMoreRows(lastRow), store, lineage);
        await Scan(vm);
        var id = vm.BannerCandidate!.Template.Id.ToString("D");

        vm.DismissBannerCommand.Execute(null);
        Assert.False(vm.IsBannerVisible);
        Assert.False(vm.ApplyBannerCommand.CanExecute(null));
        var dismissed = Assert.Single(lineage.Events, e => e.Event == "template.dismissed");
        Assert.Equal(id, dismissed.Details["templateId"]);
        Assert.Single(dismissed.Details);

        vm.SelectedContainer = vm.Containers[3];
        Assert.False(vm.IsBannerVisible);
    }

    [Fact]
    public async Task APlanAppliedForTheFile_HidesTheBanner()
    {
        var store = Store();
        string? plan = null;
        var path = CopyOfI01();
        var vm = Studio(path, store, savePlan: json => plan = json);
        await Scan(vm);
        vm.SelectedContainer = vm.Containers.First(r => r.Info.Format == "csv");
        vm.NewName = "Results table";
        vm.RenameCommand.Execute(null);
        await Run(vm, () => vm.SaveTemplateCommand.Execute(null));
        vm.SaveCommand.Execute(null);
        Assert.NotNull(plan);

        var again = Studio(path, store, plan: plan);
        await Scan(again);
        Assert.Contains(again.Containers, r => r.Info.Name == "Results table");
        Assert.False(again.IsBannerVisible);
        Assert.Equal("Exact", again.Templates.Single().Match);
    }

    [Fact]
    public async Task ARefusedPlan_ShowsItsMessage_AndTheBanner()
    {
        var store = Store();
        string? plan = null;
        var vm = Studio(CopyOfI01(), store, savePlan: json => plan = json);
        await Scan(vm);
        var table = vm.Containers.First(r => r.Info.Format == "csv" && r.Info.ContentLineCount >= 3);
        vm.SelectedContainer = table;
        vm.NewName = "Results table";
        vm.RenameCommand.Execute(null);
        await Run(vm, () => vm.SaveTemplateCommand.Execute(null));
        vm.SaveCommand.Execute(null);

        var other = Studio(I01WithMoreRows(table.Info.ContentLastLine!.Value), store, plan: plan);
        await Scan(other);
        Assert.EndsWith(IntakeStudioViewModel.AutomaticResultText, other.EditStatus);
        Assert.True(other.IsBannerVisible);
    }

    [Fact]
    public async Task ADifferentStructure_ShowsNoBanner()
    {
        var store = Store();
        await SavedI01TemplateAsync(store);
        var vm = Studio(CopyOfI01("{\n  \"a\": 1\n}\n"), store);
        await Scan(vm);
        Assert.False(vm.IsBannerVisible);
        Assert.Equal(string.Empty, vm.Templates.Single().Match);
    }

    /// <summary>After fix 1a r09 and r12 have the same structure but other column names: never an Exact banner.</summary>
    [Fact]
    public async Task R09WithAnR12Template_ShowsNoExactBanner()
    {
        var real = Path.Combine(IntakeTestData.Folder, "real");
        var store = Store();
        var source = Studio(Directory.GetFiles(real, "r12*").Single(), store);
        await Scan(source);
        source.TemplateName = "r12";
        await Run(source, () => source.SaveTemplateCommand.Execute(null));

        var vm = Studio(Directory.GetFiles(real, "r09*").Single(), store);
        await Scan(vm);
        _output.WriteLine(vm.BannerText ?? "no banner");
        Assert.NotEqual("Exact", vm.Templates.Single().Match);
        if (vm.IsBannerVisible)
        {
            Assert.Equal("A saved layout, 'r12', looks similar to this file. The column names are different.", vm.BannerText);
        }
    }

    [Fact]
    public async Task Delete_AsksFirst_NoKeepsIt_YesDeletes()
    {
        var store = Store();
        await SavedI01TemplateAsync(store);
        var answers = new Queue<bool>([false, true]);
        var questions = new List<string>();
        var lineage = new IntakeStudioOpenTests.MemoryLineage();
        var vm = Studio(CopyOfI01(), store, lineage, confirm: q =>
        {
            questions.Add(q);
            return answers.Dequeue();
        });
        await Scan(vm);
        vm.SelectedTemplate = vm.Templates.Single();

        await Run(vm, () => vm.DeleteTemplateCommand.Execute(null));
        Assert.Single(vm.Templates);
        Assert.Single(store.List());
        Assert.Equal("Delete the saved layout 'i01 edited'? This cannot be undone.", questions.Single());

        await Run(vm, () => vm.DeleteTemplateCommand.Execute(null));
        Assert.Empty(vm.Templates);
        Assert.Empty(store.List());
        Assert.Null(vm.SelectedTemplate);
        Assert.False(vm.IsBannerVisible);
        Assert.Equal("Deleted the saved layout 'i01 edited'.", vm.TemplateStatus);
        Assert.Single(lineage.Events, e => e.Event == "template.deleted");
    }

    [Fact]
    public async Task Rename_UsesTheNameBox()
    {
        var store = Store();
        await SavedI01TemplateAsync(store);
        var lineage = new IntakeStudioOpenTests.MemoryLineage();
        var vm = Studio(CopyOfI01(), store, lineage);
        await Scan(vm);
        vm.SelectedTemplate = vm.Templates.Single();
        Assert.Equal("i01 edited", vm.TemplateName);
        vm.TemplateName = "";
        Assert.False(vm.RenameTemplateCommand.CanExecute(null));
        vm.TemplateName = "Lab B";
        await Run(vm, () => vm.RenameTemplateCommand.Execute(null));
        Assert.Equal("Renamed the saved layout to 'Lab B'.", vm.TemplateStatus);
        Assert.Equal("Lab B", vm.SelectedTemplate!.Name);
        Assert.Equal("Lab B", store.List().Single().Name);
        var renamed = Assert.Single(lineage.Events, e => e.Event == "template.renamed");
        Assert.Equal("Lab B", renamed.Details["templateName"]);
    }

    [Fact]
    public async Task ADamagedTemplateFile_IsReportedOnce()
    {
        Directory.CreateDirectory(StoreFolder);
        File.WriteAllText(Path.Combine(StoreFolder, Guid.NewGuid().ToString("D") + ".json"), "{ not json");
        var store = Store();
        var vm = Studio(CopyOfI01(), store);
        Assert.Equal("1 saved layout could not be read.", vm.TemplateStatus);
        await Scan(vm);
        Assert.Equal("1 saved layout could not be read.", vm.TemplateStatus);
        await Run(vm, () => vm.SaveTemplateCommand.Execute(null));
        Assert.StartsWith("Saved the layout", vm.TemplateStatus);
        await Scan(vm);
        Assert.StartsWith("Saved the layout", vm.TemplateStatus);
    }

    [Fact]
    public async Task ScanningAgain_HidesTheBanner_UntilTheNewSuggestion()
    {
        var store = Store();
        await SavedI01TemplateAsync(store);
        var vm = Studio(CopyOfI01(), store);
        await Scan(vm);
        Assert.True(vm.IsBannerVisible);

        vm.ScanCommand.Execute(null);
        Assert.False(vm.IsBannerVisible);
        Assert.All(vm.Templates, r => Assert.Equal(string.Empty, r.Match));
        await TestTimeout.Run(() => vm.ScanTask!, TimeSpan.FromSeconds(60));
        await TestTimeout.Run(() => vm.SuggestTask!, TimeSpan.FromSeconds(30));
        Assert.True(vm.IsBannerVisible);

        vm.Mode = FileIntakeNodeSettings.MarkersMode;
        Assert.False(vm.IsBannerVisible);
    }

    [Fact]
    public async Task CancelDuringApply_LeavesTheLayoutUnchanged()
    {
        var store = Store();
        await SavedI01TemplateAsync(store);
        var vm = Studio(CopyOfI01(), store);
        await Scan(vm);
        var before = vm.Containers.Select(r => r.Info).ToList();
        vm.SelectedTemplate = vm.Templates.Single();

        vm.ApplyTemplateCommand.Execute(null);
        Assert.True(vm.IsTemplateBusy);
        Assert.False(vm.CanEdit);
        AssertTemplateCommands(vm, false);
        vm.Cancel();
        await TestTimeout.Run(() => vm.TemplateTask!, TimeSpan.FromSeconds(30));

        Assert.False(vm.Session!.IsModified);
        Assert.Equal(before, vm.Containers.Select(r => r.Info));
        Assert.Equal(IntakeStudioViewModel.TemplateCancelledText, vm.TemplateStatus);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task ApplyButton_ForATemplateThatDoesNotFit_AsksFirst()
    {
        var store = Store();
        var json = Studio(CopyOfI01("{\n  \"a\": 1\n}\n"), store);
        await Scan(json);
        json.TemplateName = "Json only";
        await Run(json, () => json.SaveTemplateCommand.Execute(null));

        var questions = new List<string>();
        var vm = Studio(CopyOfI01(), store, confirm: q =>
        {
            questions.Add(q);
            return false;
        });
        await Scan(vm);
        vm.SelectedTemplate = vm.Templates.Single();
        Assert.True(vm.ApplyTemplateCommand.CanExecute(null));
        await Run(vm, () => vm.ApplyTemplateCommand.Execute(null));
        Assert.Equal(IntakeStudioViewModel.ApplyAnywayQuestion, questions.Single());
        Assert.False(vm.Session!.IsModified);

        var yes = Studio(CopyOfI01(), store, confirm: _ => true);
        await Scan(yes);
        yes.SelectedTemplate = yes.Templates.Single();
        await Run(yes, () => yes.ApplyTemplateCommand.Execute(null));
        Assert.Equal("The saved layout 'Json only' does not fit this file, so nothing was changed.", yes.TemplateStatus);
        Assert.False(yes.Session!.IsModified);
    }

    /// <summary>Scan, suggested, applied, undone, saved plan, dismissed, deleted: the order in the lineage.</summary>
    [Fact]
    public async Task Lineage_Order_ForAScenario()
    {
        var store = Store();
        var (_, lastRow) = await SavedI01TemplateAsync(store);
        var lineage = new IntakeStudioOpenTests.MemoryLineage();
        var vm = Studio(I01WithMoreRows(lastRow), store, lineage, confirm: _ => true);
        await Scan(vm);
        await Run(vm, () => vm.ApplyBannerCommand.Execute(null));
        vm.UndoCommand.Execute(null);
        vm.RedoCommand.Execute(null);
        vm.SaveCommand.Execute(null);
        vm.RevertCommand.Execute(null);
        vm.SaveCommand.Execute(null);
        await Scan(vm);
        Assert.True(vm.IsBannerVisible);
        vm.DismissBannerCommand.Execute(null);
        vm.SelectedTemplate = vm.Templates.Single();
        await Run(vm, () => vm.DeleteTemplateCommand.Execute(null));

        string[] wanted = ["intake.scanned", "template.suggested", "template.applied", "edit.undone", "plan.saved", "intake.scanned", "template.suggested", "template.dismissed", "template.deleted"];
        var events = lineage.Events.Select(e => e.Event).ToList();
        _output.WriteLine(string.Join(", ", events));
        var at = 0;
        foreach (var name in wanted)
        {
            at = events.IndexOf(name, at);
            Assert.True(at >= 0, $"{name} missing or out of order in: {string.Join(", ", events)}");
            at++;
        }

        Assert.All(lineage.Events.Where(e => e.Event.StartsWith("template.")), e => Assert.DoesNotContain("Results table", string.Join(" ", e.Details.Values)));
    }
}

/// <summary>The shared template store in the test run: configured to a temp folder, never next to the test program.</summary>
[Collection(NodeFactoryHandlerCollection.Name)]
public sealed class AppTemplatesTests
{
    [Fact]
    public async Task AMainViewModelWithoutAStore_SavesToTheTestFolder_NotNextToTheTestProgram()
    {
        var dialogs = new ProjectEditorTests.FakeDialogs();
        var main = new MainViewModel(dialogs, new ProjectEditorTests.MemoryRecent(), new IntakeStudioOpenTests.MemoryLineage());
        main.Editor.AddNode(NodeType.FileIntake, new Point(10, 20));
        var node = main.Editor.Nodes.Last();
        node.SetFileIntakeSettings(new FileIntakeNodeSettings { Path = IntakeTestData.PathOf("i01_sample") });
        var name = "shared " + Guid.NewGuid().ToString("N");

        dialogs.OnIntakeStudio = vm => Task.Run(async () =>
        {
            await vm.ScanAsync();
            await (vm.SuggestTask ?? Task.CompletedTask);
            vm.TemplateName = name;
            vm.SaveTemplateCommand.Execute(null);
            await vm.TemplateTask!;
        }).Wait(TimeSpan.FromSeconds(30));
        main.ShowIntakeStudio(node);

        Assert.Equal(TestTemplateFolder.Folder, AppTemplates.Shared.Folder);
        Assert.Contains(Directory.GetFiles(TestTemplateFolder.Folder, "*.json"), f => File.ReadAllText(f).Contains(name));
        Assert.False(Directory.Exists(TemplateLocation.DefaultFolder()), "A templates folder was made next to the test program.");
        await Task.CompletedTask;
    }
}
