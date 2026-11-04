using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.UI.Services;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

public partial class ParseStudioViewModelTests
{
    private static readonly string[] R01ToR18 = Enumerable.Range(1, 18).Select(i => $"r{i:00}").ToArray();

    [Fact]
    public async Task Load_C01_Gives4Rows_AndKeptColumnsOnly()
    {
        var (vm, csv, _) = Studio("c01_basic.csv", ignore: ["extra_10"]);

        await TestTimeout.Run(() => vm.ReloadNowAsync());

        Assert.Null(vm.PreviewError);
        Assert.Equal(4, vm.Rows.Count);
        Assert.Equal(31, vm.Columns.Count);
        Assert.DoesNotContain("extra_10", vm.Columns);
        Assert.All(vm.Rows, r => Assert.Equal(31, r.Count));
        Assert.Equal("5", csv.Settings.Single(s => s.Key == "PreviewRows").Value);
    }

    [Fact]
    public async Task AutoFill3_OverR01ToR18_Gives6SlotsOf3()
    {
        var (vm, _, _) = await LoadedC01();

        foreach (var c in R01ToR18) vm.AutoFillColumns.Add(c);
        vm.AutoFill(3);

        Assert.Equal(6, vm.Slots.Count);
        Assert.All(vm.Slots, s => Assert.Equal(3, s.Columns.Count));
        Assert.Equal(["r16", "r17", "r18"], vm.Slots[5].Columns);
        Assert.Null(vm.AutoFillError);

        // The result stays editable.
        vm.AssignColumnsToSlot(5, ["r16"]);
        Assert.Equal(["r16"], vm.Slots[5].Columns);
    }

    [Fact]
    public async Task Components_Row1_Has6_AndChangeWithTheDelimiter()
    {
        var (vm, _, _) = await LoadedC01();
        vm.SourceColumn = "sample";
        vm.SelectedRowIndex = 0;

        Assert.Equal("S100:compid1|compid2|compid3|compid4|compid5|compid6", vm.SelectedCellValue);
        Assert.Equal(6, vm.Components.Count);
        Assert.Equal("compid1", vm.Components[0].Name);

        vm.DelimiterLevels[1].Delimiter = ";";
        Assert.Single(vm.Components);
        Assert.Equal("compid1|compid2|compid3|compid4|compid5|compid6", vm.Components[0].Name);

        vm.DelimiterLevels[0].Delimiter = "#";
        Assert.Empty(vm.Components);
        Assert.Equal(CompositeCellParser.NoKeyDelimiter, vm.ComponentsError);
    }

    [Fact]
    public async Task Components_BadLevels_ShowTheParserMessage()
    {
        var (vm, _, _) = await LoadedC01();
        vm.SourceColumn = "sample";

        vm.MoveLevelDown(0);

        Assert.Empty(vm.Components);
        Assert.StartsWith("Level 1 must split once", vm.ComponentsError);
    }

    [Fact]
    public async Task TestParsing_C01_In4_Out24_NoRejects()
    {
        var (vm, _, _) = await LoadedC01();
        vm.SourceColumn = "sample";
        vm.AutoFill(R01ToR18, 3);
        vm.CarryMode = CarryChoice.None;

        vm.TestParsing();

        Assert.Null(vm.TestError);
        Assert.Equal((4, 24, 0, 0), (vm.RowsIn, vm.RowsOut, vm.RowsRejected, vm.RowsFlagged));
        Assert.Equal(24, vm.ResultRows.Count);
        Assert.Equal(vm.OutputColumns, vm.ResultColumns);
        Assert.Equal(["sample_id", "component", "value_a", "value_b", "value_c"], vm.ResultColumns);
    }

    [Fact]
    public async Task TestParsing_C12_ExplicitSlots_In6_Out21()
    {
        // c12 has 6 records, so the CsvFile node shows 10 preview rows.
        var (vm, _, _) = Studio("c12_irregular_slots.csv", previewRows: "10");
        await TestTimeout.Run(() => vm.ReloadNowAsync());
        vm.SourceColumn = "sample";
        vm.CarryMode = CarryChoice.List;
        vm.CarryNames.Add("id");
        string[][] slots =
        [
            ["m01", "m02"], ["m03", "m04", "m05", "m06"], ["m07"], ["m09", "m11", "m13"], ["m14", "m15", "m16"], ["m17", "m18", "m19", "m20"]
        ];
        for (var i = 0; i < slots.Length; i++)
        {
            vm.AssignColumnsToSlot(i, slots[i]);
        }

        vm.TestParsing();

        Assert.Null(vm.TestError);
        Assert.Equal((6, 21, 0, 0), (vm.RowsIn, vm.RowsOut, vm.RowsRejected, vm.RowsFlagged));
        Assert.Equal(["id", "sample_id", "component", "value_a", "value_b", "value_c", "value_d"], vm.OutputColumns);
    }

    [Fact]
    public async Task TestParsing_UnknownSlotColumn_ShowsTheMessage_NoException()
    {
        var (vm, _, _) = await LoadedC01();
        vm.SourceColumn = "sample";
        vm.AssignColumnsToSlot(0, ["zz_new"]);

        vm.TestParsing();

        Assert.Equal("Slot S1 uses the column 'zz_new', which is not in the file. Edit the slot.", vm.TestError);
        Assert.Equal(0, vm.RowsOut);
    }

