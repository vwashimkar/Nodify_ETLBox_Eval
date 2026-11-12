using System.Windows;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class ParseValueConnectionTests
{
    [Fact]
    public void CsvFile_To_ParseValue_IsAccepted()
    {
        var editor = new EditorViewModel();
        var csv = Add(editor, NodeType.CsvFile);
        var parse = Add(editor, NodeType.ParseValue);

        Connect(editor, csv.Outputs[0], parse.Inputs[0]);

        Assert.Single(editor.Connections);
        Assert.True(parse.Inputs[0].IsConnected);
    }

    [Theory]
    [InlineData(NodeType.CsvSource)]
    [InlineData(NodeType.FilterByCountry)]
    public void OtherRowsNode_To_ParseValue_IsRefusedWithMessage(NodeType sourceType)
    {
        var editor = new EditorViewModel();
        var other = Add(editor, sourceType);
        var parse = Add(editor, NodeType.ParseValue);

        Connect(editor, other.Outputs[0], parse.Inputs[0]);

        Assert.Empty(editor.Connections);
        Assert.False(parse.Inputs[0].IsConnected);
        Assert.Equal(["ParseValue: the input accepts only a CsvFile or CSV part node for now. Connect the output of a CsvFile or CSV part node."], editor.Issues);
    }

    [Fact]
    public void SecondCsvFile_To_ParseValue_IsRefusedWithMessage()
    {
        var editor = new EditorViewModel();
        var first = Add(editor, NodeType.CsvFile);
        var second = Add(editor, NodeType.CsvFile);
        var parse = Add(editor, NodeType.ParseValue);

        Connect(editor, first.Outputs[0], parse.Inputs[0]);
        Connect(editor, second.Outputs[0], parse.Inputs[0]);

        Assert.Single(editor.Connections);
        Assert.Equal(["ParseValue: the input already has a connection. Remove it before connecting another node."], editor.Issues);
    }

    [Fact]
    public void ParseValue_To_LegacyNode_IsRefusedByPortKind()
    {
        var editor = new EditorViewModel();
        var parse = Add(editor, NodeType.ParseValue);
        var toJson = Add(editor, NodeType.ToJson);

        Connect(editor, parse.Outputs[0], toJson.Inputs[0]);

        Assert.Empty(editor.Connections);
        Assert.Equal(["Port kind mismatch: ParsedRows → Rows."], editor.Issues);
    }

    [Fact]
    public void LegacyRules_Unchanged_CsvSourceToFilter_AndSecondInputRefused()
    {
        var editor = new EditorViewModel();
        var csv = Add(editor, NodeType.CsvSource);
        var csv2 = Add(editor, NodeType.CsvSource);
        var filter = Add(editor, NodeType.FilterByCountry);

        Connect(editor, csv.Outputs[0], filter.Inputs[0]);
        Assert.Single(editor.Connections);
        Assert.Empty(editor.Issues);

        Connect(editor, csv2.Outputs[0], filter.Inputs[0]);
        Assert.Single(editor.Connections);
        Assert.Equal(["Input is already connected."], editor.Issues);
    }

    internal static NodeViewModel Add(EditorViewModel editor, NodeType type)
    {
        editor.AddNode(type, new Point(0, 0));
        return editor.Nodes[^1];
    }

    internal static void Connect(EditorViewModel editor, ConnectorViewModel output, ConnectorViewModel input)
    {
        editor.StartConnection(output);
        editor.CompleteConnection(input);
    }
}
