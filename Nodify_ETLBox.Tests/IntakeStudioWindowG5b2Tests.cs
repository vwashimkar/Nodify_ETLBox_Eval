using System.Windows;
using System.Windows.Controls;
using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.ViewModels;
using Nodify_ETLBox.UI;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>G5b2: the compact Edit tab, the narrow window and the Preview copy, in the real Intake Studio window.</summary>
public sealed partial class IntakeStudioWindowTests
{
    private static readonly string[] FormatAndTableRow =
        ["FormatBox", "ChangeFormatButton", "DelimiterBox", "HeaderBox", "SkipLinesBox", "ApplyOptionsButton"];

    /// <summary>The control's bounds in the Edit tab's ScrollViewer (its viewport starts at 0, 0).</summary>
    private static Rect InEditScroll(Window window, string name)
    {
        var scroll = (ScrollViewer)window.FindName("EditScroll");
        var control = Named(window, name);
        return control.TransformToAncestor(scroll).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
    }

    private static void AssertInsideViewport(Window window, string name)
    {
        var scroll = (ScrollViewer)window.FindName("EditScroll");
        var box = InEditScroll(window, name);
        Assert.True(Named(window, name).ActualWidth > 0 && Named(window, name).ActualHeight > 0, $"{name} has no size.");
        Assert.True(box.Left >= -0.5 && box.Top >= -0.5 && box.Bottom <= scroll.ViewportHeight + 0.5 && box.Right <= scroll.ViewportWidth + 0.5,
            $"{name} at {box} is outside the Edit tab's viewport {scroll.ViewportWidth:0} x {scroll.ViewportHeight:0}.");
    }

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task G5b2_1A_At1100x720_EveryEditControlIsVisible_WithoutScrolling(WpfTheme theme)
        => WithEditTab(theme, 1100, 720, async (window, vm) =>
        {
            await Scan(vm, window);
            await SelectTable(vm, window);
            var scroll = (ScrollViewer)window.FindName("EditScroll");
            Assert.Equal(0, scroll.ScrollableHeight, 1);
            foreach (var name in EditControls.Append("EditIntro"))
            {
                AssertInsideViewport(window, name);
            }

            // The intro is one line.
            var intro = (TextBlock)window.FindName("EditIntro");
            Assert.True(intro.ActualHeight < 2 * intro.FontFamily.LineSpacing * intro.FontSize);
        });

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task G5b2_1B_At820x560_FormatAndTheTableRowAreVisible_TheRestCanBeScrolledTo(WpfTheme theme)
        => WithEditTab(theme, 820, 560, async (window, vm) =>
        {
            await Scan(vm, window);
            await SelectTable(vm, window);
            var scroll = (ScrollViewer)window.FindName("EditScroll");
            Assert.Equal(0, scroll.VerticalOffset, 1);
            foreach (var name in FormatAndTableRow)
            {
                AssertInsideViewport(window, name);
            }

            foreach (var name in EditControls.Except(FormatAndTableRow))
            {
                Named(window, name).BringIntoView();
                await WpfHost.Settle();
                window.UpdateLayout();
                AssertInsideViewport(window, name);
            }
        });

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task G5b2_1C_HeaderNotSet_ShowsAPlaceholder_ThatIsNotAChoice(WpfTheme theme)
        => WithEditTab(theme, 1100, 720, async (window, vm) =>
        {
            await Scan(vm, window);
            var unknown = vm.Containers.FirstOrDefault(r => r.Info.Format is "csv" or "tsv" && r.Info.HeaderGuess is not (HeaderGuess.Yes or HeaderGuess.No));
            if (unknown is not null)
            {
                await Select(vm, window, unknown.Number);
            }
            else
            {
                // i01 has no table without a header guess: the same state as the view model gives such a part.
                await SelectTable(vm, window);
                vm.SelectedHeader = null;
                await WpfHost.Settle();
                window.UpdateLayout();
            }

            var placeholder = (TextBlock)window.FindName("HeaderPlaceholder");
            var header = (ComboBox)window.FindName("HeaderBox");
            Assert.Null(vm.SelectedHeader);
            Assert.True(WpfHost.IsVisibleInLayout(placeholder));
            Assert.Equal("(not set)", placeholder.Text);
            Assert.Equal(((System.Windows.Media.SolidColorBrush)window.FindResource("text-muted-brush")).Color,
                ((System.Windows.Media.SolidColorBrush)placeholder.Foreground).Color);
            Assert.Equal(["Yes", "No"], header.Items.Cast<SettingChoice>().Select(c => c.Label));
            Assert.Equal(-1, header.SelectedIndex);

            vm.SelectedHeader = "Yes";
            await WpfHost.Settle();
            window.UpdateLayout();
            Assert.False(WpfHost.IsVisibleInLayout(placeholder));
        });
}
