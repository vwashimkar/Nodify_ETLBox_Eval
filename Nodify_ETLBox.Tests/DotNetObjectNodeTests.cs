using System.Windows;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class DotNetObjectNodeTests
{
    private static NodeViewModel Add(EditorViewModel editor, NodeType type)
    {
        editor.AddNode(type, new Point(0, 0));
        return editor.Nodes[^1];
    }

    private static void Connect(EditorViewModel editor, ConnectorViewModel output, ConnectorViewModel input)
    {
        editor.StartConnection(output);
        editor.CompleteConnection(input);
    }

    [Fact]
    public void Node_HasOneInputNoOutput_AndTheOutputTypeChoice()
    {
        var node = NodeFactory.CreateDotNetObject(new Point(0, 0));

        Assert.Equal(NodeType.DotNetObject, node.NodeType);
        Assert.Equal("DotNetObject", node.Title);
        Assert.Equal("Parsed", Assert.Single(node.Inputs).Name);
        Assert.Equal(PortKind.ParsedRows, node.Inputs[0].Kind);
        Assert.Empty(node.Outputs);
        Assert.Equal("Not configured", node.ConfigurationStatus);

        var setting = Assert.Single(node.Settings);
        Assert.Equal("Output type", setting.Label);
        Assert.Equal(SettingKind.Choice, setting.Kind);
        Assert.Equal(nameof(ObjectKind.ListOfStringDictionary), setting.Value);
        Assert.Equal(OutputSettings.Default, node.GetOutputSettings());
        Assert.Equal(
            ["List<Dictionary<string, string>>", "List<T> of a generated class (comes later)", "DataTable (comes later)"],
            setting.Choices.Select(c => c.Label));
        Assert.Equal([true, false, false], setting.Choices.Select(c => c.IsEnabled));
    }

    [Fact]
    public void ParseValue_To_DotNetObject_IsAccepted_AndConfigured()
    {
        var editor = new EditorViewModel();
        var parse = Add(editor, NodeType.ParseValue);
        var output = Add(editor, NodeType.DotNetObject);

        Connect(editor, parse.Outputs[0], output.Inputs[0]);

        Assert.Single(editor.Connections);
        Assert.Equal("Configured", output.ConfigurationStatus);
        Assert.DoesNotContain(editor.Issues, i => i.StartsWith("DotNetObject:"));
    }

    [Theory]
    [InlineData(NodeType.CsvFile)]
    [InlineData(NodeType.CsvSource)]
    [InlineData(NodeType.FilterByCountry)]
    [InlineData(NodeType.ToJson)]
    public void OtherNode_To_DotNetObject_IsRefusedWithMessage(NodeType sourceType)
    {
        var editor = new EditorViewModel();
        var other = Add(editor, sourceType);
        var output = Add(editor, NodeType.DotNetObject);

        Connect(editor, other.Outputs[0], output.Inputs[0]);

        Assert.Empty(editor.Connections);
        Assert.Equal(["DotNetObject: the input accepts only a ParseValue node. Connect the output of a ParseValue node."], editor.Issues);
    }

    [Fact]
    public void SecondParseValue_To_DotNetObject_IsRefusedWithMessage()
    {
        var editor = new EditorViewModel();
        var first = Add(editor, NodeType.ParseValue);
        var second = Add(editor, NodeType.ParseValue);
        var output = Add(editor, NodeType.DotNetObject);

        Connect(editor, first.Outputs[0], output.Inputs[0]);
        Connect(editor, second.Outputs[0], output.Inputs[0]);

        Assert.Single(editor.Connections);
        Assert.Equal(["DotNetObject: the input already has a connection. Remove it before connecting another node."], editor.Issues);
    }

    [Fact]
    public void ParseValue_To_LegacyNode_IsStillRefusedByPortKind()
    {
        var editor = new EditorViewModel();
        var parse = Add(editor, NodeType.ParseValue);
        var toJson = Add(editor, NodeType.ToJson);

        Connect(editor, parse.Outputs[0], toJson.Inputs[0]);

        Assert.Empty(editor.Connections);
        Assert.Equal(["Port kind mismatch: ParsedRows → Rows."], editor.Issues);
    }

    [Fact]
    public void Unconnected_And_Unsupported_GiveIssuesOnceEach_WithNodeName()
    {
        var editor = new EditorViewModel();
        var output = Add(editor, NodeType.DotNetObject);
        output.Settings[0].Value = nameof(ObjectKind.DataTable);

        editor.Validate();
        editor.Validate();

        Assert.Equal(1, editor.Issues.Count(i => i == $"DotNetObject: {EditorViewModel.DotNetObjectNotConnected}"));
        Assert.Equal(1, editor.Issues.Count(i => i == $"DotNetObject: {ObjectKinds.NotYetSupported}"));
        Assert.Equal("Not configured", output.ConfigurationStatus);
    }

    [Fact]
    public void Disconnect_SetsTheChipBackToNotConfigured()
    {
        var editor = new EditorViewModel();
        var parse = Add(editor, NodeType.ParseValue);
        var output = Add(editor, NodeType.DotNetObject);
        Connect(editor, parse.Outputs[0], output.Inputs[0]);

        editor.Disconnect(editor.Connections[0]);

        Assert.Equal("Not configured", output.ConfigurationStatus);
        Assert.Contains($"DotNetObject: {EditorViewModel.DotNetObjectNotConnected}", editor.Issues);
    }
}
