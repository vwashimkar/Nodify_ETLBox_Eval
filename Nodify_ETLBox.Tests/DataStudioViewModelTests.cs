using EtlDesigner.Engine.Csv;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class DataStudioViewModelTests
{
    [Fact]
    public async Task Loads_C01_WithPreviewRows5_Returns4Rows()
    {
        var node = CreateCsvNode(CompositePath("c01_basic.csv"), "Comma", true, "0", "utf-8", "5");
        var vm = new DataStudioViewModel(node, BuildSettings(node));

        await TestTimeout.Run(() => vm.ReloadNowAsync());

        Assert.Null(vm.PreviewError);
        Assert.Equal(4, vm.LoadedRowCount);
    }

    [Fact]
    public async Task IgnoreThreeColumns_ReducesResultColumnsByThree_AndKeepsRows()
    {
        var node = CreateCsvNode(CompositePath("c01_basic.csv"), "Comma", true, "0", "utf-8", "5");
        var vm = new DataStudioViewModel(node, BuildSettings(node));
        await TestTimeout.Run(() => vm.ReloadNowAsync());

        var initialRows = vm.LoadedRowCount;
        var initialColumns = vm.TotalColumnCount;

        vm.Columns[0].IsSelected = true;
        vm.Columns[1].IsSelected = true;
        vm.Columns[2].IsSelected = true;
        vm.TestDatasetCommand.Execute(null);

        Assert.NotNull(vm.ResultRowsView);
        Assert.Equal(initialRows, vm.ResultRowsView!.Count);
        Assert.Equal(initialColumns - 3, vm.ResultRowsView.Table!.Columns.Count - 1);
    }

    [Fact]
    public async Task KeepOnlyTwoColumns_UsesFileOrder()
    {
        var node = CreateCsvNode(CompositePath("c01_basic.csv"), "Comma", true, "0", "utf-8", "5");
        var vm = new DataStudioViewModel(node, BuildSettings(node));
        await TestTimeout.Run(() => vm.ReloadNowAsync());

        vm.IsIgnoreMode = false;
        vm.SelectNoColumnsCommand.Execute(null);

        var name = vm.Columns.First(c => c.Name == "name");
        var id = vm.Columns.First(c => c.Name == "id");
        name.IsSelected = true;
        id.IsSelected = true;

        vm.TestDatasetCommand.Execute(null);

        var resultColumns = vm.ResultRowsView!.Table!.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName).ToList();
        Assert.Equal(new[] { "#", "id", "name" }, resultColumns);
    }

    [Fact]
    public async Task InvertTwice_RestoresOriginalSelection()
    {
        var node = CreateCsvNode(CompositePath("c01_basic.csv"), "Comma", true, "0", "utf-8", "5");
        var vm = new DataStudioViewModel(node, BuildSettings(node));
        await TestTimeout.Run(() => vm.ReloadNowAsync());

        var original = vm.Columns.Select(c => c.IsSelected).ToArray();

        vm.InvertColumnsCommand.Execute(null);
        vm.InvertColumnsCommand.Execute(null);

        Assert.Equal(original, vm.Columns.Select(c => c.IsSelected).ToArray());
    }

    [Fact]
    public async Task ChangingSeparatorOnC06_ReloadsWithColumns()
    {
        var node = CreateCsvNode(CompositePath("c06_semicolon.csv"), "Comma", true, "0", "utf-8", "5");
        var vm = new DataStudioViewModel(node, BuildSettings(node));
        await TestTimeout.Run(() => vm.ReloadNowAsync());

        vm.Separator = ";";
        await TestTimeout.Run(() => vm.ReloadNowAsync());

        Assert.Null(vm.PreviewError);
        Assert.Equal(32, vm.TotalColumnCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Save_WritesHasHeaderBackToNode(bool hasHeader)
    {
        var node = CreateCsvNode(CompositePath("c01_basic.csv"), "Comma", true, "0", "utf-8", "5");
        var vm = new DataStudioViewModel(node, BuildSettings(node));
        await TestTimeout.Run(() => vm.ReloadNowAsync());

        vm.HasHeader = hasHeader;
        vm.SaveCommand.Execute(null);

        Assert.Equal(hasHeader, node.Settings.First(s => s.Key == "HasHeader").BoolValue);
    }

    [Fact]
    public async Task Cancel_LeavesNodeUnchanged()
    {
        var node = CreateCsvNode(CompositePath("c01_basic.csv"), "Comma", true, "0", "utf-8", "5");
        node.ColumnSelection.Mode = CsvColumnSelectionMode.KeepOnly;
        node.ColumnSelection.Names.Add("id");

        var beforeSeparator = node.Settings.First(s => s.Key == "Separator").Value;
        var beforeHeader = node.Settings.First(s => s.Key == "HasHeader").BoolValue;
        var beforeMode = node.ColumnSelection.Mode;
        var beforeNames = node.ColumnSelection.Names.ToList();

        var vm = new DataStudioViewModel(node, BuildSettings(node));
        await TestTimeout.Run(() => vm.ReloadNowAsync());

        vm.Separator = ";";
        vm.HasHeader = false;
        vm.IsIgnoreMode = true;
        if (vm.Columns.Count > 0)
        {
            vm.Columns[0].IsSelected = true;
        }

        vm.CancelCommand.Execute(null);

        Assert.Equal(beforeSeparator, node.Settings.First(s => s.Key == "Separator").Value);
        Assert.Equal(beforeHeader, node.Settings.First(s => s.Key == "HasHeader").BoolValue);
        Assert.Equal(beforeMode, node.ColumnSelection.Mode);
        Assert.Equal(beforeNames, node.ColumnSelection.Names);
    }

    [Fact]
    public async Task CancelledReload_KeepsPreviousData_AndStopsSpinner()
    {
        var reader = new CsvPreviewReader();
        var node = CreateCsvNode(CompositePath("c01_basic.csv"), "Comma", true, "0", "utf-8", "5");
        var vm = new DataStudioViewModel(node, BuildSettings(node), reader);
        await TestTimeout.Run(() => vm.ReloadNowAsync());
        var statusBefore = vm.StatusText;

        // Hold the next read at its first row, cancel it there, then let it go.
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        reader.RowCollected = count =>
        {
            if (count == 1)
            {
                reached.Set();
                release.Wait(TestTimeout.Default);
            }
        };

        using var cts = new CancellationTokenSource();
        var reload = vm.ReloadNowAsync(cts.Token);
        Assert.True(reached.Wait(TestTimeout.Default), "The reload never reached its first row.");
        Assert.True(vm.IsLoading);

        cts.Cancel();
        release.Set();
        await TestTimeout.Run(() => reload);

        Assert.False(vm.IsLoading);
        Assert.False(vm.ShowCancelLoad);
        Assert.Null(vm.PreviewError);
        Assert.Equal(4, vm.LoadedRowCount);
        Assert.Equal(statusBefore, vm.StatusText);
    }

    [Fact]
    public async Task OlderCancelledReload_DoesNotStopSpinnerOfNewerReload()
    {
        var reader = new CsvPreviewReader();
        var node = CreateCsvNode(CompositePath("c01_basic.csv"), "Comma", true, "0", "utf-8", "5");
        var vm = new DataStudioViewModel(node, BuildSettings(node), reader);
        await TestTimeout.Run(() => vm.ReloadNowAsync());

        // Hold each of the next two reads at its first row.
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
        Assert.Equal(4, vm.LoadedRowCount);
    }

    [Theory]
    [InlineData(1001)]
    [InlineData(5001)]
    [InlineData(6000)]
    public async Task PreviewRowsAboveTheLimit_IsClampedTo1000_WithTheInlineMessage(int typed)
    {
        var (vm, _, requests) = await FakeLoaded(Good);

        vm.PreviewRows = typed;

        Assert.Equal(1000, vm.PreviewRows);
        Assert.Equal("Preview rows is limited to 1,000. Use Execute to read the whole file.", vm.PreviewRowsMessage);

        await TestTimeout.Run(() => vm.ReloadNowAsync());
        Assert.Equal(1000, requests[^1].PreviewRows);
    }

    [Fact]
    public async Task PreviewRowsAtTheLimit_HasNoMessage_AndALowerValueClearsIt()
    {
        var (vm, _, _) = await FakeLoaded(Good);

        vm.PreviewRows = 1000;
        Assert.Null(vm.PreviewRowsMessage);

        vm.PreviewRows = 6000;
        Assert.NotNull(vm.PreviewRowsMessage);

        vm.PreviewRows = 20;
        Assert.Null(vm.PreviewRowsMessage);
        Assert.Equal(20, vm.PreviewRows);
    }

    [Fact]
    public async Task PreviewRowsAlreadyAtTheLimit_RefreshesTheBox_WhenALargerValueIsTyped()
    {
        var (vm, _, _) = await FakeLoaded(Good);
        vm.PreviewRows = 1000;
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.PreviewRows = 6000;

        Assert.Contains(nameof(DataStudioViewModel.PreviewRows), changed);
    }

    [Fact]
    public async Task NodeSavedAbove1000_OpensClamped_AndSaveWrites1000()
    {
        var (vm, node, requests) = await FakeLoaded(Good, previewRows: "6000");

        Assert.Equal(1000, vm.PreviewRows);
        Assert.NotNull(vm.PreviewRowsMessage);
        Assert.Equal(1000, requests[0].PreviewRows);

        vm.SaveCommand.Execute(null);
        Assert.Equal("1000", node.Settings.First(s => s.Key == "PreviewRows").Value);
    }

    [Fact]
    public async Task C10_WithPreviewRows6000_ShowsTheFirst1000Rows_WithoutALicenceStop()
    {
        // The node asks for 6,000 preview rows, so the window opens with the value cut to 1,000. G6d: the test
        // used to set PreviewRows after opening, which also schedules a delayed reload (400 ms). Without a UI
        // thread an older load could switch the spinner off after that reload had switched it on, and the test
        // saw a false "done" (in the app both run on the UI thread). Now the explicit reload below is the newest
        // and only load, so its end is final. The delayed reload's order is covered by
        // ExplicitReload_CancelledByTheDelayedReload_KeepsTheSpinner_AndTheNewestLoadShowsTheRows.
        var node = CreateCsvNode(CompositePath("c10_large.csv"), "Comma", true, "0", "utf-8", "6000");
        var reader = new CsvPreviewReader();
        var requests = new List<int>();
        var vm = new DataStudioViewModel(node, BuildSettings(node), read: (settings, token) =>
        {
            lock (requests)
            {
                requests.Add(settings.PreviewRows);
            }

            return reader.ReadAsync(settings, token);
        });
        Assert.Equal(1000, vm.PreviewRows);
        Assert.Equal(DataStudioViewModel.PreviewRowsLimitMessage, vm.PreviewRowsMessage);

        await TestTimeout.Run(() => vm.ReloadNowAsync());

        lock (requests)
        {
            Assert.Equal(new[] { 1000, 1000 }, requests); // the constructor's load and the explicit one, no delayed reload
        }

        Assert.True(vm.PreviewError is null, $"Preview error: {vm.PreviewError}");
        Assert.False(vm.IsLoading);
        Assert.Equal(1000, vm.LoadedRowCount);
    }

    /// <summary>
    /// The explicit reload is cancelled by the delayed reload that setting PreviewRows schedules.
    /// The fake reader holds every read until the test releases it, so the order is forced:
    /// the explicit call returns with 0 rows and no error while the spinner stays on, and the
    /// newest load then shows the rows. A user sees the spinner, never an empty grid without it.
    /// </summary>
    [Fact]
    public async Task ExplicitReload_CancelledByTheDelayedReload_KeepsTheSpinner_AndTheNewestLoadShowsTheRows()
    {
        var node = CreateCsvNode(CompositePath("c01_basic.csv"), "Comma", true, "0", "utf-8", "5");
        var reads = new List<(CsvFileSettings Settings, TaskCompletionSource<CsvPreview> Result)>();
        var thirdRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new DataStudioViewModel(node, BuildSettings(node), read: (settings, _) =>
        {
            var result = new TaskCompletionSource<CsvPreview>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (reads)
            {
                reads.Add((settings, result));
                if (reads.Count == 3)
                {
                    thirdRead.TrySetResult();
                }
            }

            return result.Task;
        });
        CsvFileSettings? LastRequest() { lock (reads) { return reads[^1].Settings; } }
        var loaded = LoadOf1000Ended(vm, LastRequest);

        vm.PreviewRows = 6000;
        var explicitReload = vm.ReloadNowAsync();
        await TestTimeout.Run(() => thirdRead.Task);

        reads[0].Result.SetResult(Rows(5));
        reads[1].Result.SetResult(Rows(1000));
        await TestTimeout.Run(() => explicitReload);

        Assert.Equal(1000, reads[1].Settings.PreviewRows);
        Assert.Equal(1000, reads[2].Settings.PreviewRows);
        Assert.Equal(0, vm.LoadedRowCount);
        Assert.Null(vm.PreviewError);
        Assert.True(vm.IsLoading, "The spinner must stay on while the newest load runs.");

        reads[2].Result.SetResult(Rows(1000));
        await TestTimeout.Run(() => loaded);

        Assert.False(vm.IsLoading);
        Assert.Null(vm.PreviewError);
        Assert.Equal(1000, vm.LoadedRowCount);
    }

    /// <summary>Ends when the spinner goes off after a load of 1,000 rows was asked for.</summary>
    private static Task LoadOf1000Ended(DataStudioViewModel vm, Func<CsvFileSettings?> lastRequest)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DataStudioViewModel.IsLoading) && !vm.IsLoading && lastRequest()?.PreviewRows == 1000)
            {
                ended.TrySetResult();
            }
        };
        return ended.Task;
    }

    private static CsvPreview Rows(int count)
        => new(["id"], Enumerable.Range(1, count).Select(i => (IReadOnlyList<string>)[i.ToString()]).ToList(), false, null, 1);

    [Fact]
    public async Task LicenceStop_HasNoFormatHint_AndClearsTheOldData()
    {
        CsvPreview next = Good;
        var (vm, _, _) = await FakeLoaded(() => next);
        Assert.Equal(1, vm.LoadedRowCount);
        Assert.False(vm.ShowFormatHint);
        Assert.Contains("Showing 1 rows", vm.StatusText);

        next = new CsvPreview([], [], false, "The ETLBox licence stopped the preview. A valid licence is needed to read this file.", 3);
        await TestTimeout.Run(() => vm.ReloadNowAsync());

        Assert.NotNull(vm.PreviewError);
        Assert.False(vm.ShowFormatHint);
        Assert.Null(vm.DataRowsView);
        Assert.Null(vm.ResultRowsView);
        Assert.Empty(vm.Columns);
        Assert.Empty(vm.FilteredColumns);
        Assert.Equal(0, vm.LoadedRowCount);
        Assert.Equal(0, vm.TotalColumnCount);
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.Equal(string.Empty, vm.EmptyStateMessage);
    }

    [Fact]
    public async Task MissingFile_HasNoFormatHint_AndClearsTheOldData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.csv");
        File.Copy(CompositePath("c01_basic.csv"), path);
        var node = CreateCsvNode(path, "Comma", true, "0", "utf-8", "5");
        var vm = new DataStudioViewModel(node, BuildSettings(node));
        await TestTimeout.Run(() => vm.ReloadNowAsync());
        Assert.Equal(4, vm.LoadedRowCount);

        File.Delete(path);
        await TestTimeout.Run(() => vm.ReloadNowAsync());

        Assert.NotNull(vm.PreviewError);
        Assert.False(vm.ShowFormatHint);
        Assert.Equal(0, vm.LoadedRowCount);
        Assert.Equal(string.Empty, vm.StatusText);
    }

    [Fact]
    public async Task UnclosedQuote_ShowsTheFormatHint_AndAFixedLoadHidesIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path, ["id,name", "1,Good", "2,\"Broken"]);
        try
        {
            var node = CreateCsvNode(path, "Comma", true, "0", "utf-8", "5");
            var vm = new DataStudioViewModel(node, BuildSettings(node));
            await TestTimeout.Run(() => vm.ReloadNowAsync());

            Assert.NotNull(vm.PreviewError);
            Assert.True(vm.ShowFormatHint);
            Assert.Equal(0, vm.LoadedRowCount);

            File.WriteAllLines(path, ["id,name", "1,Good"]);
            await TestTimeout.Run(() => vm.ReloadNowAsync());

            Assert.Null(vm.PreviewError);
            Assert.False(vm.ShowFormatHint);
            Assert.Equal(1, vm.LoadedRowCount);
            Assert.Contains("Showing 1 rows", vm.StatusText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FailedLoad_KeepsTheColumnSelection_ForTheNextGoodLoadAndForSave()
    {
        CsvPreview next = Good;
        var (vm, node, _) = await FakeLoaded(() => next);
        vm.Columns.Single(c => c.Name == "a").IsSelected = true;

        next = new CsvPreview([], [], false, "File was not found. Check the path and try again.", 3);
        await TestTimeout.Run(() => vm.ReloadNowAsync());
        vm.SaveCommand.Execute(null);

        Assert.Equal(["a"], node.ColumnSelection.Names);
        Assert.Null(node.KnownColumns);

        next = Good;
        await TestTimeout.Run(() => vm.ReloadNowAsync());

        Assert.True(vm.Columns.Single(c => c.Name == "a").IsSelected);
        Assert.False(vm.Columns.Single(c => c.Name == "b").IsSelected);
    }

    private static readonly CsvPreview Good = new(["a", "b"], [["1", "2"]], false, null, 3);

    private static Task<(DataStudioViewModel Vm, NodeViewModel Node, List<CsvFileSettings> Requests)> FakeLoaded(CsvPreview preview, string previewRows = "5")
        => FakeLoaded(() => preview, previewRows);

    private static async Task<(DataStudioViewModel Vm, NodeViewModel Node, List<CsvFileSettings> Requests)> FakeLoaded(Func<CsvPreview> preview, string previewRows = "5")
    {
        var node = CreateCsvNode("fake.csv", "Comma", true, "0", "utf-8", previewRows);
        var requests = new List<CsvFileSettings>();
        var vm = new DataStudioViewModel(node, BuildSettings(node), read: (settings, _) =>
        {
            requests.Add(settings);
            return Task.FromResult(preview());
        });

        await TestTimeout.Run(() => vm.WaitForCurrentLoadAsync());
        return (vm, node, requests);
    }

    private static NodeViewModel CreateCsvNode(string filePath, string separator, bool hasHeader, string skipLines, string encodingName, string previewRows)
    {
        var node = new NodeViewModel(Guid.NewGuid(), "CsvFile", NodeType.CsvFile, _ => true);
        node.Settings.Add(new SettingViewModel("FilePath", "File path", SettingKind.FilePath, required: true, filePath));
        node.Settings.Add(new SettingViewModel("Separator", "Separator", SettingKind.ComboBox, required: true, separator));
        node.Settings.Add(new SettingViewModel("HasHeader", "Header row", SettingKind.CheckBox, required: false, hasHeader ? bool.TrueString : bool.FalseString));
        node.Settings.Add(new SettingViewModel("SkipLines", "Skip lines", SettingKind.Number, required: true, skipLines));
        node.Settings.Add(new SettingViewModel("EncodingName", "Encoding", SettingKind.ComboBox, required: true, encodingName));
        node.Settings.Add(new SettingViewModel("PreviewRows", "Preview rows", SettingKind.Number, required: true, previewRows));
        return node;
    }

    private static CsvFileSettings BuildSettings(NodeViewModel node)
    {
        var separator = node.Settings.First(s => s.Key == "Separator").Value;
        separator = separator.Equals("Comma", StringComparison.OrdinalIgnoreCase) ? "," : separator;
        separator = separator.Equals("Semicolon", StringComparison.OrdinalIgnoreCase) ? ";" : separator;
        separator = separator.Equals("Tab", StringComparison.OrdinalIgnoreCase) ? "tab" : separator;
        separator = separator.Equals("Pipe", StringComparison.OrdinalIgnoreCase) ? "|" : separator;

        return new CsvFileSettings(
            node.Settings.First(s => s.Key == "FilePath").Value,
            separator,
            node.Settings.First(s => s.Key == "HasHeader").BoolValue,
            int.Parse(node.Settings.First(s => s.Key == "SkipLines").Value),
            node.Settings.First(s => s.Key == "EncodingName").Value,
            int.Parse(node.Settings.First(s => s.Key == "PreviewRows").Value));
    }

    private static string CompositePath(string fileName)
        => Path.Combine(TestDataPath("composite"), fileName);

    private static string TestDataPath(string relative)
        => Path.Combine(RepoRoot(), "Nodify_ETLBox", "SampleData", "TestData", relative);

    private static string RepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (current.GetFiles("*.slnx").Length > 0 || current.GetFiles("*.sln").Length > 0)
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root was not found from test base directory.");
    }
}
