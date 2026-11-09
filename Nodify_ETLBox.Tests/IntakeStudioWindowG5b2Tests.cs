using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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
            Assert.Equal(((SolidColorBrush)window.FindResource("text-muted-brush")).Color,
                ((SolidColorBrush)placeholder.Foreground).Color);
            Assert.Equal(["Yes", "No"], header.Items.Cast<SettingChoice>().Select(c => c.Label));
            Assert.Equal(-1, header.SelectedIndex);

            vm.SelectedHeader = "Yes";
            await WpfHost.Settle();
            window.UpdateLayout();
            Assert.False(WpfHost.IsVisibleInLayout(placeholder));
        });
    /// <summary>The window at <paramref name="width"/> x <paramref name="height"/> with i01 scanned and a table selected.</summary>
    private Task WithScannedWindow(WpfTheme theme, double width, double height, Func<IntakeStudioWindow, IntakeStudioViewModel, Task> check)
        => WithEditTab(theme, width, height, async (window, vm) =>
        {
            await Scan(vm, window);
            await SelectTable(vm, window);
            await check(window, vm);
        });

    [Theory]
    [InlineData(WpfTheme.Light, 820, 560)]
    [InlineData(WpfTheme.Dark, 820, 560)]
    [InlineData(WpfTheme.Light, 1100, 720)]
    [InlineData(WpfTheme.Dark, 1100, 720)]
    public Task G5b2_2A_TheContainersTitle_IsNeverClipped_TheLayoutButtonsAreVisible(WpfTheme theme, double width, double height)
        => WithScannedWindow(theme, width, height, (window, vm) =>
        {
            var title = (TextBlock)window.FindName("ContainersTitle");
            Assert.True(title.ActualWidth + 0.5 >= title.DesiredSize.Width - title.Margin.Left - title.Margin.Right,
                $"The title is {title.ActualWidth:0} px wide but needs {title.DesiredSize.Width:0}.");
            var root = (FrameworkElement)window.Content;
            var box = title.TransformToAncestor(root).TransformBounds(new Rect(0, 0, title.ActualWidth, title.ActualHeight));
            Assert.True(box.Left >= 0 && box.Top >= 0 && box.Right <= root.ActualWidth && box.Bottom <= root.ActualHeight, $"The title at {box} is outside the window.");
            var list = (FrameworkElement)window.FindName("ContainersList");
            foreach (var name in new[] { "UndoButton", "RedoButton", "RevertButton" })
            {
                var button = Named(window, name);
                Assert.True(WpfHost.IsVisibleInLayout(button));
                var b = button.TransformToAncestor(root).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                Assert.False(b.IntersectsWith(box) && b.Width > 0 && b.Height > 0 && Rect.Intersect(b, box).Width > 0.5, $"{name} covers the title.");
                var l = list.TransformToAncestor(root).TransformBounds(new Rect(0, 0, list.ActualWidth, list.ActualHeight));
                Assert.True(b.Right <= l.Right + 0.5, $"{name} at {b} is wider than the list at {l}.");
            }

            return Task.CompletedTask;
        });

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task G5b2_2B_At820x560_ThePartColumnShowsAtLeast14Characters(WpfTheme theme)
        => WithScannedWindow(theme, 820, 560, (window, vm) =>
        {
            var list = (ListBox)window.FindName("ContainersList");
            var shown = WpfHost.Descendants<TextBlock>(list).Where(t => t.Name == "PartText" && t.ActualWidth > 0).ToList();
            Assert.NotEmpty(shown);
            var part = shown.First();
            const string text = "abcdefghijklmn";
            var longest = shown.Select(t => t.Text).Where(t => t.Length >= 14).Select(t => t[..14]).DefaultIfEmpty(text);
            foreach (var first14 in longest)
            {
                var formatted = new FormattedText(first14, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(part.FontFamily, part.FontStyle, part.FontWeight, part.FontStretch), part.FontSize, part.Foreground,
                    VisualTreeHelper.GetDpi(part).PixelsPerDip);
                Assert.True(formatted.WidthIncludingTrailingWhitespace <= part.ActualWidth + 0.5,
                    $"The Part column is {part.ActualWidth:0} px; \"{first14}\" needs {formatted.WidthIncludingTrailingWhitespace:0} px.");
            }

            Assert.True(part.TextTrimming == TextTrimming.CharacterEllipsis);
            Assert.Equal(part.Text, part.ToolTip);
            return Task.CompletedTask;
        });

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task G5b2_2C_TheListSplitter_MovesBothPanes_AndKeepsTheMinimums(WpfTheme theme)
        => WithScannedWindow(theme, 820, 560, async (window, vm) =>
        {
            var grid = (Grid)window.FindName("MiddleGrid");
            var splitter = (GridSplitter)window.FindName("ListSplitter");
            Assert.Equal(GridResizeBehavior.PreviousAndNext, splitter.ResizeBehavior);
            double Left() => grid.ColumnDefinitions[0].ActualWidth;
            double Right() => grid.ColumnDefinitions[2].ActualWidth;
            Assert.True(Left() >= 300 && Right() >= 360, $"Start: list {Left():0}, tabs {Right():0}.");

            async Task Drag(double dx)
            {
                splitter.RaiseEvent(new DragStartedEventArgs(0, 0));
                splitter.RaiseEvent(new DragDeltaEventArgs(dx, 0));
                splitter.RaiseEvent(new DragCompletedEventArgs(dx, 0, false));
                await WpfHost.Settle();
                window.UpdateLayout();
            }

            var (left, right) = (Left(), Right());
            await Drag(-40);
            Assert.True(Left() < left && Right() > right, $"After -40: list {Left():0} (was {left:0}), tabs {Right():0} (was {right:0}).");
            Assert.Equal(left - 40, Left(), 1);
            Assert.Equal(right + 40, Right(), 1);

            (left, right) = (Left(), Right());
            await Drag(40);
            Assert.True(Left() > left && Right() < right);

            await Drag(-1000);
            Assert.Equal(300, Left(), 1);
            await Drag(2000);
            Assert.Equal(360, Right(), 1);
            Assert.True(Left() >= 300);
        });

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task G5b2_2D_At1100x720_TheListIsAsBefore_AndTheTabsAreWider(WpfTheme theme)
        => WithScannedWindow(theme, 1100, 720, (window, vm) =>
        {
            var grid = (Grid)window.FindName("MiddleGrid");
            Assert.Equal(380, grid.ColumnDefinitions[0].ActualWidth, 1);
            Assert.True(grid.ColumnDefinitions[2].ActualWidth >= 600, $"The tabs are {grid.ColumnDefinitions[2].ActualWidth:0} px.");
            Assert.Equal(0, ((ScrollViewer)window.FindName("EditScroll")).ScrollableHeight, 1);
            return Task.CompletedTask;
        });
    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task G5b2_3A_ModeChange_AnsweredNo_TheModeBoxShowsTheOldModeAgain(WpfTheme theme)
    {
        var questions = new List<string>();
        return WithEditTab(theme, 1100, 720, async (window, vm) =>
        {
            await Scan(vm, window);
            await SelectTable(vm, window);
            vm.NewName = "Results";
            await Click(window, "RenameButton");
            Assert.True(vm.Session!.IsModified);

            var modeBox = (ComboBox)window.FindName("ModeBox");
            modeBox.SelectedValue = FileIntakeNodeSettings.MarkersMode;
            await WpfHost.Settle();
            window.UpdateLayout();

            Assert.Equal([IntakeStudioViewModel.DiscardQuestion], questions);
            Assert.Equal(FileIntakeNodeSettings.AutoDetect, vm.Mode);
            Assert.Equal(FileIntakeNodeSettings.AutoDetect, modeBox.SelectedValue);
            Assert.Equal("Auto-detect", ((SettingChoice)modeBox.SelectedItem).Label);
            Assert.Contains(vm.Containers, r => r.Info.Name == "Results");
        }, () => new IntakeStudioViewModel(new FileIntakeNodeSettings { Path = IntakeTestData.PathOf("i01_sample") },
            confirm: q => { questions.Add(q); return false; }));
    }
}
