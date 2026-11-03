using EtlDesigner.Engine.Csv;
using EtlDesigner.UI.Services;
using EtlDesigner.UI.ViewModels;
using Xunit;
using static Nodify_ETLBox.Tests.ParseValueConnectionTests;

namespace Nodify_ETLBox.Tests;

public class ParseValueUpstreamTests
{
    [Fact]
    public void NotConnected_GivesMessage()
    {
        var editor = new EditorViewModel();
        var parse = Add(editor, NodeType.ParseValue);

        var upstream = ParseValueUpstreamResolver.Resolve(parse, editor.Connections);

        Assert.False(upstream.IsResolved);
        Assert.Null(upstream.Settings);
        Assert.Equal("Connect a configured CsvFile node to the input of this node.", upstream.Message);
    }

    [Fact]
    public void ConnectedToUnconfiguredCsvFile_GivesSameMessage()
    {
        var editor = new EditorViewModel();
        var csv = Add(editor, NodeType.CsvFile);
        var parse = Add(editor, NodeType.ParseValue);
        Connect(editor, csv.Outputs[0], parse.Inputs[0]);

        var upstream = ParseValueUpstreamResolver.Resolve(parse, editor.Connections);

        Assert.Equal("Not configured", csv.ConfigurationStatus);
        Assert.Equal(ParseValueUpstreamResolver.NotConnectedMessage, upstream.Message);
    }

    [Fact]
    public async Task ConfiguredCsvFile_ColumnsUnknownThenRead_GivesSettingsAndKeptColumns()
    {
        var (editor, csv, parse) = ConfiguredChain("c01_basic.csv");
        csv.ColumnSelection.Mode = CsvColumnSelectionMode.Ignore;
        csv.ColumnSelection.Names.AddRange(["extra_01", "EXTRA_02"]);

        var before = ParseValueUpstreamResolver.Resolve(parse, editor.Connections);
        Assert.Equal(ParseValueUpstreamResolver.ColumnsUnknownMessage, before.Message);
        Assert.Equal(TestFiles.Composite("c01_basic.csv"), before.Settings!.Path);

        Assert.True(await TestTimeout.Run(() => ParseValueUpstreamResolver.RefreshColumnsAsync(csv, new CsvPreviewReader(), CancellationToken.None)));
        var after = ParseValueUpstreamResolver.Resolve(parse, editor.Connections);

        Assert.True(after.IsResolved);
        Assert.Equal(",", after.Settings!.Separator);
        Assert.Equal(30, after.KeptColumns!.Count);
        Assert.Equal("id", after.KeptColumns[0]);
        Assert.DoesNotContain("extra_01", after.KeptColumns);
        Assert.DoesNotContain("extra_02", after.KeptColumns);
    }

    [Fact]
    public void KeptColumns_KeepOnly_UsesFileOrder()
    {
        var selection = new CsvColumnSelection { Mode = CsvColumnSelectionMode.KeepOnly };
        selection.Names.AddRange(["sample", "id"]);

        Assert.Equal(["id", "sample"], ParseValueUpstreamResolver.KeptColumns(["id", "name", "sample"], selection));
    }

    [Fact]
    public void ChangingACsvFileSetting_ForgetsKnownColumns()
    {
        var (_, csv, _) = ConfiguredChain("c01_basic.csv");
        csv.KnownColumns = ["id"];

        csv.Settings.Single(s => s.Key == "HasHeader").BoolValue = false;

        Assert.Null(csv.KnownColumns);
    }

    /// <summary>An editor with a configured CsvFile node connected to a ParseValue node.</summary>
    internal static (EditorViewModel Editor, NodeViewModel Csv, NodeViewModel Parse) ConfiguredChain(string compositeFile, string previewRows = "5")
    {
        var editor = new EditorViewModel();
        var csv = Add(editor, NodeType.CsvFile);
        var parse = Add(editor, NodeType.ParseValue);
        csv.Settings.Single(s => s.Key == "FilePath").Value = TestFiles.Composite(compositeFile);
        csv.Settings.Single(s => s.Key == "PreviewRows").Value = previewRows;
        Connect(editor, csv.Outputs[0], parse.Inputs[0]);
        editor.Validate();
        Assert.Equal("Configured", csv.ConfigurationStatus);
        return (editor, csv, parse);
    }
}

internal static class TestFiles
{
    public static string Composite(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "SampleData", "TestData", "composite", fileName);
}
