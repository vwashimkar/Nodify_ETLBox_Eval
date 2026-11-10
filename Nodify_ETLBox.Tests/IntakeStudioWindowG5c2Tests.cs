using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.ViewModels;
using Nodify_ETLBox.UI;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>G5c2: the Templates tab and the banner in the real Intake Studio window. Temp store and lineage.</summary>
public sealed partial class IntakeStudioWindowTests
{
    private static readonly string[] TemplateControls =
        ["TemplatesList", "TemplateNameBox", "SaveTemplateButton", "RenameTemplateButton", "ApplyTemplateButton", "DeleteTemplateButton"];

    /// <summary>The window with the Templates tab open, a temp store (optionally with the i01 template), on a copy of i01.</summary>
    private Task WithTemplatesTab(WpfTheme theme, double width, double height, bool withTemplate, Func<IntakeStudioWindow, IntakeStudioViewModel, Task> check,
        bool templatesTab = true, int moreTemplates = 0)
    {
        var folder = Directory.CreateTempSubdirectory("g5c2-window-").FullName;
        var store = new FileTemplateStore(new TemplateSettings { Folder = Path.Combine(folder, "templates") }, Path.Combine(folder, "fallback"));
        var copy = Path.Combine(folder, "i01_copy.txt");
        File.Copy(IntakeTestData.PathOf("i01_sample"), copy);
        return _wpf.RunAsync(async () =>
        {
            try
            {
                if (withTemplate)
                {
                    await SaveI01Template(store, copy, "i01 edited");
                }

                for (var i = 0; i < moreTemplates; i++)
                {
                    var json = Path.Combine(folder, $"json{i}.txt");
                    File.WriteAllText(json, "{\n  \"a\": 1\n}\n");
                    await SaveI01Template(store, json, "Other layout");
                }

                WpfHost.SetTheme(theme);
                using var trace = BindingTrace.Start();
                var vm = new IntakeStudioViewModel(new FileIntakeNodeSettings { Path = copy }, lineage: new IntakeStudioOpenTests.MemoryLineage(), templates: store);
                var window = new IntakeStudioWindow { DataContext = vm };
                try
                {
                    WpfHost.Layout(window, width, height);
                    if (templatesTab)
                    {
                        ((TabControl)window.FindName("DetailsTabs")).SelectedItem = window.FindName("TemplatesTab");
                    }

                    await WpfHost.Settle();
                    window.UpdateLayout();
                    await check(window, vm);
                    Assert.Empty(trace.Messages);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                ProjectEditorTests.DeleteTempFolder(folder);
            }
        });
    }

    /// <summary>Scans <paramref name="path"/>, renames its first table "Results table" when there is one, saves the layout as <paramref name="name"/>.</summary>
    private static async Task SaveI01Template(ITemplateStore store, string path, string name)
    {
        var vm = new IntakeStudioViewModel(new FileIntakeNodeSettings { Path = path }, lineage: new IntakeStudioOpenTests.MemoryLineage(), templates: store);
        await IntakeStudioTemplateTests.Scan(vm);
        if (vm.Containers.FirstOrDefault(r => r.Info.Format == "csv") is { } table)
        {
            vm.SelectedContainer = table;
            vm.NewName = "Results table";
            vm.RenameCommand.Execute(null);
        }

        vm.TemplateName = name;
        await IntakeStudioTemplateTests.Run(vm, () => vm.SaveTemplateCommand.Execute(null));
        Assert.StartsWith("Saved", vm.TemplateStatus);
    }

    private static async Task ScanWithSuggestion(IntakeStudioViewModel vm, Window window)
    {
        await Scan(vm, window);
        if (vm.SuggestTask is { } suggest)
        {
            await suggest;
        }

        await WpfHost.Settle();
        window.UpdateLayout();
    }

    private static async Task ClickAndWait(Window window, IntakeStudioViewModel vm, string name)
    {
        await Click(window, name);
        if (vm.TemplateTask is { } task)
        {
            await task;
        }

        await WpfHost.Settle();
        window.UpdateLayout();
    }

    private static Rect InWindow(Window window, string name)
    {
        var control = Named(window, name);
        return control.TransformToAncestor(window).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
    }

    [Theory]
    [InlineData(WpfTheme.Light, 1100, 720)]
    [InlineData(WpfTheme.Dark, 1100, 720)]
    [InlineData(WpfTheme.Light, 820, 560)]
    [InlineData(WpfTheme.Dark, 820, 560)]
    public Task G5c2_A_TheTemplatesTabExists_AndShowsNoTypeNames(WpfTheme theme, double width, double height)
        => WithTemplatesTab(theme, width, height, withTemplate: true, async (window, vm) =>
        {
            Assert.Equal("Templates", ((TabItem)window.FindName("TemplatesTab")).Header);
            Assert.Equal(["Details", "Edit", "Templates"], ((TabControl)window.FindName("DetailsTabs")).Items.Cast<TabItem>().Select(t => (string)t.Header));
            await ScanWithSuggestion(vm, window);
            foreach (var text in WpfHost.Descendants<TextBlock>(window).Where(WpfHost.IsVisibleInLayout).Select(t => t.Text))
            {
                Assert.DoesNotContain("{", text);
                Assert.DoesNotContain("IntakeTemplate", text);
                Assert.DoesNotContain("EtlDesigner", text);
            }
        });

    [Theory]
    [InlineData(WpfTheme.Light, 1100, 720)]
    [InlineData(WpfTheme.Dark, 1100, 720)]
    [InlineData(WpfTheme.Light, 820, 560)]
    [InlineData(WpfTheme.Dark, 820, 560)]
    public Task G5c2_B_BeforeAScan_EveryControlOfTheTabIsDisabled(WpfTheme theme, double width, double height)
        => WithTemplatesTab(theme, width, height, withTemplate: true, (window, vm) =>
        {
            Assert.Single(vm.Templates);
            foreach (var name in TemplateControls)
            {
                Assert.False(Named(window, name).IsEnabled, $"{name} is enabled before a scan.");
            }

            Assert.False(WpfHost.IsVisibleInLayout(Named(window, "TemplateBanner")));
            return Task.CompletedTask;
        });

    [Theory]
    [InlineData(WpfTheme.Light, 1100, 720)]
    [InlineData(WpfTheme.Dark, 1100, 720)]
    [InlineData(WpfTheme.Light, 820, 560)]
    [InlineData(WpfTheme.Dark, 820, 560)]
    public Task G5c2_C_AfterAScan_SaveAddsARow_AndSelectsIt(WpfTheme theme, double width, double height)
        => WithTemplatesTab(theme, width, height, withTemplate: false, async (window, vm) =>
        {
            await ScanWithSuggestion(vm, window);
            ((TextBox)window.FindName("TemplateNameBox")).Text = "Lab A";
            await WpfHost.Settle();
            Assert.True(Named(window, "SaveTemplateButton").IsEnabled);
            await ClickAndWait(window, vm, "SaveTemplateButton");
            var list = (ListBox)window.FindName("TemplatesList");
            Assert.Single(list.Items);
            Assert.Same(vm.Templates[0], list.SelectedItem);
            Assert.Equal("Lab A", vm.Templates[0].Name);
        });

    [Theory]
    [InlineData(WpfTheme.Light, 1100, 720)]
    [InlineData(WpfTheme.Dark, 1100, 720)]
    [InlineData(WpfTheme.Light, 820, 560)]
    [InlineData(WpfTheme.Dark, 820, 560)]
    public Task G5c2_D_TheBannerShowsTheExactSentence_ApplyChangesTheList_AndEnablesUndo(WpfTheme theme, double width, double height)
        => WithTemplatesTab(theme, width, height, withTemplate: true, async (window, vm) =>
        {
            await ScanWithSuggestion(vm, window);
            Assert.True(WpfHost.IsVisibleInLayout(Named(window, "TemplateBanner")));
            Assert.Equal("A saved layout, 'i01 edited', matches this file.", ((TextBlock)window.FindName("BannerText")).Text);
            Assert.False(Named(window, "UndoButton").IsEnabled);
            Assert.DoesNotContain(vm.Containers, r => r.Info.Name == "Results table");

            await ClickAndWait(window, vm, "BannerApplyButton");
            Assert.Contains(vm.Containers, r => r.Info.Name == "Results table");
            Assert.False(WpfHost.IsVisibleInLayout(Named(window, "TemplateBanner")));
            Assert.True(Named(window, "UndoButton").IsEnabled);
        });

    [Theory]
    [InlineData(WpfTheme.Light, 1100, 720)]
    [InlineData(WpfTheme.Dark, 1100, 720)]
    [InlineData(WpfTheme.Light, 820, 560)]
    [InlineData(WpfTheme.Dark, 820, 560)]
    public Task G5c2_E_NotNow_HidesTheBanner_AndItStaysHidden(WpfTheme theme, double width, double height)
        => WithTemplatesTab(theme, width, height, withTemplate: true, async (window, vm) =>
        {
            await ScanWithSuggestion(vm, window);
            await ClickAndWait(window, vm, "BannerDismissButton");
            Assert.False(WpfHost.IsVisibleInLayout(Named(window, "TemplateBanner")));
            await Select(vm, window, 2);
            await Select(vm, window, 5);
            Assert.False(WpfHost.IsVisibleInLayout(Named(window, "TemplateBanner")));
        }, templatesTab: false);

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task G5c2_F_At820x560_TheBannerDoesNotOverlapTheTotalsOrTheButtons(WpfTheme theme)
        => WithTemplatesTab(theme, 820, 560, withTemplate: true, async (window, vm) =>
        {
            await ScanWithSuggestion(vm, window);
            Assert.True(WpfHost.IsVisibleInLayout(Named(window, "TemplateBanner")));
            var banner = InWindow(window, "TemplateBanner");
            foreach (var other in new[] { "TotalsText", "SaveButton", "CloseButton" })
            {
                var box = InWindow(window, other);
                Assert.True(WpfHost.IsVisibleInLayout(Named(window, other)), $"{other} is not visible.");
                var overlap = Rect.Intersect(banner, box);
                Assert.True(overlap.IsEmpty || overlap.Width < 0.5 || overlap.Height < 0.5, $"The banner {banner} overlaps {other} {box}.");
            }

            Assert.True(banner.Bottom <= window.ActualHeight + 0.5, $"The banner {banner} is cut off.");
            foreach (var button in new[] { "BannerApplyButton", "BannerDismissButton" })
            {
                var box = InWindow(window, button);
                Assert.True(box.Left >= banner.Left - 0.5 && box.Right <= banner.Right + 0.5, $"{button} {box} is outside the banner {banner}.");
            }
        });

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task G5c2_G_At1100x720_EveryControlOfTheTabIsInsideIt(WpfTheme theme)
        => WithTemplatesTab(theme, 1100, 720, withTemplate: true, async (window, vm) =>
        {
            await ScanWithSuggestion(vm, window);
            var panel = (FrameworkElement)window.FindName("TemplatesPanel");
            var bounds = panel.TransformToAncestor(window).TransformBounds(new Rect(0, 0, panel.ActualWidth, panel.ActualHeight));
            foreach (var name in TemplateControls)
            {
                var box = InWindow(window, name);
                Assert.True(Named(window, name).ActualHeight > 0, $"{name} has no height.");
                Assert.True(box.Left >= bounds.Left - 0.5 && box.Top >= bounds.Top - 0.5 && box.Right <= bounds.Right + 0.5 && box.Bottom <= bounds.Bottom + 0.5,
                    $"{name} {box} is outside the tab {bounds}.");
            }

            Assert.True(Named(window, "TemplatesList").ActualHeight >= 40, "The list has almost no room.");
        });

    [Theory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public Task G5c2_DisabledButtons_LookDisabled(WpfTheme theme)
        => WithTemplatesTab(theme, 1100, 720, withTemplate: false, (window, vm) =>
        {
            var disabled = (Button)window.FindName("SaveTemplateButton");
            Assert.False(disabled.IsEnabled);
            var text = WpfHost.Descendants<TextBlock>(disabled).First();
            var enabled = (Button)window.FindName("CloseButton");
            var enabledText = WpfHost.Descendants<TextBlock>(enabled).First();
            Assert.NotEqual(((SolidColorBrush)enabledText.Foreground).Color, ((SolidColorBrush)text.Foreground).Color);
            return Task.CompletedTask;
        });

    /// <summary>The G5c2 pictures for the report (only with INTAKE_SCREENS=1).</summary>
    [ScreensTheory]
    [InlineData(WpfTheme.Light)]
    [InlineData(WpfTheme.Dark)]
    public async Task G5c2_Screens_ForTheReport(WpfTheme theme)
    {
        var folder = Path.Combine(WpfHost.ReportsFolder(), "screens");
        if (theme == WpfTheme.Dark)
        {
            await WithTemplatesTab(theme, 1100, 720, withTemplate: true, async (window, vm) =>
            {
                await ScanWithSuggestion(vm, window);
                Assert.Equal(2, vm.Templates.Count);
                Assert.Contains(vm.Templates, r => r.Match == "Exact");
                Save(window, Path.Combine(folder, "g5c2-templates-tab-dark-1100.png"));
            }, moreTemplates: 1);
            return;
        }

        await WithTemplatesTab(theme, 1100, 720, withTemplate: true, async (window, vm) =>
        {
            await ScanWithSuggestion(vm, window);
            Assert.True(vm.IsBannerVisible);
            Save(window, Path.Combine(folder, "g5c2-banner-light-1100.png"));
        }, templatesTab: false);
        await WithTemplatesTab(theme, 820, 560, withTemplate: true, async (window, vm) =>
        {
            await ScanWithSuggestion(vm, window);
            Assert.True(vm.IsBannerVisible);
            Save(window, Path.Combine(folder, "g5c2-banner-light-820.png"));
        }, templatesTab: false);

        static void Save(Window window, string path)
        {
            WpfHost.RenderPng(window, 96, path);
            Assert.True(WpfHost.DistinctColours(path) > 5, $"{path} looks blank.");
        }
    }
}
