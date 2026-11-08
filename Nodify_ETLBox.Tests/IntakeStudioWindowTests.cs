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
public sealed class IntakeStudioWindowTests
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
                var preview = ((TextBox)window.FindName("PreviewBox")).Text;
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
