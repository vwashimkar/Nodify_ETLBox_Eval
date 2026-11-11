using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>The text a node shows on the real canvas (G4c step 3).</summary>
[Collection(WpfCollection.Name)]
public sealed class CanvasNodeTextTests
{
    private readonly WpfHost _wpf;

    public CanvasNodeTextTests(WpfHost wpf) => _wpf = wpf;

    private static async Task<List<string>> TextsOf(MainWindow window, NodeViewModel node)
    {
        await IntakeStudioOpenTests.LayOut(window);
        return WpfHost.VisibleTexts(IntakeStudioOpenTests.CanvasNode(window, node));
    }

    private static void AssertNoGenericLines(List<string> texts)
    {
        Assert.DoesNotContain(texts, t => t.Contains("rows:", StringComparison.Ordinal));
        Assert.DoesNotContain("Idle", texts);
        Assert.DoesNotContain("Configured", texts);
    }

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task FileIntakeNode_ShowsTitle_Subtitle_FileName_ThenTheTotals(WpfTheme theme)
        => _wpf.RunAsync(async () =>
        {
            var (window, main, _) = IntakeStudioOpenTests.NewMainWindow();
            IntakeStudioOpenTests.SwitchTheme(window, theme);
            try
            {
                var node = IntakeStudioOpenTests.Add(main, NodeType.FileIntake);

                // G6c: the "Parts" output port label comes after the title.
                var empty = await TextsOf(window, node);
                Assert.Equal([node.Title, "Parts", node.Subtitle!, NodeViewModel.NoFileChosenText], empty);
                AssertNoGenericLines(empty);

                var path = IntakeTestData.PathOf("i01_sample");
                node.SetFileIntakeSettings(node.GetFileIntakeSettings() with { Path = path });
                var chosen = await TextsOf(window, node);
                Assert.Equal([node.Title, "Parts", node.Subtitle!, "i01_sample.txt"], chosen);
                Assert.Equal(path, node.CanvasNoteToolTip);
                AssertNoGenericLines(chosen);

                node.IntakeSummary = "27 containers, 3 warnings";
                var scanned = await TextsOf(window, node);
                Assert.Equal([node.Title, "Parts", node.Subtitle!, "i01_sample.txt", "27 containers, 3 warnings"], scanned);
                AssertNoGenericLines(scanned);

                // G5b: a saved layout adds one short line; removing it hides the line again.
                node.IntakePlanJson = IntakePlanJson.Serialize(new IntakePlan(IntakePlan.CurrentFormatVersion, "00", 1, null, IntakeMode.AutoDetect, []));
                var edited = await TextsOf(window, node);
                Assert.Equal([node.Title, "Parts", node.Subtitle!, "i01_sample.txt", "27 containers, 3 warnings", NodeViewModel.LayoutEditedText], edited);
                node.IntakePlanJson = null;
                Assert.Equal(scanned, await TextsOf(window, node));
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task CsvFileNode_KeepsItsStateRowsAndConfigurationLines()
        => _wpf.RunAsync(async () =>
        {
            var (window, main, _) = IntakeStudioOpenTests.NewMainWindow();
            try
            {
                var node = IntakeStudioOpenTests.Add(main, NodeType.CsvFile);

                var texts = await TextsOf(window, node);

                Assert.Contains(node.State.ToString(), texts);
                Assert.Contains(" | rows: ", texts);
                Assert.Contains(node.RowCount.ToString(System.Globalization.CultureInfo.CurrentCulture), texts);
                Assert.Contains(node.ConfigurationStatus, texts);
                Assert.DoesNotContain(NodeViewModel.NoFileChosenText, texts);
            }
            finally
            {
                window.Close();
            }
        });
}