    [Fact]
    public async Task OutputColumns_FollowCarryLevelsAndSlots()
    {
        var (vm, _, _) = await LoadedC01();
        vm.SourceColumn = "sample";
        vm.AutoFill(R01ToR18, 3);

        vm.CarryMode = CarryChoice.All;
        Assert.Equal(["id", "batch", "name"], vm.OutputColumns.Take(3));
        Assert.Equal("extra_10", vm.OutputColumns[12]);

        vm.CarryMode = CarryChoice.None;
        vm.AddLevel();
        vm.DelimiterLevels[2].SubPartColumn = "revision";
        Assert.Equal(["sample_id", "component", "revision", "value_a", "value_b", "value_c"], vm.OutputColumns);

        vm.RemoveLevel(2);
        vm.AddSlot();
        vm.AssignColumnsToSlot(6, ["r01", "r02", "r03", "r04"]);
        Assert.Equal("value_d", vm.OutputColumns[^1]);
    }

    [Fact]
    public void OutputShape_WideCannotBeChosen()
    {
        var (vm, _, _) = Studio("c01_basic.csv");

        vm.OutputShape = OutputShape.Wide;

        Assert.Equal(OutputShape.Long, vm.OutputShape);
        Assert.False(vm.OutputShapes.Single(o => o.Shape == OutputShape.Wide).IsAvailable);
    }

    [Fact]
    public async Task Save_WritesToNode_Cancel_LeavesNodeUnchanged()
    {
        var (vm, _, parse) = await LoadedC01();
        var before = parse.ParseValueSettings;

        vm.SourceColumn = "sample";
        vm.AutoFill(R01ToR18, 3);
        vm.MoveLevelDown(0);
        vm.CancelCommand.Execute(null);   // what the dialog's Cancel does
        Assert.Same(before, parse.ParseValueSettings);

        var reopened = new ParseStudioViewModel(parse, ParseValueUpstreamResolver.Resolve(parse, []));
        Assert.Equal(string.Empty, reopened.SourceColumn);

        vm.MoveLevelUp(1);
        vm.CarryMode = CarryChoice.List;
        vm.CarryNames.Add("id");
        vm.Save();

        var saved = parse.ParseValueSettings!;
        Assert.Equal("sample", saved.SourceColumn);
        Assert.Equal(6, saved.Slots.Count);
        Assert.Equal(ParseValueSettings.DefaultLevels, saved.DelimiterLevels);
        Assert.Equal(["id"], saved.CarryColumns.Names);
        Assert.Equal("sample", parse.Settings.Single(s => s.Key == "SourceColumn").Value);
    }

    [Fact]
    public async Task OlderCancelledReload_DoesNotStopSpinnerOfNewerReload()
    {
        var reader = new CsvPreviewReader();
        var (vm, _, _) = Studio("c01_basic.csv", reader: reader);
        await TestTimeout.Run(() => vm.ReloadNowAsync());

        using var firstReached = new ManualResetEventSlim();
        using var secondReached = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        var readNumber = 0;
        reader.RowCollected = count =>
        {
            if (count != 1)
            {
                return;
            }

            if (Interlocked.Increment(ref readNumber) == 1)
            {
                firstReached.Set();
                releaseFirst.Wait(TestTimeout.Default);
            }
            else
            {
                secondReached.Set();
                releaseSecond.Wait(TestTimeout.Default);
            }
        };

        var first = vm.ReloadNowAsync();
        Assert.True(firstReached.Wait(TestTimeout.Default), "The first reload never reached its first row.");

        var second = vm.ReloadNowAsync();   // cancels the first
        Assert.True(secondReached.Wait(TestTimeout.Default), "The second reload never reached its first row.");

        releaseFirst.Set();
        await TestTimeout.Run(() => first);
        Assert.True(vm.IsLoading, "The cancelled older reload stopped the spinner of the running newer one.");

        releaseSecond.Set();
        await TestTimeout.Run(() => second);

        Assert.False(vm.IsLoading);
        Assert.Null(vm.PreviewError);
        Assert.Equal(4, vm.Rows.Count);
    }

    [Fact]
    public async Task MissingUpstream_GivesMessage_NoException()
    {
        var editor = new EditorViewModel();
        var parse = ParseValueConnectionTests.Add(editor, NodeType.ParseValue);
        var vm = new ParseStudioViewModel(parse, ParseValueUpstreamResolver.Resolve(parse, editor.Connections));

        await TestTimeout.Run(() => vm.ReloadNowAsync());
        vm.TestParsing();

        Assert.Equal(ParseValueUpstreamResolver.NotConnectedMessage, vm.UpstreamMessage);
        Assert.Equal(ParseValueUpstreamResolver.NotConnectedMessage, vm.TestError);
        Assert.Empty(vm.Rows);
        Assert.False(vm.IsLoading);
        Assert.False(vm.LoadCommand.CanExecute(null));
    }

    private static async Task<(ParseStudioViewModel Vm, NodeViewModel Csv, NodeViewModel Parse)> LoadedC01()
    {
        var studio = Studio("c01_basic.csv");
        await TestTimeout.Run(() => studio.Vm.ReloadNowAsync());
        Assert.Equal(4, studio.Vm.Rows.Count);
        return studio;
    }

    private static (ParseStudioViewModel Vm, NodeViewModel Csv, NodeViewModel Parse) Studio(
        string file, string previewRows = "5", string[]? ignore = null, CsvPreviewReader? reader = null)
    {
        var (editor, csv, parse) = ParseValueUpstreamTests.ConfiguredChain(file, previewRows);
        csv.ColumnSelection.Mode = CsvColumnSelectionMode.Ignore;
        csv.ColumnSelection.Names.AddRange(ignore ?? []);
        var upstream = ParseValueUpstreamResolver.Resolve(parse, editor.Connections);
        Assert.NotNull(upstream.Settings);
        return (new ParseStudioViewModel(parse, upstream, reader), csv, parse);
    }
}
