using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.Controls;
using EtlDesigner.UI.ViewModels;
using Nodify_ETLBox.UI;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>The Edit tab and the clickable Preview of the real Intake Studio window (G5b step 3).</summary>
public sealed partial class IntakeStudioWindowTests
{
    private static readonly string[] EditControls =
    [
        "FormatBox", "ChangeFormatButton", "DelimiterBox", "HeaderBox", "SkipLinesBox", "ApplyOptionsButton",
        "NameBox", "RenameButton", "SplitLineBox", "SplitButton", "MergeButton"
    ];

    private static readonly (double Width, double Height)[] Sizes = [(1100, 720), (820, 560)];

    /// <summary>The window at <paramref name="width"/> x <paramref name="height"/>, Edit tab open; the binding trace must stay empty.</summary>
    private Task WithEditTab(WpfTheme theme, double width, double height, Func<IntakeStudioWindow, IntakeStudioViewModel, Task> check,
        Func<IntakeStudioViewModel>? create = null)
        => _wpf.RunAsync(async () =>
        {
            WpfHost.SetTheme(theme);
            using var trace = BindingTrace.Start();
            var vm = create?.Invoke() ?? Studio();
            var window = new IntakeStudioWindow { DataContext = vm };
            try
            {
                WpfHost.Layout(window, width, height);
                ((TabControl)window.FindName("DetailsTabs")).SelectedItem = window.FindName("EditTab");
                await WpfHost.Settle();
                window.UpdateLayout();
                await check(window, vm);
                Assert.Empty(trace.Messages);
            }
            finally
            {
                window.Close();
            }
        });

    private static async Task SelectTable(IntakeStudioViewModel vm, Window window)
        => await Select(vm, window, vm.Containers.First(r => r.Info.Format == "csv" && r.Info.ContentLastLine - r.Info.ContentFirstLine >= 2).Number);

    private static FrameworkElement Named(Window window, string name) => (FrameworkElement)window.FindName(name);

    /// <summary>A real click through UI Automation (the same path as a screen reader), not a direct command call.</summary>
    private static async Task Click(Window window, string name)
    {
        var button = (Button)window.FindName(name);
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
        await WpfHost.Settle();
        window.UpdateLayout();
    }

