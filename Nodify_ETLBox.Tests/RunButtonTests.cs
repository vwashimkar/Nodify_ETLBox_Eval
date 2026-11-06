using System.Windows;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.UI.Services;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>The legacy Run button on a canvas with new-style nodes.</summary>
public class RunButtonTests
{
    private const string LegacyChainMessage = "Graph supports only a linear 5-node chain";

    private sealed class NoDialogs : IDialogService
    {
        public void ShowCsvPreview(NodeViewModel node, CsvFileSettings settings) { }
        public void ShowParseValue(NodeViewModel node, ParseValueUpstream upstream) { }
        public void ShowExecute(ExecuteViewModel viewModel) { }
        public string? PickCsvFile(string? currentPath) => null;
        public void ShowGenerateDll(GenerateDllViewModel viewModel) { }
        public string? PickFolder(string? currentFolder) => null;
        public string? PickProjectToOpen(string? currentPath) => null;
        public string? PickProjectToSave(string? currentPath, string projectName) => null;
        public UnsavedChangesChoice AskUnsavedChanges(string projectName) => UnsavedChangesChoice.Cancel;
        public void ShowError(string title, string message) { }
    }

    private static MainViewModel Canvas(params NodeType[] types)
    {
        var main = new MainViewModel(new NoDialogs());
        for (var i = 0; i < types.Length; i++)
        {
            main.Editor.AddNode(types[i], new Point(i * 200, 0));
        }

        return main;
    }

    [Theory]
    [InlineData(NodeType.CsvFile)]
    [InlineData(NodeType.ParseValue)]
    [InlineData(NodeType.DotNetObject)]
    public void OneNewNode_ShowsOneLine(NodeType type)
    {
        var main = Canvas(type);

        main.RunCommand.Execute(null);

        Assert.Equal([MainViewModel.RunIsForLegacyChainMessage], main.Editor.Issues);
    }

    [Fact]
    public void TheFullNewGraph_ShowsOneLine_EvenWhenPressedTwice_AndAnUnknownNodeChangesNothing()
    {
        var main = Canvas(NodeType.CsvFile, NodeType.ParseValue, NodeType.DotNetObject);
        main.Editor.Nodes.Add(new NodeViewModel(Guid.NewGuid(), "Future", NodeType.Unknown, _ => true));

        main.RunCommand.Execute(null);
        main.RunCommand.Execute(null);

        Assert.Equal("Run is for the old five-node chain. Use Execute for this pipeline.", Assert.Single(main.Editor.Issues));
        Assert.True(main.RunCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(new[] { NodeType.CsvSource })]
    [InlineData(new[] { NodeType.CsvSource, NodeType.CsvFile })]
    [InlineData(new[] { NodeType.ResultJson, NodeType.ParseValue })]
    public void ALegacyNodeOnTheCanvas_KeepsTheLegacyMessages(NodeType[] types)
    {
        var main = Canvas(types);

        main.RunCommand.Execute(null);

        Assert.DoesNotContain(MainViewModel.RunIsForLegacyChainMessage, main.Editor.Issues);
        Assert.Contains(main.Editor.Issues, i => i.StartsWith(LegacyChainMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyCanvas_KeepsTheLegacyMessages()
    {
        var main = Canvas();

        main.RunCommand.Execute(null);

        Assert.DoesNotContain(MainViewModel.RunIsForLegacyChainMessage, main.Editor.Issues);
    }
}
