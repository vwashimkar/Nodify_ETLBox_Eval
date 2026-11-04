using System.Windows;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

// The palette drop handler calls AddNode once with the drop position. The double add came
// from the button's Click after the drag, which is WPF wiring and has no seam here.
public class PaletteAddNodeTests
{
    [Fact]
    public void AddNode_AddsExactlyOneNode_AtTheGivenPosition()
    {
        var editor = new EditorViewModel();

        editor.AddNode(NodeType.ParseValue, new Point(120, 80));

        var node = Assert.Single(editor.Nodes);
        Assert.Equal(new Point(120, 80), node.Location);
    }
}