    private static async Task ClickPreviewLine(Window window, int index)
    {
        var preview = (ListBox)window.FindName("PreviewBox");
        preview.SelectedItem = preview.Items[index];
        await WpfHost.Settle();
        window.UpdateLayout();
    }

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public async Task G5b_A_EditTabExists_NoTextShowsBracesOrTypeNames(WpfTheme theme)
    {
        foreach (var (width, height) in Sizes)
        {
            await WithEditTab(theme, width, height, async (window, vm) =>
            {
                await Scan(vm, window);
                await SelectTable(vm, window);
                ((TabControl)window.FindName("DetailsTabs")).SelectedItem = window.FindName("EditTab");
                await WpfHost.Settle();
                window.UpdateLayout();

                Assert.True(WpfHost.IsVisibleInLayout(Named(window, "EditPanel")));
                var texts = WpfHost.VisibleTexts(window);
                Assert.Contains("Edit", texts);
                Assert.Contains("Details", texts);
                Assert.Contains("CSV table", texts);
                Assert.DoesNotContain(texts, t => t.Contains('{') || t.Contains("SettingChoice", StringComparison.Ordinal)
                    || t.Contains("EtlDesigner", StringComparison.Ordinal) || t.Contains("IntakePreviewRow", StringComparison.Ordinal));
                foreach (var name in EditControls.Concat(["UndoButton", "RedoButton", "RevertButton", "PreviewBox"]))
                {
                    Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(Named(window, name))), $"{name} has no automation name.");
                }
            });
        }
    }

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task G5b_B_EditControls_DisabledBeforeAScan_EnabledForATable_SplitWaitsForALine(WpfTheme theme)
        => WithEditTab(theme, 1100, 720, async (window, vm) =>
        {
            foreach (var name in EditControls.Concat(["UndoButton", "RedoButton", "RevertButton"]))
            {
                Assert.False(Named(window, name).IsEnabled, $"{name} is enabled before a scan.");
            }

            await Scan(vm, window);
            await SelectTable(vm, window);
            foreach (var name in new[] { "FormatBox", "ChangeFormatButton", "DelimiterBox", "HeaderBox", "SkipLinesBox", "ApplyOptionsButton", "NameBox", "RenameButton", "MergeButton" })
            {
                Assert.True(Named(window, name).IsEnabled, $"{name} is disabled for a table.");
            }

            Assert.False(Named(window, "SplitButton").IsEnabled);
            Assert.False(Named(window, "UndoButton").IsEnabled);

            // A disabled control looks disabled: the box drawn with less opacity than an enabled one.
            Assert.True(Named(window, "SplitButton").Opacity < Named(window, "RenameButton").Opacity);
        });

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task G5b_C_ClickAPreviewLine_Split_ThenUndo(WpfTheme theme)
        => WithEditTab(theme, 1100, 720, async (window, vm) =>
        {
            await Scan(vm, window);
            await SelectTable(vm, window);
            var list = (ListBox)window.FindName("ContainersList");
            var rows = list.Items.Count;
            var totals = ((TextBlock)window.FindName("TotalsText")).Text;

            await ClickPreviewLine(window, 1);
            Assert.Equal(vm.PreviewRows[1].LineNumber, vm.SplitLine);
            Assert.Equal(vm.SplitLineText, ((TextBox)window.FindName("SplitLineBox")).Text);
            Assert.True(Named(window, "SplitButton").IsEnabled);

            await Click(window, "SplitButton");
            Assert.Equal(rows + 1, list.Items.Count);
            Assert.NotEqual(totals, ((TextBlock)window.FindName("TotalsText")).Text);
            Assert.True(Named(window, "UndoButton").IsEnabled);
            Assert.StartsWith("Split part", ((TextBlock)window.FindName("StatusLine")).Text);
            Assert.Equal("Layout: edited by you (1 change)", ((TextBlock)window.FindName("LayoutText")).Text);

            await Click(window, "UndoButton");
            Assert.Equal(rows, list.Items.Count);
            Assert.Equal(totals, ((TextBlock)window.FindName("TotalsText")).Text);
        });

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task G5b_D_AtTheMinimumSize_EveryEditControlCanBeReached(WpfTheme theme)
        => WithEditTab(theme, 820, 560, async (window, vm) =>
        {
            await Scan(vm, window);
            await SelectTable(vm, window);
            var scroll = (ScrollViewer)window.FindName("EditScroll");
            foreach (var name in EditControls)
            {
                var control = Named(window, name);
                control.BringIntoView();
                await WpfHost.Settle();
                window.UpdateLayout();
                var box = control.TransformToAncestor(scroll).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
                Assert.True(box.Top >= -0.5 && box.Bottom <= scroll.ViewportHeight + 0.5 && box.Right <= scroll.ViewportWidth + 0.5,
                    $"{name} at {box} is outside the Edit tab's viewport {scroll.ViewportWidth:0} x {scroll.ViewportHeight:0}.");
                Assert.True(control.ActualWidth > 0 && control.ActualHeight > 0, $"{name} has no size.");
            }
        });

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task G5b_E_CtrlZ_InTheNameBox_LeavesTheLayout_WithTheListFocused_UndoesIt(WpfTheme theme)
        => WithEditTab(theme, 1100, 720, async (window, vm) =>
        {
            await Scan(vm, window);
            await SelectTable(vm, window);
            await ClickPreviewLine(window, 1);
            await Click(window, "SplitButton");
            Assert.True(vm.Session!.IsModified);

            var nameBox = (TextBox)window.FindName("NameBox");
            // Like typing: a replaced selection is a step of the box's own undo (setting Text is not).
            nameBox.SelectAll();
            nameBox.SelectedText = "Typed name";
            Assert.False(LayoutUndoKeys.Handle(window, nameBox, Key.Z, ModifierKeys.Control));
            Assert.True(vm.Session.IsModified);
            Assert.True(nameBox.CanUndo);
            nameBox.Undo();
            Assert.NotEqual("Typed name", nameBox.Text);

            var list = (ListBox)window.FindName("ContainersList");
            Assert.True(LayoutUndoKeys.Handle(window, list, Key.Z, ModifierKeys.Control));
            await WpfHost.Settle();
            Assert.False(vm.Session.IsModified);
            Assert.True(LayoutUndoKeys.Handle(window, list, Key.Y, ModifierKeys.Control));
            Assert.True(vm.Session.IsModified);
        });

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task G5b_F_TheClickedPreviewLine_HasItsOwnBackground(WpfTheme theme)
        => WithEditTab(theme, 1100, 720, async (window, vm) =>
        {
            await Scan(vm, window);
            await SelectTable(vm, window);
            await ClickPreviewLine(window, 1);
            var preview = (ListBox)window.FindName("PreviewBox");
            Assert.True(preview.Items.Count >= 6);
            var lineHeight = preview.FontFamily.LineSpacing * preview.FontSize;
            Assert.True(preview.ActualHeight >= 6 * lineHeight, $"The preview is {preview.ActualHeight:0} px high.");

            Color Background(int index) => ((SolidColorBrush)((ListBoxItem)preview.ItemContainerGenerator.ContainerFromIndex(index)).Background).Color;
            var selected = Background(1);
            Assert.NotEqual(Background(0), selected);
            Assert.NotEqual(Background(2), selected);
            Assert.Equal(((SolidColorBrush)window.FindResource("accent-soft-brush")).Color, selected);
        });

    /// <summary>The G5b pictures for the report (only with INTAKE_SCREENS=1).</summary>
    [ScreensTheory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public async Task G5b_Screens_ForTheReport(WpfTheme theme)
    {
        var folder = Path.Combine(WpfHost.ReportsFolder(), "screens");
        if (theme == WpfTheme.Dark)
        {
            await WithEditTab(theme, 1100, 720, async (window, vm) =>
            {
                await Scan(vm, window);
                await SelectTable(vm, window);
                await ClickPreviewLine(window, 1);
                Save(window, Path.Combine(folder, "g5b-edit-tab-dark.png"));
            });
            return;
        }

        await WithEditTab(theme, 1100, 720, async (window, vm) =>
        {
            await Scan(vm, window);
            await SelectTable(vm, window);
            await ClickPreviewLine(window, 1);
            await Click(window, "SplitButton");
            ((ListBox)window.FindName("ContainersList")).ScrollIntoView(vm.SelectedContainer!);
            await WpfHost.Settle();
            window.UpdateLayout();
            Save(window, Path.Combine(folder, "g5b-after-split-light.png"));
        });
        await WithEditTab(theme, 820, 560, async (window, vm) =>
        {
            await Scan(vm, window);
            await SelectTable(vm, window);
            Save(window, Path.Combine(folder, "g5b-edit-tab-light-820.png"));
        });

        static void Save(Window window, string path)
        {
            WpfHost.RenderPng(window, 96, path);
            Assert.True(WpfHost.DistinctColours(path) > 5, $"{path} looks blank.");
        }
    }
}
