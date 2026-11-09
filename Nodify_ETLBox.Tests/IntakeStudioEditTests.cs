using System.Diagnostics;
using System.Text.Json;
using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.Services;
using EtlDesigner.UI.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>The review tools of Intake Studio, without a window (G5b step 1). Lineage goes to a temp folder.</summary>
public sealed class IntakeStudioEditTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("intake-edit-").FullName;
    private readonly ITestOutputHelper _output;

    public IntakeStudioEditTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private TextFileLineageLog Lineage() => new(new LineageSettings { Folder = _folder });

    private IntakeStudioViewModel Studio(string? path = null, string mode = FileIntakeNodeSettings.AutoDetect, ILineageLog? lineage = null,
        string? plan = null, Action<string?>? savePlan = null, Func<string, bool>? confirm = null, Func<UnsavedChangesChoice>? askUnsaved = null)
        => new(new FileIntakeNodeSettings { Path = path ?? IntakeTestData.PathOf("i01_sample"), Mode = mode },
            askUnsaved: askUnsaved, lineage: lineage ?? Lineage(), savedPlanJson: plan, savePlan: savePlan, confirm: confirm);

    internal static async Task Scan(IntakeStudioViewModel vm)
    {
        vm.ScanCommand.Execute(null);
        await TestTimeout.Run(() => vm.ScanTask!, TimeSpan.FromSeconds(60));
    }

    private static async Task Select(IntakeStudioViewModel vm, IntakeContainerRow row)
    {
        vm.SelectedContainer = row;
        await TestTimeout.Run(() => vm.PreviewTask!, TimeSpan.FromSeconds(30));
    }

    /// <summary>The first CSV table of at least 3 lines.</summary>
    private static IntakeContainerRow Table(IntakeStudioViewModel vm)
        => vm.Containers.First(r => r.Info.Format == "csv" && r.Info.ContentLastLine - r.Info.ContentFirstLine >= 2);

    private static IntakeContainerRow TextPart(IntakeStudioViewModel vm) => vm.Containers.First(r => !IntakeEditRules.IsTable(r.Info.Format));

    private static void AssertEditCommands(IntakeStudioViewModel vm, bool enabled)
    {
        foreach (var command in new[] { vm.ChangeFormatCommand, vm.ApplyOptionsCommand, vm.RenameCommand, vm.SplitCommand, vm.MergeCommand, vm.UndoCommand, vm.RedoCommand, vm.RevertCommand })
        {
            Assert.Equal(enabled, command.CanExecute(null));
        }
    }

    private string CopyOfI01()
    {
        var path = Path.Combine(_folder, $"i01-{Guid.NewGuid():N}.txt");
        File.Copy(IntakeTestData.PathOf("i01_sample"), path);
        return path;
    }

    [Fact]
    public async Task EditArea_IsDisabled_BeforeAScan_WhileScanning_AndEnabledAfter()
    {
        var vm = Studio();
        Assert.False(vm.CanEdit);
        AssertEditCommands(vm, false);
        Assert.Null(vm.LayoutText);

        vm.ScanCommand.Execute(null);
        Assert.True(vm.IsScanning);
        Assert.False(vm.CanEdit);
        AssertEditCommands(vm, false);
        await TestTimeout.Run(() => vm.ScanTask!, TimeSpan.FromSeconds(60));

        Assert.True(vm.CanEdit);
        Assert.False(vm.CanEditSelected);
        Assert.Equal("Layout: automatic", vm.LayoutText);
        AssertEditCommands(vm, false);
    }

    [Fact]
    public async Task EnabledRules_TableOptions_Split_Merge_Undo()
    {
        var vm = Studio();
        await Scan(vm);

        var table = Table(vm);
        await Select(vm, table);
        Assert.True(vm.IsTableSelected);
        Assert.True(vm.ApplyOptionsCommand.CanExecute(null));
        Assert.True(vm.ChangeFormatCommand.CanExecute(null));
        Assert.True(vm.RenameCommand.CanExecute(null));
        Assert.True(vm.MergeCommand.CanExecute(null));
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.RedoCommand.CanExecute(null));
        Assert.False(vm.RevertCommand.CanExecute(null));
        Assert.Equal("csv", vm.SelectedFormat);
        Assert.NotNull(vm.SelectedDelimiter);

        // Split: only strictly inside the part, never at its first line; the tooltip says why.
        Assert.False(vm.SplitCommand.CanExecute(null));
        Assert.StartsWith("Click a line in the Preview", vm.SplitToolTip);
        var first = table.Info.ContentFirstLine!.Value;
        var last = table.Info.ContentLastLine!.Value;
        foreach (var bad in new[] { first, first - 1, last + 1, 0 })
        {
            vm.SplitLine = bad;
            Assert.False(vm.SplitCommand.CanExecute(null));
            Assert.StartsWith("Choose a line from", vm.SplitToolTip);
        }

        vm.SplitLineText = "abc";
        Assert.False(vm.SplitCommand.CanExecute(null));
        vm.SplitLine = first + 1;
        Assert.True(vm.SplitCommand.CanExecute(null));
        vm.SplitLine = last;
        Assert.True(vm.SplitCommand.CanExecute(null));

        // A clicked Preview line fills the split line.
        vm.SelectedPreviewLine = vm.PreviewRows[1];
        Assert.Equal(vm.PreviewRows[1].LineNumber, vm.SplitLine);
        Assert.True(vm.SplitCommand.CanExecute(null));

        // Table options only for tables; merge not for the last part.
        await Select(vm, TextPart(vm));
        Assert.False(vm.IsTableSelected);
        Assert.False(vm.ApplyOptionsCommand.CanExecute(null));
        Assert.True(vm.RenameCommand.CanExecute(null));
        Assert.Equal(string.Empty, vm.SplitLineText);

        await Select(vm, vm.Containers[^1]);
        Assert.False(vm.MergeCommand.CanExecute(null));
    }

    [Fact]
    public async Task EachCommand_ChangesTheList_AndKeepsTheSelectionOnTheAffectedPart()
    {
        var vm = Studio();
        await Scan(vm);
        var count = vm.Containers.Count;
        var totals = vm.TotalsText;
        var table = Table(vm);
        var index = table.Number - 1;

        // Split at the second line of the table: one more row, the first part selected.
        await Select(vm, table);
        var line = table.Info.ContentFirstLine!.Value + 1;
        vm.SplitLine = line;
        vm.SplitCommand.Execute(null);
        Assert.Equal(count + 1, vm.Containers.Count);
        Assert.Equal(index, vm.SelectedContainer!.Number - 1);
        Assert.Equal(table.Info.ContentFirstLine, vm.SelectedContainer.Info.ContentFirstLine);
        var at = vm.Containers[index + 1].Info.ContentFirstLine!.Value;
        Assert.True(at >= line, $"The second part starts at {at}, before the split line {line}.");
        Assert.Equal(FormattableString.Invariant($"Split part {index + 1} at line {at}."), vm.EditStatus);
        Assert.Equal(vm.Containers.Select((r, i) => i + 1), vm.Containers.Select(r => r.Number));
        Assert.StartsWith(IntakeText.Count(count + 1, "container"), vm.TotalsText);
        Assert.NotEqual(totals, vm.TotalsText);
        Assert.Equal("Layout: edited by you (1 change)", vm.LayoutText);
        Assert.Contains(vm.Details, d => d.Label == "Layout" && d.Value == IntakeStudioViewModel.SetByYouText);

        // Merge it back: the merged part is selected.
        vm.MergeCommand.Execute(null);
        Assert.Equal(count, vm.Containers.Count);
        Assert.Equal(index, vm.SelectedContainer!.Number - 1);
        Assert.Equal(table.Info.ContentLastLine, vm.SelectedContainer.Info.ContentLastLine);

        // Rename: only that row is a new object.
        var before = vm.Containers.ToList();
        vm.NewName = "Results";
        vm.RenameCommand.Execute(null);
        Assert.Equal("Results", vm.SelectedContainer!.Info.Name);
        Assert.EndsWith(": Results", vm.SelectedContainer.Part);
        Assert.Equal(index, vm.SelectedContainer.Number - 1);
        Assert.Equal(before.Count - 1, before.Zip(vm.Containers).Count(p => ReferenceEquals(p.First, p.Second)));

        // Table options.
        vm.SelectedDelimiter = "semicolon";
        vm.SelectedHeader = "No";
        vm.SkipLinesText = "1";
        vm.ApplyOptionsCommand.Execute(null);
        Assert.Equal(";", vm.SelectedContainer!.Info.Delimiter);
        Assert.Equal(HeaderGuess.No, vm.SelectedContainer.Info.HeaderGuess);
        Assert.Equal(1, vm.SelectedContainer.Info.SkipLines);
        Assert.Equal($"Options of part {index + 1} changed.", vm.EditStatus);

        // Change format.
        vm.SelectedFormat = "text";
        vm.ChangeFormatCommand.Execute(null);
        Assert.Equal("text", vm.SelectedContainer!.Info.Format);
        Assert.Equal($"Part {index + 1} is now Text.", vm.EditStatus);
        Assert.False(vm.IsTableSelected);
        Assert.Equal("Layout: edited by you (5 changes)", vm.LayoutText);
    }

    [Fact]
    public async Task AFailedOperation_ShowsTheEnginesMessage_AndChangesNothing()
    {
        var vm = Studio();
        await Scan(vm);
        await Select(vm, Table(vm));
        var rows = vm.Containers.ToList();
        var selected = vm.SelectedContainer;

        vm.NewName = "  ";
        vm.RenameCommand.Execute(null);
        Assert.False(string.IsNullOrWhiteSpace(vm.EditStatus));
        Assert.Equal(rows, vm.Containers);
        Assert.Same(selected, vm.SelectedContainer);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.HasUnsavedChanges);

        vm.SkipLinesText = "999999";
        vm.ApplyOptionsCommand.Execute(null);
        Assert.Contains("skip", vm.EditStatus!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(rows, vm.Containers);

        vm.SkipLinesText = "two";
        vm.ApplyOptionsCommand.Execute(null);
        Assert.Equal("Skip lines must be a whole number, 0 or more.", vm.EditStatus);
        Assert.Equal(rows, vm.Containers);
    }

    [Fact]
    public async Task UndoRedoRevert_RestoreTheRows_AndSelectThePartThatChanged()
    {
        var vm = Studio();
        await Scan(vm);
        var original = vm.Containers.Select(r => (r.Info.ContentFirstLine, r.Info.ContentLastLine, r.Info.Format)).ToList();
        var table = Table(vm);
        await Select(vm, table);
        vm.SplitLine = table.Info.ContentFirstLine!.Value + 1;
        vm.SplitCommand.Execute(null);
        var afterSplit = vm.Containers.Select(r => (r.Info.ContentFirstLine, r.Info.ContentLastLine, r.Info.Format)).ToList();

        await Select(vm, vm.Containers[0]);
        vm.UndoCommand.Execute(null);
        Assert.Equal(original, vm.Containers.Select(r => (r.Info.ContentFirstLine, r.Info.ContentLastLine, r.Info.Format)));
        Assert.Equal(table.Number, vm.SelectedContainer!.Number);
        Assert.Equal("Undid the last change.", vm.EditStatus);
        Assert.True(vm.RedoCommand.CanExecute(null));
        Assert.False(vm.RevertCommand.CanExecute(null));
        Assert.Equal("Layout: automatic", vm.LayoutText);

        vm.RedoCommand.Execute(null);
        Assert.Equal(afterSplit, vm.Containers.Select(r => (r.Info.ContentFirstLine, r.Info.ContentLastLine, r.Info.Format)));
        Assert.Equal(table.Number, vm.SelectedContainer!.Number);

        vm.RevertCommand.Execute(null);
        Assert.Equal(original, vm.Containers.Select(r => (r.Info.ContentFirstLine, r.Info.ContentLastLine, r.Info.Format)));
        Assert.Equal("Back to the detected result.", vm.EditStatus);
        Assert.Equal("Layout: automatic", vm.LayoutText);
        Assert.True(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Dirty_SaveWritesAPlan_OpeningTheSavedLayoutAndClosingIsNotDirty()
    {
        string? saved = null;
        var saves = 0;
        var vm = Studio(savePlan: json => { saved = json; saves++; });
        await Scan(vm);
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.SaveCommand.CanExecute(null));

        var table = Table(vm);
        await Select(vm, table);
        vm.SplitLine = table.Info.ContentFirstLine!.Value + 1;
        vm.SplitCommand.Execute(null);
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.SaveCommand.CanExecute(null));

        // Undo back to the detected result: nothing to save.
        vm.UndoCommand.Execute(null);
        Assert.False(vm.HasUnsavedChanges);
        vm.RedoCommand.Execute(null);
        Assert.True(vm.HasUnsavedChanges);

        vm.SaveCommand.Execute(null);
        Assert.Equal(1, saves);
        Assert.NotNull(saved);
        Assert.False(vm.HasUnsavedChanges);
        var layout = vm.Containers.Select(r => (r.Info.ContentFirstLine, r.Info.ContentLastLine)).ToList();

        // Open again with the saved layout: the same parts, not dirty, closing does not ask.
        var asked = 0;
        var reopened = Studio(plan: saved, askUnsaved: () => { asked++; return UnsavedChangesChoice.Cancel; });
        await Scan(reopened);
        Assert.Null(reopened.EditStatus);
        Assert.Equal(layout, reopened.Containers.Select(r => (r.Info.ContentFirstLine, r.Info.ContentLastLine)));
        Assert.Equal("Layout: edited by you (saved layout)", reopened.LayoutText);
        Assert.False(reopened.HasUnsavedChanges);
        Assert.True(reopened.ConfirmClose());
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task UnmodifiedLayout_StoresNoPlan_RevertOfASavedLayoutRemovesIt()
    {
        var calls = new List<string?>();
        var vm = Studio(mode: FileIntakeNodeSettings.AutoDetect, savePlan: calls.Add);
        await Scan(vm);
        vm.Encoding = IntakeEncodings.Utf8; // a settings change only
        await Scan(vm);
        vm.SaveCommand.Execute(null);
        Assert.Empty(calls);

        // A saved layout, reverted and saved: the plan is removed.
        var edited = Studio(savePlan: calls.Add);
        await Scan(edited);
        await Select(edited, Table(edited));
        edited.NewName = "Results";
        edited.RenameCommand.Execute(null);
        edited.SaveCommand.Execute(null);
        var plan = Assert.Single(calls);
        Assert.NotNull(plan);

        var reopened = Studio(plan: plan, savePlan: calls.Add);
        await Scan(reopened);
        Assert.True(reopened.RevertCommand.CanExecute(null));
        reopened.RevertCommand.Execute(null);
        Assert.True(reopened.HasUnsavedChanges);
        reopened.SaveCommand.Execute(null);
        Assert.Equal(2, calls.Count);
        Assert.Null(calls[1]);
        Assert.Null(reopened.SavedPlanJson);
        Assert.False(reopened.HasUnsavedChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanAgain_WithUnsavedEdits_AsksFirst_NoKeepsTheSession(bool answer)
    {
        var questions = new List<string>();
        var vm = Studio(confirm: q => { questions.Add(q); return answer; });
        await Scan(vm);

        // Not modified: no question.
        await Scan(vm);
        Assert.Empty(questions);

        await Select(vm, Table(vm));
        vm.NewName = "Results";
        vm.RenameCommand.Execute(null);
        var session = vm.Session;
        var task = vm.ScanTask;

        vm.ScanCommand.Execute(null);
        Assert.Equal([IntakeStudioViewModel.RescanQuestion], questions);
        if (answer)
        {
            await TestTimeout.Run(() => vm.ScanTask!, TimeSpan.FromSeconds(60));
            Assert.NotSame(session, vm.Session);
            Assert.False(vm.Session!.IsModified);
            Assert.DoesNotContain(vm.Containers, r => r.Info.Name == "Results");
        }
        else
        {
            Assert.Same(task, vm.ScanTask);
            Assert.Same(session, vm.Session);
            Assert.Contains(vm.Containers, r => r.Info.Name == "Results");
        }
    }

    [Fact]
    public async Task SavedPlan_OfAChangedFile_IsRefused_WithTheMessage_AndTheAutomaticResultShows()
    {
        var path = CopyOfI01();
        string? plan = null;
        var vm = Studio(path, savePlan: json => plan = json);
        await Scan(vm);
        var automatic = vm.Containers.Select(r => (r.Info.ContentFirstLine, r.Info.ContentLastLine)).ToList();
        await Select(vm, Table(vm));
        vm.NewName = "Results";
        vm.RenameCommand.Execute(null);
        vm.SaveCommand.Execute(null);
        Assert.NotNull(plan);

        File.AppendAllText(path, "\n");
        var reopened = Studio(path, plan: plan);
        await Scan(reopened);
        Assert.Equal($"{IntakePlan.ChangedFileMessage} {IntakeStudioViewModel.AutomaticResultText}", reopened.EditStatus);
        Assert.Equal("Layout: automatic", reopened.LayoutText);
        Assert.DoesNotContain(reopened.Containers, r => r.Info.Name == "Results");
        Assert.Equal(automatic.Count, reopened.Containers.Count);
        Assert.False(reopened.HasUnsavedChanges);
        Assert.Equal(plan, reopened.SavedPlanJson);
    }

    [Theory]
    [InlineData("{\"formatVersion\": 1, \"sha256\": 5}")]
    [InlineData("\"not a layout\"")]
    [InlineData("{}")]
    public async Task DamagedPlan_ShowsItCouldNotBeRead_AndTheAutomaticResult(string json)
    {
        var vm = Studio(plan: json);
        await Scan(vm);
        Assert.Equal($"{IntakeStudioViewModel.UnreadablePlanText} {IntakeStudioViewModel.AutomaticResultText}", vm.EditStatus);
        Assert.Equal("Layout: automatic", vm.LayoutText);
        Assert.Equal(27, vm.Containers.Count);
    }

    [Fact]
    public async Task Lineage_EventsAreWrittenInOrder_ToTheTempFile()
    {
        var lineage = Lineage();
        string? plan = null;
        var vm = Studio(lineage: lineage, savePlan: json => plan = json);
        await Scan(vm);
        var table = Table(vm);
        await Select(vm, table);
        vm.SplitLine = table.Info.ContentFirstLine!.Value + 1;
        vm.SplitCommand.Execute(null);
        vm.MergeCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        vm.SaveCommand.Execute(null);
        var reopened = Studio(lineage: lineage, plan: plan, confirm: _ => true);
        await Scan(reopened);
        // G5b2: an edit thrown away by Scan again (answered Yes) is recorded before the new scan.
        await Select(reopened, Table(reopened));
        reopened.NewName = "Thrown away";
        reopened.RenameCommand.Execute(null);
        await Scan(reopened);

        Assert.NotNull(lineage.FilePath);
        Assert.StartsWith(_folder, lineage.FilePath);
        var events = File.ReadAllLines(lineage.FilePath!).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("event").GetString()).ToList();
        Assert.Equal(["intake.scanned", "edit.split", "edit.merged", "edit.undone", "plan.saved", "intake.scanned", "plan.applied",
            "edit.renamed", "edit.discarded", "intake.scanned", "plan.applied"], events);
        Assert.Null(vm.LineageNote);
    }

    [Fact]
    public async Task LargeFile_36001Parts_RenameTouchesOneRow_SplitTiming()
    {
        var path = Path.Combine(_folder, "large.txt");
        var bytes = File.ReadAllBytes(IntakeTestData.PathOf("i01_sample"));
        using (var stream = File.Create(path))
        {
            for (var i = 0; i < 2000; i++)
            {
                stream.Write(bytes);
            }
        }

        var vm = Studio(path, FileIntakeNodeSettings.MarkersMode, lineage: new TextFileLineageLog(new LineageSettings { Folder = _folder }));
        var clock = Stopwatch.StartNew();
        await Scan(vm);
        _output.WriteLine($"scan and list: {clock.ElapsedMilliseconds} ms, {vm.Containers.Count} rows");
        Assert.Equal(36001, vm.Containers.Count);

        var middle = vm.Containers.Skip(18000).First(r => r.Info.ContentLastLine - r.Info.ContentFirstLine >= 2);
        await Select(vm, middle);
        var list = vm.Containers;
        var before = list.ToList();

        clock.Restart();
        vm.NewName = "Renamed";
        vm.RenameCommand.Execute(null);
        var rename = clock.Elapsed.TotalMilliseconds;
        Assert.Same(list, vm.Containers);
        Assert.Equal(36000, before.Zip(vm.Containers).Count(p => ReferenceEquals(p.First, p.Second)));

        vm.SplitLine = middle.Info.ContentFirstLine!.Value + 1;
        clock.Restart();
        vm.SplitCommand.Execute(null);
        var split = clock.Elapsed.TotalMilliseconds;
        Assert.Equal(36002, vm.Containers.Count);

        clock.Restart();
        vm.UndoCommand.Execute(null);
        var undo = clock.Elapsed.TotalMilliseconds;
        Assert.Equal(36001, vm.Containers.Count);

        _output.WriteLine($"rename {rename:0.0} ms, split with list rebuild {split:0.0} ms, undo with list rebuild {undo:0.0} ms");
        Assert.True(rename < 1000, $"Rename took {rename:0} ms.");
        Assert.True(split < 5000, $"Split took {split:0} ms.");
    }
}
