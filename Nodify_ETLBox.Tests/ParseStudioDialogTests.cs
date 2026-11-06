using EtlDesigner.Engine.Parsing;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

// Members added for the ParseValue dialog (Task C2).
public partial class ParseStudioViewModelTests
{
    private static GridSelection Cells(params (int Row, string Column)[] cells)
        => new(cells.Select(c => new GridCell(c.Row, c.Column)).ToList(), null);

    [Fact]
    public async Task Load_FillsRowsView_StatusText_FileName()
    {
        var (vm, _, parse) = await LoadedC01();

        Assert.Equal(4, vm.RowsView!.Count);
        Assert.Equal("#", vm.RowsView.Table!.Columns[0].ColumnName);
        Assert.Equal(vm.Columns.Count + 1, vm.RowsView.Table.Columns.Count);
        Assert.StartsWith("Showing 4 rows · 32 columns · ", vm.StatusText);
        Assert.Equal("c01_basic.csv", vm.FileName);
        Assert.Equal(parse.Title, vm.NodeTitle);
        Assert.Null(vm.GridMessage);
    }

    [Fact]
    public async Task GridSelection_DistinctColumnsInFileOrder_UseAsSource_UseForSlot()
    {
        var (vm, _, _) = await LoadedC01();

        vm.GridSelectionChangedCommand.Execute(Cells((1, "r03"), (0, "r01"), (2, "r03"), (0, "nowhere")));
        Assert.Equal(["r01", "r03"], vm.SelectedGridColumns);
        Assert.Equal("r03", vm.CurrentColumn);   // the last valid cell when the grid gives no current cell
        Assert.Equal(2, vm.SelectedRowIndex);

        vm.UseGridSelectionCommand.Execute(vm.Slots[1]);
        Assert.Equal(["r01", "r03"], vm.Slots[1].Columns);

        vm.GridSelectionChangedCommand.Execute(new GridSelection([new GridCell(0, "sample")], new GridCell(0, "sample")));
        Assert.True(vm.UseAsSourceCommand.CanExecute(null));
        vm.UseAsSourceCommand.Execute(null);
        Assert.Equal("sample", vm.SourceColumn);
    }

    [Fact]
    public async Task UseGridSelection_DisabledWithoutSelection()
    {
        var (vm, _, _) = await LoadedC01();
        Assert.False(vm.UseGridSelectionCommand.CanExecute(vm.Slots[0]));
        Assert.False(vm.UseAsSourceCommand.CanExecute(null));
    }

    [Fact]
    public async Task DisplayedCell_SourceCellOfSelectedRow_WithSegments_AndComponentRows()
    {
        var (vm, _, _) = await LoadedC01();
        Assert.Equal(ParseStudioViewModel.NothingSelectedText, vm.DisplayedCellCaption);

        vm.SourceColumn = "sample";
        vm.SelectedRowIndex = 1;

        Assert.Equal("Row 2 · column \"sample\"", vm.DisplayedCellCaption);
        Assert.Equal("S101", vm.DisplayedCellSegments[0].Text);
        Assert.Equal(SegmentKind.Key, vm.DisplayedCellSegments[0].Kind);
        Assert.Equal(6, vm.ComponentRows.Count);
        Assert.Equal(new ComponentRow(6, "compid6", ""), vm.ComponentRows[5]);

        // A clicked cell in another column is shown instead.
        vm.SetGridSelection(new GridSelection([new GridCell(0, "name")], new GridCell(0, "name")));
        Assert.Equal("Person 1", vm.DisplayedCellText);
    }

    [Fact]
    public async Task AutoFill_UsesGridSelection()
    {
        var (vm, _, _) = await LoadedC01();
        vm.SourceColumn = "sample";
        vm.AutoFillGroupSize = 2;

        vm.SetGridSelection(Cells((0, "r04"), (0, "r01"), (0, "r02")));
        vm.AutoFillCommand.Execute(null);
        Assert.Equal(2, vm.Slots.Count);
        Assert.Equal(["r01", "r02"], vm.Slots[0].Columns);
        Assert.Equal(["r04"], vm.Slots[1].Columns);
    }

    [Fact]
    public async Task AutoFill_WithNoGridSelection_IsDisabled_ShowsTheHint_AndChangesNoSlot()
    {
        var (vm, _, _) = await LoadedC01();
        vm.SourceColumn = "sample";
        var slotsBefore = vm.Slots.Select(s => s.Columns.ToList()).ToList();

        Assert.False(vm.AutoFillCommand.CanExecute(null));
        Assert.Equal(
            "Select the value columns in the grid first (for example by Shift-clicking from the first to the last), then press Auto-fill.",
            vm.AutoFillHint);

        vm.AutoFillFromSelection();
        Assert.Equal(slotsBefore, vm.Slots.Select(s => s.Columns.ToList()).ToList());

        vm.SetGridSelection(Cells((0, "r01")));
        Assert.True(vm.AutoFillCommand.CanExecute(null));
        Assert.Null(vm.AutoFillHint);

        vm.SetGridSelection(Cells());
        Assert.False(vm.AutoFillCommand.CanExecute(null));
        Assert.NotNull(vm.AutoFillHint);
    }

