using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EtlDesigner.UI.ViewModels;
using Nodify_ETLBox.UI;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>
/// Task G7a: the "Build a reader" window on the STA thread, every step in Light and Dark, at its normal size and at its
/// minimum size (820 x 560 fits a 1280 x 720 logical screen, which is 1920 x 1080 at 150 % scaling), with no binding errors,
/// no text cut, and no node, port or JSON word on screen. Screens (INTAKE_SCREENS=1) at 96 and 144 DPI in reports/g7a.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class GuidedBuilderWindowTests : IDisposable
{
    private static readonly string[] ForbiddenWords = ["node", "nodes", "port", "ports", "JSON", "selector", "ParseValue", "DotNetObject", "container"];

    private readonly WpfHost _wpf;
    private readonly ITestOutputHelper _output;
    private readonly string _folder = Directory.CreateTempSubdirectory("g7a_window_").FullName;

    public GuidedBuilderWindowTests(WpfHost wpf, ITestOutputHelper output)
    {
        _wpf = wpf;
        _output = output;
    }

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private static bool Screens => Environment.GetEnvironmentVariable("INTAKE_SCREENS") == "1";

    private static Rect Bounds(FrameworkElement element, Visual relativeTo)
        => element.TransformToVisual(relativeTo).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    /// <summary>Inside the client area and as wide as it wants to be (so its text is not cut).</summary>
    private static void AssertNotClipped(FrameworkElement element, Window window)
    {
        var client = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        var box = Bounds(element, client);
        Assert.True(box.Left >= -0.5 && box.Top >= -0.5 && box.Right <= client.ActualWidth + 0.5 && box.Bottom <= client.ActualHeight + 0.5,
            $"{element.Name} at {box} is outside {client.ActualWidth:0} x {client.ActualHeight:0}.");
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var wanted = element.DesiredSize.Width - element.Margin.Left - element.Margin.Right;
        Assert.True(element.ActualWidth + 0.5 >= wanted, $"{element.Name} is cut: {element.ActualWidth:0} < {wanted:0}.");
    }

    private static async Task Show(Window window, double width, double height)
    {
        await WpfHost.Settle();
        WpfHost.Layout(window, width, height);
        await WpfHost.Settle();
        window.UpdateLayout();
    }

    /// <summary>The visible texts of the shown page and the bars (not the hidden pages), checked for engine words.</summary>
    private void AssertPlainWords(Window window, GuidedStep step)
    {
        var texts = WpfHost.VisibleTexts(window).Where(t => t.Length > 0).ToList();
        foreach (var text in texts)
        {
            foreach (var word in ForbiddenWords)
            {
                Assert.False(System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{word}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                    $"Step {step}: '{word}' in \"{text}\"");
            }
        }
    }

    private async Task CheckStep(Window window, GuidedBuilderViewModel vm, double width, double height, WpfTheme theme, params string[] buttons)
    {
        await Show(window, width, height);
        Assert.Equal((int)vm.Step - 1, ((TabControl)window.FindName("Pages")).SelectedIndex);
        foreach (var name in buttons.Append("BackButton"))
        {
            AssertNotClipped((FrameworkElement)window.FindName(name), window);
        }

        if (vm.Step != GuidedStep.Save)
        {
            AssertNotClipped((FrameworkElement)window.FindName("NextButton"), window);
        }

        AssertNotClipped((FrameworkElement)window.FindName("StepBar"), window);
        AssertPlainWords(window, vm.Step);
        if (Screens)
        {
            var name = $"g7a-step{(int)vm.Step}-{vm.Step.ToString().ToLowerInvariant()}-{theme.ToString().ToLowerInvariant()}-{width:0}";
            var folder = Path.Combine(WpfHost.ReportsFolder(), "g7a");
            WpfHost.RenderPng(window, 96, Path.Combine(folder, name + ".png"));
            WpfHost.RenderPng(window, 144, Path.Combine(folder, name + "-150.png"));
        }
    }

    [Theory]
    [InlineData(WpfTheme.Light, 1000, 680)]
    [InlineData(WpfTheme.Dark, 1000, 680)]
    [InlineData(WpfTheme.Light, 820, 560)]
    [InlineData(WpfTheme.Dark, 820, 560)]
    public Task EveryStep_Renders_WithoutBindingErrors_CutText_OrEngineWords(WpfTheme theme, int width, int height)
    {
        var path = G6bParts.Write(_folder, "i10.txt", G6bParts.Variant("more rows"));
        return _wpf.RunAsync(async () =>
        {
            WpfHost.SetTheme(theme);
            using var trace = BindingTrace.Start();
            var b = GuidedBuilderTests.New(Path.Combine(_folder, $"{theme}{width}"));
            var vm = b.Vm;
            var window = new GuidedBuilderWindow { DataContext = vm };
            try
            {
                await CheckStep(window, vm, width, height, theme, "BrowseButton");

                vm.FilePath = path;
                await vm.ScanTask!;
                await vm.TablesTask!;
                Assert.Equal("Read i10.txt: 3 parts found.", ((TextBlock)window.FindName("FileMessageText")).Text);
                await CheckStep(window, vm, width, height, theme, "BrowseButton", "FileMessageText");

                vm.NextCommand.Execute(null);
                await CheckStep(window, vm, width, height, theme, "AdvancedButton", "TableMessageText");
                Assert.Equal(GuidedBuilderViewModel.TableQuestion, ((TextBlock)window.FindName("TableMessageText")).Text);

                vm.NextCommand.Execute(null);
                vm.SplitEnabled = true;
                vm.SplitColumn = "sample_id";
                vm.SelectedSeparator = "other";
                vm.OtherSeparator = "-";
                await CheckStep(window, vm, width, height, theme, "SplitBox", "PartNamesBox");

                vm.NextCommand.Execute(null);
                vm.TryCommand.Execute(null);
                await vm.TryTask!;
                Assert.True(vm.TrySucceeded, vm.TryMessage);
                await CheckStep(window, vm, width, height, theme, "TryButton", "TryMessageText", "TryCountsLine", "TestOtherButton");

                vm.NextCommand.Execute(null);
                vm.Dll!.OutputFolder = Path.Combine(_folder, $"out{theme}{width}");
                vm.CreateDllCommand.Execute(null);
                await vm.CreateDllTask!;
                Assert.Equal(GuidedBuilderViewModel.LicenceNote, vm.SaveMessage);
                await CheckStep(window, vm, width, height, theme, "CreateDllButton", "SaveSetupButton", "OpenCanvasButton");

                _output.WriteLine($"{theme} {width}x{height}: {trace.Messages.Count} binding messages");
                Assert.Empty(trace.Messages);
            }
            finally
            {
                window.Close();
            }
        }, TimeSpan.FromSeconds(120));
    }

    [Fact]
    public Task TheGuidedButton_IsOnTheMainWindowToolbar()
        => _wpf.RunAsync(async () =>
        {
            var (window, main, _) = IntakeStudioOpenTests.NewMainWindow();
            try
            {
                await IntakeStudioOpenTests.LayOut(window);
                var button = (Button)window.FindName("GuidedButton");
                Assert.Equal("Guided", button.Content);
                Assert.Same(main.GuidedCommand, button.Command);
                Assert.True(button.IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
}
