using System.ComponentModel;
using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>G5b2 step 3: changing Mode, Encoding or the file asks before unsaved layout edits are lost. Lineage goes to a temp folder.</summary>
public sealed class IntakeStudioDiscardTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("intake-discard-").FullName;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private TextFileLineageLog Lineage() => new(new LineageSettings { Folder = _folder });

    private IntakeStudioViewModel Studio(Func<string, bool> confirm, ILineageLog? lineage = null)
        => new(new FileIntakeNodeSettings { Path = IntakeTestData.PathOf("i01_sample") }, lineage: lineage ?? Lineage(), confirm: confirm);

    /// <summary>i01 scanned and one part renamed: the layout has unsaved edits.</summary>
    private static async Task WithAnEdit(IntakeStudioViewModel vm)
    {
        await IntakeStudioEditTests.Scan(vm);
        vm.SelectedContainer = vm.Containers.First(r => r.Info.Format == "csv");
        await TestTimeout.Run(() => vm.PreviewTask!, TimeSpan.FromSeconds(30));
        vm.NewName = "Results";
        vm.RenameCommand.Execute(null);
        Assert.True(vm.Session!.IsModified);
    }

    public static TheoryData<string> Settings => new() { "mode", "encoding", "file" };

    private static string NewValue(string setting) => setting switch
    {
        "mode" => FileIntakeNodeSettings.MarkersMode,
        "encoding" => "utf-8",
        _ => IntakeTestData.PathOf("i02_crlf")
    };

    private static void Change(IntakeStudioViewModel vm, string setting, string value)
    {
        switch (setting)
        {
            case "mode": vm.Mode = value; break;
            case "encoding": vm.Encoding = value; break;
            default: vm.FilePath = value; break;
        }
    }

    private static string Read(IntakeStudioViewModel vm, string setting) => setting switch
    {
        "mode" => vm.Mode,
        "encoding" => vm.Encoding,
        _ => vm.FilePath
    };

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Change_WithUnsavedEdits_AnswerNo_KeepsTheOldValue_AndTheEdits(string setting)
    {
        var questions = new List<string>();
        var vm = Studio(q => { questions.Add(q); return false; });
        await WithAnEdit(vm);
        var old = Read(vm, setting);
        var session = vm.Session;
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Change(vm, setting, NewValue(setting));

        Assert.Equal([IntakeStudioViewModel.DiscardQuestion], questions);
        Assert.Equal(old, Read(vm, setting));
        Assert.Same(session, vm.Session);
        Assert.Contains(vm.Containers, r => r.Info.Name == "Results");
        // The box is told to show the kept value again.
        Assert.Contains(setting switch { "mode" => "Mode", "encoding" => "Encoding", _ => "FilePath" }, raised);

        // Setting the kept value again (what the box does after the refusal) asks nothing.
        Change(vm, setting, old);
        Assert.Single(questions);
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Change_WithUnsavedEdits_AnswerYes_ChangesTheValue_AndDropsTheEdits(string setting)
    {
        var questions = new List<string>();
        var vm = Studio(q => { questions.Add(q); return true; });
        await WithAnEdit(vm);

        Change(vm, setting, NewValue(setting));

        Assert.Equal([IntakeStudioViewModel.DiscardQuestion], questions);
        Assert.Equal(NewValue(setting), Read(vm, setting));
        Assert.Null(vm.Session);
        Assert.Empty(vm.Containers);
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Change_WithoutEdits_AsksNothing(string setting)
    {
        var questions = new List<string>();
        var vm = Studio(q => { questions.Add(q); return false; });
        await IntakeStudioEditTests.Scan(vm);

        Change(vm, setting, NewValue(setting));

        Assert.Empty(questions);
        Assert.Equal(NewValue(setting), Read(vm, setting));
    }

    [Fact]
    public async Task Change_AfterTheEditsWereSaved_AsksNothing()
    {
        var questions = new List<string>();
        var vm = new IntakeStudioViewModel(new FileIntakeNodeSettings { Path = IntakeTestData.PathOf("i01_sample") },
            lineage: Lineage(), confirm: q => { questions.Add(q); return false; }, savePlan: _ => { });
        await WithAnEdit(vm);
        vm.SaveCommand.Execute(null);

        vm.Mode = FileIntakeNodeSettings.MarkersMode;

        Assert.Empty(questions);
        Assert.Equal(FileIntakeNodeSettings.MarkersMode, vm.Mode);
    }
}
