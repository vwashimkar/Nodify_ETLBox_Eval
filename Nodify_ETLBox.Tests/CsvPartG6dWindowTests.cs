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
/// Task G6d in real windows (STA, both themes, no binding errors): the "Use in pipeline" button and its line in
/// Intake Studio at 820 x 560 and 1100 x 720, the canvas after it, the CSV part settings dialog and a
/// double-click on the CSV part node.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class CsvPartG6dWindowTests : IDisposable
{
    private readonly WpfHost _wpf;
    private readonly ITestOutputHelper _output;
    private readonly string _folder = Directory.CreateTempSubdirectory("g6d_window_").FullName;

    public CsvPartG6dWindowTests(WpfHost wpf, ITestOutputHelper output)
    {
        _wpf = wpf;
        _output = output;
    }

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private static bool Screens => Environment.GetEnvironmentVariable("INTAKE_SCREENS") == "1";

    private static string ScreenPath(string name) => Path.Combine(WpfHost.ReportsFolder(), "g6d", name);

    private static Rect Bounds(FrameworkElement element, Visual relativeTo)
        => element.TransformToVisual(relativeTo).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    /// <summary>Inside the window's client area and as wide as it wants to be (so its text is not cut).</summary>
    private static void AssertNotClipped(FrameworkElement element, Window window)
    {
        Assert.True(WpfHost.IsVisibleInLayout(element), $"{element.Name} is hidden.");
        var client = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        var box = Bounds(element, client);
        Assert.True(box.Left >= 0 && box.Top >= 0 && box.Right <= client.ActualWidth + 0.5 && box.Bottom <= client.ActualHeight + 0.5,
            $"{element.Name} at {box} is outside {client.ActualWidth:0} x {client.ActualHeight:0}.");
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var wanted = element.DesiredSize.Width - element.Margin.Left - element.Margin.Right;
        Assert.True(element.ActualWidth + 0.5 >= wanted, $"{element.Name} is cut: {element.ActualWidth:0} < {wanted:0}.");
    }

    [Theory]
    [InlineData(WpfTheme.Light, 820, 560)]
    [InlineData(WpfTheme.Dark, 820, 560)]
    [InlineData(WpfTheme.Light, 1100, 720)]
    [InlineData(WpfTheme.Dark, 1100, 720)]
    public Task Studio_UseInPipeline_DisabledOnKeyValue_ThenTheConfirmationLine_NothingClipped(WpfTheme theme, int width, int height)
    {
        var path = G6bParts.Write(_folder, "i10.txt", G6bParts.Variant("example"));
        return _wpf.RunAsync(async () =>
        {
            var (mainWindow, main, dialogs) = IntakeStudioOpenTests.NewMainWindow();
            WpfHost.SetTheme(theme);
            using var trace = BindingTrace.Start();
            var intake = G6cCanvas.Add(main.Editor, NodeType.FileIntake, 10);
            intake.SetFileIntakeSettings(intake.GetFileIntakeSettings() with { Path = path });
            IntakeStudioViewModel? vm = null;
            dialogs.OnIntakeStudio = studio => vm = studio;
            main.ShowIntakeStudio(intake);
            var window = new IntakeStudioWindow { DataContext = vm };
            try
            {
                WpfHost.Layout(window, width, height);
                await vm!.ScanAsync(); // On the UI thread; WpfHost.RunAsync has the time limit.
                await WpfHost.Settle();
                vm.SelectedContainer = vm.Containers[0];
                await WpfHost.Settle();
                window.UpdateLayout();

                var button = (Button)window.FindName("UseInPipelineButton");
                Assert.False(button.IsEnabled);
                Assert.Equal(vm.UseInPipelineToolTip, button.ToolTip);
                Assert.StartsWith("Part 1 is not a CSV or TSV table", (string)button.ToolTip);
                AssertNotClipped(button, window);
                AssertNotClipped((Button)window.FindName("SaveButton"), window);
                AssertNotClipped((Button)window.FindName("CloseButton"), window);

                var table = vm.Containers.First(r => r.Info.Format == "csv");
                vm.SelectedContainer = table;
                await WpfHost.Settle();
                window.UpdateLayout();
                Assert.True(button.IsEnabled);
                vm.UseInPipelineCommand.Execute(null);
                await WpfHost.Settle();
                window.UpdateLayout();

                var line = (TextBlock)window.FindName("StatusLine");
                Assert.Equal($"Part {table.Number} is now the CSV part of this pipeline.", line.Text);
                AssertNotClipped(line, window);
                Assert.Single(main.Editor.Nodes, n => n.NodeType == NodeType.CsvPart);
                if (Screens)
                {
                    WpfHost.RenderPng(window, 96, ScreenPath($"g6d-studio-use-{theme.ToString().ToLowerInvariant()}-{width}.png"));
                }

                Assert.Empty(trace.Messages);
                await TestTimeout.Run(() => main.CsvPartRefresh!);
            }
            finally
            {
                window.Close();
                mainWindow.Close();
            }
        });
    }

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task Canvas_AfterUseInPipeline_ShowsTheConnectedCsvPartNode_NextToTheFileIntake(WpfTheme theme)
    {
        var path = G6bParts.Write(_folder, "i10.txt", G6bParts.Variant("example"));
        return _wpf.RunAsync(async () =>
        {
            using var trace = BindingTrace.Start();
            var (window, main, dialogs) = IntakeStudioOpenTests.NewMainWindow();
            IntakeStudioOpenTests.SwitchTheme(window, theme);
            try
            {
                var intake = G6cCanvas.Add(main.Editor, NodeType.FileIntake, 10);
                intake.SetFileIntakeSettings(intake.GetFileIntakeSettings() with { Path = path });
                IntakeStudioViewModel? vm = null;
                dialogs.OnIntakeStudio = studio => vm = studio;
                main.ShowIntakeStudio(intake);
                await vm!.ScanAsync(); // On the UI thread; WpfHost.RunAsync has the time limit.
                vm.SelectedContainer = vm.Containers.First(r => r.Info.Format == "csv");
                vm.UseInPipelineCommand.Execute(null);
                await TestTimeout.Run(() => main.CsvPartRefresh!);
                var part = main.Editor.Nodes.Single(n => n.NodeType == NodeType.CsvPart);
                main.Editor.SelectedNodes.Add(part);
                WpfHost.Layout(window, 1100, 720);
                await WpfHost.Settle();
                window.UpdateLayout();

                var texts = WpfHost.VisibleTexts(IntakeStudioOpenTests.CanvasNode(window, part));
                _output.WriteLine(string.Join(" | ", texts));
                // No ParseValue yet ("Use in pipeline" adds only the CSV part), so the node asks for one; its columns are read.
                Assert.Contains(EditorViewModel.CsvPartNotToParseValue, texts);
                Assert.NotNull(part.KnownColumns);
                var intakeBox = Bounds(IntakeStudioOpenTests.CanvasNode(window, intake), window);
                var partBox = Bounds(IntakeStudioOpenTests.CanvasNode(window, part), window);
                Assert.True(intakeBox.Right < partBox.Left, $"File intake {intakeBox} overlaps the CSV part {partBox}.");
                if (Screens)
                {
                    WpfHost.RenderPng(window, 96, ScreenPath($"g6d-canvas-used-{theme.ToString().ToLowerInvariant()}-1100.png"));
                }

                Assert.Empty(trace.Messages);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public async Task SettingsDialog_ShowsTheFacts_NothingClipped(WpfTheme theme)
    {
        var path = G6bParts.Write(_folder, "i10.txt", G6bParts.Variant("example"));
        var (canvas, part) = await CsvPartUseInPipelineTests.UsedAsync(path);
        var vm = new CsvPartSettingsViewModel(part, path, hasFileIntake: true, canvas.Editor.CurrentCsvPartRead(part));
        await _wpf.RunAsync(async () =>
        {
            WpfHost.SetTheme(theme);
            using var trace = BindingTrace.Start();
            var window = new CsvPartSettingsWindow { DataContext = vm };
            try
            {
                WpfHost.Layout(window, 520, 480);
                window.SizeToContent = SizeToContent.Height;
                await WpfHost.Settle();
                window.UpdateLayout();

                var texts = WpfHost.VisibleTexts(window);
                _output.WriteLine(string.Join(" | ", texts));
                foreach (var expected in new[] { "i10.txt", "i10", vm.PartText, "Comma", vm.ColumnNames, vm.ExampleSha256, "All columns", EditorViewModel.CsvPartReady })
                {
                    Assert.Contains(expected, texts);
                }

                foreach (var name in new[] { "OpenStudioButton", "ChooseColumnsButton", "CloseButton" })
                {
                    var button = (Button)window.FindName(name);
                    Assert.True(button.IsEnabled, name);
                    AssertNotClipped(button, window);
                }

                if (Screens)
                {
                    WpfHost.RenderPng(window, 96, ScreenPath($"g6d-settings-{theme.ToString().ToLowerInvariant()}.png"));
                }

                Assert.Empty(trace.Messages);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public Task DoubleClickCommand_OnTheCsvPartNode_OpensTheSettingsDialog()
        => _wpf.RunAsync(() =>
        {
            var (window, main, dialogs) = IntakeStudioOpenTests.NewMainWindow();
            try
            {
                var part = G6cCanvas.Add(main.Editor, NodeType.CsvPart, 200);
                CsvPartSettingsViewModel? opened = null;
                dialogs.OnCsvPartSettings = vm => opened = vm;
                part.DoubleClickCommand!.Execute(null);
                Assert.NotNull(opened);
                Assert.Equal(CsvPartSettingsViewModel.NotSet, opened!.TemplateName);
                Assert.False(opened.OpenIntakeStudioCommand.CanExecute(null));
            }
            finally
            {
                window.Close();
            }
        });
}
