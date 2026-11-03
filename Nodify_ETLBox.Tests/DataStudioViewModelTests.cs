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

        await vm.ReloadNowAsync();

        Assert.Null(vm.PreviewError);
        Assert.Equal(4, vm.LoadedRowCount);
    }

    [Fact]
    public async Task IgnoreThreeColumns_ReducesResultColumnsByThree_AndKeepsRows()
    {
        var node = CreateCsvNode(CompositePath("c01_basic.csv"), "Comma", true, "0", "utf-8", "5");
        var vm = new DataStudioViewModel(node, BuildSettings(node));
        await vm.ReloadNowAsync();

        var initialRows = vm.LoadedRowCount;
        var initialColumns = vm.TotalColumnCount;

        vm.Columns[0].IsSelected = true;
        vm.Columns[1].IsSelected = true;
        vm.Columns[2].IsSelected = true;
        vm.TestDatasetCommand.Execute(null);

        Assert.NotNull(vm.ResultRowsView);
        Assert.Equal(initialRows, vm.ResultRowsView!.Count);
        Assert.Equal(initialColumns - 3, vm.ResultRowsView.Table.Columns.Count - 1);
    }

    [Fact]
    public async Task KeepOnlyTwoColumns_UsesFileOrder()
    {
        var node = CreateCsvNode(CompositePath("c01_basic.csv"), "Comma", true, "0", "utf-8", "5");
        var vm = new DataStudioViewModel(node, BuildSettings(node));
        await vm.ReloadNowAsync();

        vm.IsIgnoreMode = false;
        vm.SelectNoColumnsCommand.Execute(null);

        var name = vm.Columns.First(c => c.Name == "name");
        var id = vm.Columns.First(c => c.Name == "id");
        name.IsSelected = true;
        id.IsSelected = true;

        vm.TestDatasetCommand.Execute(null);

        var resultColumns = vm.ResultRowsView!.Table.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName).ToList();
        Assert.Equal(new[] { "#", "id", "name" }, resultColumns);
    }

    [Fact]
    public async Task InvertTwice_RestoresOriginalSelection()
    {
        var node = CreateCsvNode(CompositePath("c01_basic.csv"), "Comma", true, "0", "utf-8", "5");
        var vm = new DataStudioViewModel(node, BuildSettings(node));
        await vm.ReloadNowAsync();

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
        await vm.ReloadNowAsync();

        vm.Separator = ";";
        await vm.ReloadNowAsync();

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
        await vm.ReloadNowAsync();

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
        await vm.ReloadNowAsync();

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
