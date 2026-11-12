using System.IO;
using System.Windows;
using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>Task G6c step 4: the CSV part node in the real main window (STA, both themes, no binding errors).</summary>
[Collection(WpfCollection.Name)]
public sealed class CsvPartCanvasWindowTests : IDisposable
{
    private readonly WpfHost _wpf;
    private readonly ITestOutputHelper _output;
    private readonly string _folder = Directory.CreateTempSubdirectory("g6c_window_").FullName;

    public CsvPartCanvasWindowTests(WpfHost wpf, ITestOutputHelper output)
    {
        _wpf = wpf;
        _output = output;
    }

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private string I10Copy()
    {
        var path = Path.Combine(_folder, "i10.txt");
        File.Copy(G6bParts.I10Path, path, overwrite: true);
        return path;
    }

    /// <summary>Builds the four nodes in a main window, waits for the part read, lays out at the size and runs <paramref name="check"/>.</summary>
    private async Task WithCanvas(WpfTheme theme, double width, double height, bool broken, Func<MainWindow, G6cCanvas, Task> check)
    {
        var path = I10Copy();
        var selector = await CsvPartLocatorTests.SelectorAsync(path);
        if (broken)
        {
            // The table lost a column: the locator stops with its column-count sentence.
            var lines = File.ReadAllLines(path);
            File.WriteAllLines(path, lines.Select((l, i) => i >= 21 && i < 27 ? l[..l.LastIndexOf(',')] : l));
        }

        await _wpf.RunAsync(async () =>
        {
            using var trace = BindingTrace.Start();
            var (window, main, _) = IntakeStudioOpenTests.NewMainWindow();
            IntakeStudioOpenTests.SwitchTheme(window, theme);
            try
            {
                var canvas = G6cCanvas.Build(path, selector, main: main);
                await canvas.SettleAsync();
                main.Editor.SelectedNodes.Add(canvas.Part);
                WpfHost.Layout(window, width, height);
                await WpfHost.Settle();
                window.UpdateLayout();
                await check(window, canvas);
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
    public Task CsvPartNode_ShowsPart_Template_Columns_AndReady(WpfTheme theme)
        => WithCanvas(theme, 1100, 720, broken: false, (window, canvas) =>
        {
            var texts = WpfHost.VisibleTexts(IntakeStudioOpenTests.CanvasNode(window, canvas.Part));
            _output.WriteLine(string.Join(" | ", texts));
            Assert.Equal(NodeFactory.CsvPartTitle, texts[0]);
            Assert.Contains(canvas.Part.CsvPartText!, texts);
            Assert.Contains(EditorViewModel.CsvPartReady, texts);
            Assert.Contains(NodeFactory.CsvPartSubtitle, texts);
            Assert.Contains("Parts", WpfHost.VisibleTexts(IntakeStudioOpenTests.CanvasNode(window, canvas.Intake)));
            // The settings panel repeats the read-only lines for the selected CSV part.
            Assert.True(WpfHost.VisibleTexts(window).Count(t => t == canvas.Part.CsvPartText) >= 2);
            return Task.CompletedTask;
        });

    [Fact]
    public Task CsvPartNode_InAnErrorState_ShowsTheLocatorSentence()
        => WithCanvas(WpfTheme.Light, 1100, 720, broken: true, (window, canvas) =>
        {
            var texts = WpfHost.VisibleTexts(IntakeStudioOpenTests.CanvasNode(window, canvas.Part));
            _output.WriteLine(string.Join(" | ", texts));
            Assert.StartsWith("The CSV part", canvas.Part.CsvPartStatus);
            Assert.Contains(canvas.Part.CsvPartStatus!, texts);
            // G6d: the generic "Idle | rows: 0" and "Configured" lines carry nothing for a CSV part, so they are hidden.
            Assert.DoesNotContain("Not configured", texts);
            Assert.DoesNotContain("Idle", texts);
            return Task.CompletedTask;
        });

    /// <summary>G6d step 0: with the same node positions, the error node is as wide as the ready node and does not reach ParseValue.</summary>
    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public async Task CsvPartNode_InAnErrorState_KeepsItsWidth_AndDoesNotOverlapParseValue(WpfTheme theme)
    {
        var widths = new List<double>();
        foreach (var broken in new[] { false, true })
        {
            await WithCanvas(theme, 1100, 720, broken, (window, canvas) =>
            {
                var part = IntakeStudioOpenTests.CanvasNode(window, canvas.Part);
                var parse = IntakeStudioOpenTests.CanvasNode(window, canvas.Parse);
                var output = IntakeStudioOpenTests.CanvasNode(window, canvas.Output);
                var partBox = part.TransformToVisual(window).TransformBounds(new Rect(part.RenderSize));
                var parseBox = parse.TransformToVisual(window).TransformBounds(new Rect(parse.RenderSize));
                var outputBox = output.TransformToVisual(window).TransformBounds(new Rect(output.RenderSize));
                _output.WriteLine($"{theme} broken={broken}: part {partBox}, parse {parseBox}, output {outputBox}");
                Assert.True(partBox.Right < parseBox.Left, $"The CSV part node ({partBox.Right:0}) reaches ParseValue ({parseBox.Left:0}).");
                Assert.True(parseBox.Right < outputBox.Left, "ParseValue reaches DotNetObject.");
                widths.Add(part.ActualWidth);
                if (broken)
                {
                    // The sentence wraps inside the node; its full text is the tooltip.
                    var status = WpfHost.Descendants<System.Windows.Controls.TextBlock>(part).Single(t => t.Text == canvas.Part.CsvPartStatus);
                    Assert.Equal(canvas.Part.CsvPartStatus, status.ToolTip);
                    Assert.True(status.ActualWidth <= (double)Application.Current.FindResource("node-note-width"));
                }

                return Task.CompletedTask;
            });
        }

        Assert.Equal(widths[0], widths[1]);
    }

    /// <summary>The G6d error-node pictures (only with INTAKE_SCREENS=1), in reports/g6d, with the node positions of the ready picture.</summary>
    [ScreensTheory]
    [InlineData(WpfTheme.Light, false)]
    [InlineData(WpfTheme.Dark, false)]
    [InlineData(WpfTheme.Light, true)]
    [InlineData(WpfTheme.Dark, true)]
    public Task G6d_CanvasScreens_ForTheReport(WpfTheme theme, bool broken)
        => WithCanvas(theme, 1100, 720, broken, (window, _) =>
        {
            var path = Path.Combine(WpfHost.ReportsFolder(), "g6d", $"g6d-canvas-{(broken ? "error" : "ready")}-{theme.ToString().ToLowerInvariant()}-1100.png");
            WpfHost.RenderPng(window, 96, path);
            Assert.True(WpfHost.DistinctColours(path) > 5, $"{path} looks blank.");
            return Task.CompletedTask;
        });

    /// <summary>The G6c pictures for the report (only with INTAKE_SCREENS=1), in reports/g6c.</summary>
    [ScreensTheory]
    [InlineData(WpfTheme.Light, false)]
    [InlineData(WpfTheme.Dark, false)]
    [InlineData(WpfTheme.Light, true)]
    public Task G6c_Screens_ForTheReport(WpfTheme theme, bool broken)
        => WithCanvas(theme, 1100, 720, broken, (window, _) =>
        {
            var folder = Directory.CreateDirectory(Path.Combine(WpfHost.ReportsFolder(), "g6c")).FullName;
            var path = Path.Combine(folder, $"g6c-canvas-{(broken ? "error" : "ready")}-{theme.ToString().ToLowerInvariant()}-1100.png");
            WpfHost.RenderPng(window, 96, path);
            Assert.True(WpfHost.DistinctColours(path) > 5, $"{path} looks blank.");
            return Task.CompletedTask;
        });
}