    [Fact]
    public async Task SlotChoices_TickInFileOrder_AndFollowTheSlot()
    {
        var (vm, _, _) = await LoadedC01();
        var slot = vm.Slots[0];
        Assert.Equal(vm.Columns, slot.Choices.Select(c => c.Name));

        slot.Choices.Single(c => c.Name == "r05").IsChecked = true;
        slot.Choices.Single(c => c.Name == "r02").IsChecked = true;
        Assert.Equal(["r02", "r05"], slot.Columns);

        vm.AssignColumnsToSlot(0, ["r07"]);
        Assert.Equal(["r07"], slot.Choices.Where(c => c.IsChecked).Select(c => c.Name));

        vm.AddSlot();
        Assert.Equal(vm.Columns.Count, vm.Slots[^1].Choices.Count);
    }

    [Fact]
    public async Task CarryChoices_EditCarryNames()
    {
        var (vm, _, _) = await LoadedC01();
        vm.CarryMode = CarryChoice.List;

        vm.CarryChoices.Single(c => c.Name == "batch").IsChecked = true;
        vm.CarryChoices.Single(c => c.Name == "id").IsChecked = true;

        Assert.Equal(["id", "batch"], vm.CarryNames);
        Assert.Equal(CarryColumns.Of("id", "batch").Names, vm.BuildSettings().CarryColumns.Names);
    }

    [Fact]
    public async Task Cancel_WithoutChanges_Closes_WithChanges_AsksFirst()
    {
        var (vm, _, parse) = await LoadedC01();
        var before = parse.ParseValueSettings;
        var closes = new List<bool>();
        vm.CloseRequested += (_, saved) => closes.Add(saved);

        Assert.False(vm.HasChanges);
        vm.CancelCommand.Execute(null);
        Assert.Equal([false], closes);

        vm.Trim = !vm.Trim;
        Assert.True(vm.HasChanges);
        vm.CancelCommand.Execute(null);
        Assert.True(vm.ShowDiscardPrompt);
        Assert.Single(closes);

        vm.KeepEditingCommand.Execute(null);
        Assert.False(vm.ShowDiscardPrompt);

        vm.CancelCommand.Execute(null);
        vm.DiscardCommand.Execute(null);
        Assert.Equal([false, false], closes);
        Assert.Same(before, parse.ParseValueSettings);
    }

    [Fact]
    public async Task Save_WritesTheNode_AndRequestsClose()
    {
        var (vm, _, parse) = await LoadedC01();
        var closes = new List<bool>();
        vm.CloseRequested += (_, saved) => closes.Add(saved);

        vm.SourceColumn = "sample";
        vm.SaveCommand.Execute(null);

        Assert.Equal("sample", parse.ParseValueSettings!.SourceColumn);
        Assert.Equal([true], closes);
    }

    [Fact]
    public async Task TestParsing_OpensResult_FillsResultView_AndARecordSelectsItsRow()
    {
        var (vm, _, _) = await LoadedC01();
        vm.SourceColumn = "sample";
        vm.AutoFill(R01ToR18, 3);

        vm.TestParsingCommand.Execute(null);

        Assert.Equal(1, vm.SelectedTabIndex);
        Assert.Equal(24, vm.ResultRowsView!.Count);
        Assert.Equal("In 4 · Out 24 · Rejected 0 · Flagged 0", vm.ResultSummary);

        vm.SelectRecordCommand.Execute(new RecordIssue(3, "empty value"));
        Assert.Equal(0, vm.SelectedTabIndex);
        Assert.Equal(2, vm.SelectedRowIndex);
        Assert.Equal(2, vm.FocusRowIndex);

        vm.SelectRecord(99);   // outside the preview: nothing changes
        Assert.Equal(2, vm.SelectedRowIndex);
    }

    [Fact]
    public void WideShape_LabelSaysItComesLater()
        => Assert.Equal("Wide output comes later",
            Studio("c01_basic.csv").Vm.OutputShapes.Single(o => o.Shape == OutputShape.Wide).Label);
}

public partial class ParseStudioViewModelTests
{
    internal static async Task<ParseStudioViewModel> LoadedC01ForOtherTests() => (await LoadedC01()).Vm;
}
