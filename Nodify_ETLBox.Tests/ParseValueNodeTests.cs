using System.Windows;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class ParseValueNodeTests
{
    [Fact]
    public void NewNode_HasOneInputOneOutput_DefaultSettings_AndSummary()
    {
        var node = NodeFactory.CreateParseValue(new Point(0, 0));

        Assert.Equal("ParseValue", node.Title);
        Assert.Equal(NodeType.ParseValue, node.NodeType);
        Assert.Single(node.Inputs);
        Assert.Single(node.Outputs);
        Assert.Equal(PortKind.Rows, node.Inputs[0].Kind);
        Assert.Equal("Not configured", node.ConfigurationStatus);
        Assert.NotNull(node.ConfigureCommand);

        var settings = node.ParseValueSettings!;
        Assert.Equal(2, settings.DelimiterLevels.Count);
        Assert.Equal(6, settings.Slots.Count);
        Assert.True(settings.CarryColumns.AllOtherColumns);

        Assert.Equal("(not chosen)", Summary(node, "SourceColumn"));
        Assert.Equal("2", Summary(node, "DelimiterLevels"));
        Assert.Equal("6", Summary(node, "Slots"));
        Assert.Equal("All other columns", Summary(node, "CarryColumns"));
        Assert.Contains("\"sourceColumn\"", node.ParseValueSettingsText);
    }

    [Fact]
    public void ChangingSettings_UpdatesSummary()
    {
        var node = NodeFactory.CreateParseValue(new Point(0, 0));

        node.ParseValueSettings = node.ParseValueSettings! with
        {
            SourceColumn = "sample",
            Slots = [new Slot("S1", ["r01"])],
            CarryColumns = CarryColumns.Of("id", "name")
        };

        Assert.Equal("sample", Summary(node, "SourceColumn"));
        Assert.Equal("1", Summary(node, "Slots"));
        Assert.Equal("id, name", Summary(node, "CarryColumns"));
    }

    private static string Summary(NodeViewModel node, string key) => node.Settings.Single(s => s.Key == key).Value;
}

/// <summary>
/// The handler is static and every new MainViewModel replaces it, so this test must not run beside
/// the classes that create one.
/// </summary>
[Collection(NodeFactoryHandlerCollection.Name)]
public class ParseValueConfigureHandlerTests
{
    [Fact]
    public void ConfigureCommand_CallsTheHandlerWithTheNode()
    {
        var node = NodeFactory.CreateParseValue(new Point(0, 0));
        NodeViewModel? opened = null;
        var previous = NodeFactory.ParseValueConfigureHandler;
        NodeFactory.ParseValueConfigureHandler = n => { opened = n; return true; };
        try
        {
            node.ConfigureCommand!.Execute(null);
        }
        finally
        {
            NodeFactory.ParseValueConfigureHandler = previous;
        }

        Assert.Same(node, opened);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NodeFactoryHandlerCollection
{
    public const string Name = "NodeFactory handlers";
}
