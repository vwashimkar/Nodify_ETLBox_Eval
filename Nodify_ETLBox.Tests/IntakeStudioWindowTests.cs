using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.ViewModels;
using Nodify_ETLBox.UI;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>The real Intake Studio window, laid out (not shown) on the STA test thread (G4c).</summary>
[Collection(WpfCollection.Name)]
public sealed partial class IntakeStudioWindowTests
{
    private static readonly string[] Parts =
    [
        "ScanButton", "CancelButton", "BrowseButton", "ModeBox", "EncodingBox", "ContainersList",
        "DetailsPanel", "PreviewBox", "TotalsText", "SaveButton", "CloseButton"
    ];

    private readonly WpfHost _wpf;

    public IntakeStudioWindowTests(WpfHost wpf) => _wpf = wpf;

    private static IntakeStudioViewModel Studio()
        => new(new FileIntakeNodeSettings { Path = IntakeTestData.PathOf("i01_sample") });

    internal static async Task Scan(IntakeStudioViewModel vm, Window window)
    {
        vm.ScanCommand.Execute(null);
        await vm.ScanTask!;
        await WpfHost.Settle();
        window.UpdateLayout();
    }

    internal static async Task Select(IntakeStudioViewModel vm, Window window, int number)
    {
        ((ListBox)window.FindName("ContainersList")).SelectedItem = vm.Containers.Single(r => r.Number == number);
        await vm.PreviewTask!;
        await WpfHost.Settle();
        window.UpdateLayout();
    }

