using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.ViewModels;
using Nodify_ETLBox.UI;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>The five Intake Studio layout and theme bugs of G4d, checked on the real window.</summary>
public sealed partial class IntakeStudioWindowTests
{
    private static IntakeStudioViewModel StudioWithPicker()
        => new(new FileIntakeNodeSettings { Path = IntakeTestData.PathOf("i01_sample") }, pickFile: _ => null);

    /// <summary>Opens the window at the size, scans i01 (Auto-detect) and runs <paramref name="check"/> with zero binding messages.</summary>
    private Task WithScannedStudio(WpfTheme theme, double width, double height, Func<IntakeStudioWindow, IntakeStudioViewModel, Task> check)
        => _wpf.RunAsync(async () =>
        {
            WpfHost.SetTheme(theme);
            using var trace = BindingTrace.Start();
            var vm = StudioWithPicker();
            var window = new IntakeStudioWindow { DataContext = vm };
            try
            {
                WpfHost.Layout(window, width, height);
                await Scan(vm, window);
                await check(window, vm);
                Assert.Empty(trace.Messages);
            }
            finally
            {
                window.Close();
            }
        });

    private static T Part<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);

    private static ListBoxItem Row(Window window, IntakeContainerRow row)
        => (ListBoxItem)Part<ListBox>(window, "ContainersList").ItemContainerGenerator.ContainerFromItem(row);

    private static T Cell<T>(DependencyObject row, string name) where T : FrameworkElement
        => WpfHost.Descendants<T>(row).Single(e => e.Name == name);

    private static Rect Bounds(FrameworkElement element, Visual relativeTo)
        => element.TransformToVisual(relativeTo).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static double Luminance(Brush brush)
    {
        var colour = Assert.IsType<SolidColorBrush>(brush).Color;
        return (0.2126 * colour.R + 0.7152 * colour.G + 0.0722 * colour.B) / 255.0;
    }

    /// <summary>True when the style or one it is based on has the template of the app's ThemedButton (same Border with CornerRadius from the token).</summary>
    private static bool UsesThemedButton(Style? style)
    {
        var themed = (Style)Application.Current.FindResource("ThemedButton");
        var count = themed.Setters.Count;
        for (var current = style; current is not null; current = current.BasedOn)
        {
            if (current.TargetType == typeof(Button) && current.Setters.Count == count && current.Triggers.Count == themed.Triggers.Count
                && current.Setters.OfType<Setter>().Any(s => s.Property == Control.TemplateProperty))
            {
                return true;
            }
        }

        return false;
    }

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task A_ModeAndEncoding_ShowTheLabels_ClosedAndOpened(WpfTheme theme)
        => WithScannedStudio(theme, 1100, 720, async (window, _) =>
        {
            var mode = Part<ComboBox>(window, "ModeBox");
            var encoding = Part<ComboBox>(window, "EncodingBox");
            Assert.Equal(["Auto-detect"], WpfHost.VisibleTexts(mode));
            Assert.Equal(["Auto (UTF-8, else Windows-1252)"], WpfHost.VisibleTexts(encoding));

            var texts = WpfHost.VisibleTexts(window);
            foreach (var box in new[] { mode, encoding })
            {
                box.IsDropDownOpen = true;
                await WpfHost.Settle();
                box.UpdateLayout();
                var items = Enumerable.Range(0, box.Items.Count)
                    .Select(i => (ComboBoxItem)box.ItemContainerGenerator.ContainerFromIndex(i))
                    .ToList();
                Assert.All(items, Assert.NotNull);
                var itemTexts = items.SelectMany(WpfHost.VisibleTexts).ToList();
                Assert.Equal(box.Items.Count, itemTexts.Count);
                texts.AddRange(itemTexts);
                box.IsDropDownOpen = false;
                await WpfHost.Settle();
            }

            Assert.Contains("Markers", texts);
            Assert.DoesNotContain(texts, t => t.Contains("SettingChoice", StringComparison.Ordinal) || t.Contains('{'));
        });

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task B_Buttons_UseTheMainWindowsStyle_DarkIsDark_DisabledLooksDisabled(WpfTheme theme)
        => WithScannedStudio(theme, 1100, 720, async (window, vm) =>
        {
            // The style objects are compared by their template and setters: creating a main window reloads the
            // theme dictionary (its theme box), so the same "ThemedButton" key then gives a new Style object.
            var main = new MainWindow();
            IntakeStudioOpenTests.SwitchTheme(main, theme);
            try
            {
                var panelButton = (Button)main.FindName("OpenIntakeStudioButton");
                Assert.True(UsesThemedButton(panelButton.Style), "The main window's panel button is not based on ThemedButton.");
                var tokenBrush = Application.Current.FindResource("surface-alt-brush");
                Assert.Same(tokenBrush, panelButton.Background);
                foreach (var name in new[] { "ScanButton", "CloseButton", "BrowseButton" })
                {
                    Assert.True(UsesThemedButton(Part<Button>(window, name).Style), $"{name} is not ThemedButton.");
                    Assert.Same(tokenBrush, Part<Button>(window, name).Background);
                }
            }
            finally
            {
                main.Close();
            }

            foreach (var name in new[] { "ScanButton", "CloseButton", "BrowseButton" })
            {
                var button = Part<Button>(window, name);
                Assert.True(button.IsEnabled, $"{name} should be enabled after a scan.");
                if (theme == WpfTheme.Dark)
                {
                    Assert.True(Luminance(button.Background) < 0.5, $"{name} background is light in Dark ({Luminance(button.Background):0.00}).");
                    Assert.True(Luminance(button.Foreground) > 0.5, $"{name} text is dark in Dark.");
                }
            }

            var save = Part<Button>(window, "SaveButton");
            Assert.False(save.IsEnabled);
            var disabledForeground = ((SolidColorBrush)save.Foreground).Color;
            vm.Mode = FileIntakeNodeSettings.MarkersMode;
            await WpfHost.Settle();
            Assert.True(save.IsEnabled);
            Assert.NotEqual(disabledForeground, ((SolidColorBrush)save.Foreground).Color);
        });

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task C_Columns_DoNotRunTogether_HeadersSitOverTheirColumns_OneFont(WpfTheme theme)
        => WithScannedStudio(theme, 1100, 720, (window, vm) =>
        {
            var header = Part<TextBlock>(window, "LinesHeader");
            var checkedRows = 0;
            foreach (var row in vm.Containers)
            {
                if (Row(window, row) is not { } item || !item.IsVisible)
                {
                    continue;
                }

                checkedRows++;
                var part = Cell<TextBlock>(item, "PartText");
                var lines = Cell<TextBlock>(item, "LinesText");
                var status = Cell<TextBlock>(item, "StatusText");
                Assert.True(Bounds(part, window).Right < Bounds(lines, window).Left, $"Row {row.Number}: Part runs into Lines.");
                Assert.True(Math.Abs(Bounds(header, window).Left - Bounds(lines, window).Left) <= 6,
                    $"Row {row.Number}: Lines header at {Bounds(header, window).Left:0}, Lines text at {Bounds(lines, window).Left:0}.");
                Assert.Equal(part.FontFamily, lines.FontFamily);
                Assert.Equal(part.FontFamily, status.FontFamily);
                Assert.Equal(TextTrimming.CharacterEllipsis, part.TextTrimming);
                Assert.Equal(row.Part, part.ToolTip);
            }

            Assert.True(checkedRows >= 10, $"Only {checkedRows} rows were visible.");
            return Task.CompletedTask;
        });

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task D_Rows_AreCompact_OnlyIssueRowsHaveASecondLine(WpfTheme theme)
        => WithScannedStudio(theme, 1100, 720, async (window, vm) =>
        {
            var plain = vm.Containers.First(r => string.IsNullOrEmpty(r.IssueSummary));
            var fault = vm.Containers.Single(r => r.Number == 26);
            Assert.False(string.IsNullOrEmpty(fault.IssueSummary));
            var list = Part<ListBox>(window, "ContainersList");
            list.ScrollIntoView(fault);
            await WpfHost.Settle();
            window.UpdateLayout();

            var plainHeight = Row(window, plain).ActualHeight;
            var faultHeight = Row(window, fault).ActualHeight;
            Assert.True(plainHeight <= 28, $"A row without an issue is {plainHeight:0.0} px high.");
            Assert.True(faultHeight > plainHeight && faultHeight <= 52, $"The fault row is {faultHeight:0.0} px high (plain {plainHeight:0.0}).");

            WpfHost.Layout(window, 820, 560);
            list.ScrollIntoView(vm.Containers[0]);
            await WpfHost.Settle();
            window.UpdateLayout();
            var viewport = WpfHost.Descendants<ScrollViewer>(list).First();
            var inside = vm.Containers
                .Select(r => Row(window, r))
                .Where(i => i is not null && i.IsVisible)
                .Count(i =>
                {
                    var b = Bounds(i, viewport);
                    return b.Top >= -0.5 && b.Bottom <= viewport.ActualHeight + 0.5;
                });
            var root = (Grid)window.Content; Assert.True(inside >= 9, $"Only {inside} rows fit in the list at 820 x 560 (viewport {viewport.ViewportHeight:0}). Root {root.ActualHeight:0}: " + string.Join(", ", root.Children.OfType<FrameworkElement>().Select(c => $"row {Grid.GetRow(c)} {c.ActualHeight:0}")) + $"; details {Part<Border>(window, "DetailsPanel").ActualHeight:0}, preview {Part<Control>(window, "PreviewBox").ActualHeight:0}");
        });

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task E_SelectedRow_IsHighlighted_WithoutFocus(WpfTheme theme)
        => WithScannedStudio(theme, 1100, 720, async (window, vm) =>
        {
            await Select(vm, window, 12);
            Part<Button>(window, "ScanButton").Focus();
            await WpfHost.Settle();
            var list = Part<ListBox>(window, "ContainersList");
            Assert.False(list.IsKeyboardFocusWithin);

            var selected = Row(window, vm.Containers.Single(r => r.Number == 12));
            var other = Row(window, vm.Containers.Single(r => r.Number == 11));
            Assert.True(selected.IsSelected);
            var selectedColour = Assert.IsType<SolidColorBrush>(selected.Background).Color;
            Assert.NotEqual(selectedColour, (other.Background as SolidColorBrush)?.Color ?? Colors.Transparent);
            Assert.NotEqual(Colors.Transparent, selectedColour);
            Assert.Equal(((SolidColorBrush)Application.Current.FindResource("accent-soft-brush")).Color, selectedColour);
        });

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task F_AtTheMinimumSize_DetailsShowEightRows_PreviewSixLines_TheSplitterMoves(WpfTheme theme)
        => WithScannedStudio(theme, 820, 560, async (window, vm) =>
        {
            await Select(vm, window, 12);
            var details = Part<Border>(window, "DetailsPanel");
            var viewer = WpfHost.Descendants<ScrollViewer>(details).First();
            var rows = WpfHost.Descendants<TextBlock>(viewer)
                .Where(t => WpfHost.IsVisibleInLayout(t) && !string.IsNullOrEmpty(t.Text) && Grid.GetColumn(t) == 0)
                .Select(t => Bounds(t, viewer))
                .OrderBy(b => b.Top)
                .ToList();
            Assert.True(rows.Count >= 8, $"The selected container has only {rows.Count} detail rows.");
            Assert.All(rows.Take(8), b => Assert.True(b.Bottom <= viewer.ViewportHeight + 0.5,
                $"A detail row ends at {b.Bottom:0}, the Details viewport is {viewer.ViewportHeight:0} high."));

            var preview = Part<Control>(window, "PreviewBox");
            var lineHeight = preview.FontFamily.LineSpacing * preview.FontSize;
            Assert.True(preview.ActualHeight >= 6 * lineHeight, $"The preview is {preview.ActualHeight:0} px high, 6 lines need {6 * lineHeight:0}.");

            var splitter = Part<GridSplitter>(window, "DetailsSplitter");
            var detailsBefore = details.ActualHeight;
            var previewBefore = preview.ActualHeight;
            // At the minimum size Details sits at its minimum height, so the splitter moves down (into the Preview).
            var move = 40.0;
            splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            splitter.RaiseEvent(new DragDeltaEventArgs(0, move) { RoutedEvent = Thumb.DragDeltaEvent });
            splitter.RaiseEvent(new DragCompletedEventArgs(0, move, false) { RoutedEvent = Thumb.DragCompletedEvent });
            await WpfHost.Settle();
            window.UpdateLayout();

            Assert.True(Math.Sign(details.ActualHeight - detailsBefore) == Math.Sign(move), $"Details went from {detailsBefore:0} to {details.ActualHeight:0} for a move of {move:0}.");
            Assert.True(Math.Sign(preview.ActualHeight - previewBefore) == -Math.Sign(move), $"Preview went from {previewBefore:0} to {preview.ActualHeight:0} for a move of {move:0}.");
        });
}