    [Fact]
    public Task BindingTrace_SeesABindingToAMissingProperty()
        => _wpf.RunAsync(() =>
        {
            using var trace = BindingTrace.Start();
            var text = new TextBlock { DataContext = new object() };
            text.SetBinding(TextBlock.TextProperty, new Binding("NoSuchProperty"));
            WpfHost.Layout(text, 100, 20);

            Assert.Contains(trace.Messages, m => m.Contains("NoSuchProperty", StringComparison.Ordinal));
        });

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task Window_Constructs_AndEveryNamedPartHasASize(WpfTheme theme)
        => _wpf.RunAsync(async () =>
        {
            WpfHost.SetTheme(theme);
            using var trace = BindingTrace.Start();
            var window = new IntakeStudioWindow { DataContext = Studio() };
            try
            {
                foreach (var (width, height) in new[] { (1100.0, 720.0), (820.0, 560.0) })
                {
                    WpfHost.Layout(window, width, height);
                    await WpfHost.Settle();
                    foreach (var name in Parts)
                    {
                        var part = Assert.IsAssignableFrom<FrameworkElement>(window.FindName(name));
                        if (name == "CancelButton")
                        {
                            // Shown only half a second into a scan.
                            Assert.Equal(Visibility.Collapsed, part.Visibility);
                            continue;
                        }

                        Assert.True(part.ActualWidth > 0 && part.ActualHeight > 0, $"{name} has no size at {width} x {height} ({theme}). Window {window.ActualWidth} x {window.ActualHeight}; " + string.Join("; ", Parts.Select(n => $"{n} {((FrameworkElement)window.FindName(n)).ActualWidth:0} x {((FrameworkElement)window.FindName(n)).ActualHeight:0}")));
                    }
                }

                Assert.Empty(trace.Messages);
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>
    /// Screens for the report: i01 scanned (Auto-detect), container 12 selected, at 96 and 144 DPI, at
    /// 1100 x 720 and 820 x 560, in reports/screens. Parts that do not fit inside the window's client
    /// area are written to g4c-clipping-&lt;theme&gt;.txt next to them (not a failure: the report says it).
    /// </summary>
    [ScreensTheory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task Screens_ForTheReport(WpfTheme theme)
        => _wpf.RunAsync(async () =>
        {
            WpfHost.SetTheme(theme);
            var vm = Studio();
            var window = new IntakeStudioWindow { DataContext = vm };
            var folder = Path.Combine(WpfHost.ReportsFolder(), "screens");
            var clipping = new List<string>();
            try
            {
                WpfHost.Layout(window, 1100, 720);
                await Scan(vm, window);
                await Select(vm, window, 12);

                foreach (var (width, height) in new[] { (1100, 720), (820, 560) })
                {
                    WpfHost.Layout(window, width, height);
                    await WpfHost.Settle();
                    var client = (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetChild(window, 0);
                    foreach (var name in Parts.Where(n => n != "CancelButton"))
                    {
                        var part = (FrameworkElement)window.FindName(name);
                        var corner = part.TranslatePoint(new Point(part.ActualWidth, part.ActualHeight), client);
                        if (corner.X > client.ActualWidth + 0.5 || corner.Y > client.ActualHeight + 0.5)
                        {
                            clipping.Add($"{width} x {height}: {name} ends at {corner.X:0} x {corner.Y:0}, client area {client.ActualWidth:0} x {client.ActualHeight:0}");
                        }
                    }

                    foreach (var dpi in new[] { 96, 144 })
                    {
                        var path = Path.Combine(folder, $"g4c-studio-{theme.ToString().ToLowerInvariant()}-{dpi}-{width}x{height}.png");
                        WpfHost.RenderPng(window, dpi, path);

                        Assert.True(new FileInfo(path).Length < 400 * 1024, $"{path} is 400 KB or more.");
                        Assert.True(WpfHost.DistinctColours(path) > 5, $"{path} looks blank.");
                    }
                }

                if (theme == WpfTheme.Dark)
                {
                    // The SOAP fault container (26), with its issue line, Dark, 96 DPI, 1100 x 720.
                    WpfHost.Layout(window, 1100, 720);
                    await Select(vm, window, 26);
                    var fault = Path.Combine(folder, "g4d-studio-dark-fault.png");
                    WpfHost.RenderPng(window, 96, fault);
                    Assert.True(new FileInfo(fault).Length < 400 * 1024, $"{fault} is 400 KB or more.");
                    Assert.True(WpfHost.DistinctColours(fault) > 5, $"{fault} looks blank.");
                }

                File.WriteAllLines(Path.Combine(folder, $"g4c-clipping-{theme.ToString().ToLowerInvariant()}.txt"),
                    clipping.Count == 0 ? ["No named part ends outside the client area."] : clipping);
            }
            finally
            {
                window.Close();
            }
        });

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task ScanI01_FillsTheList_TheTotals_AndThePreview(WpfTheme theme)
        => _wpf.RunAsync(async () =>
        {
            WpfHost.SetTheme(theme);
            using var trace = BindingTrace.Start();
            var vm = Studio();
            var window = new IntakeStudioWindow { DataContext = vm };
            try
            {
                WpfHost.Layout(window, 1100, 720);
                var list = (ListBox)window.FindName("ContainersList");

                await Scan(vm, window);
                Assert.Equal(27, list.Items.Count);
                Assert.Equal("27 containers, 0 errors, 3 warnings", ((TextBlock)window.FindName("TotalsText")).Text);

                await Select(vm, window, 12);
                var row = vm.Containers.Single(r => r.Number == 12);
                // G5b: the Preview is a list of lines (a click chooses the split line), no longer one text box.
                var preview = string.Join(Environment.NewLine, ((ListBox)window.FindName("PreviewBox")).Items.Cast<IntakePreviewRow>().Select(r => r.Text));
                Assert.StartsWith(FormattableString.Invariant($"{row.Info.ContentFirstLine}  "), preview.TrimStart());

                ((ComboBox)window.FindName("ModeBox")).SelectedValue = FileIntakeNodeSettings.MarkersMode;
                Assert.Equal(FileIntakeNodeSettings.MarkersMode, vm.Mode);
                await Scan(vm, window);
                Assert.Equal(19, list.Items.Count);

                Assert.Empty(trace.Messages);
            }
            finally
            {
                window.Close();
            }
        });
}

/// <summary>The screens for a report: written only when INTAKE_SCREENS=1, otherwise skipped.</summary>
/// <summary>A test that uses the real Windows clipboard, which another program can hold; only with INTAKE_CLIPBOARD_TESTS=1.</summary>
public sealed class ClipboardTheoryAttribute : TheoryAttribute
{
    public ClipboardTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("INTAKE_CLIPBOARD_TESTS") != "1")
        {
            Skip = "uses the real Windows clipboard, which another program can hold: set INTAKE_CLIPBOARD_TESTS=1 to run it";
        }
    }
}

public sealed class ScreensTheoryAttribute : TheoryAttribute
{
    public ScreensTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("INTAKE_SCREENS") != "1")
        {
            Skip = "writes the report pictures: set INTAKE_SCREENS=1 to run it";
        }
    }
}
