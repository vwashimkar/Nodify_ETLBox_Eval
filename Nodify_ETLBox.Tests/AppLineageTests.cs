using System.Windows;
using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.Services;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>The shared lineage log in the test run: configured to a temp folder, never next to the test program.</summary>
[Collection(NodeFactoryHandlerCollection.Name)]
public sealed class AppLineageTests
{
    [Fact]
    public void AMainViewModelWithoutALog_WritesTheScanToTheTestFolder_NotNextToTheTestProgram()
    {
        var dialogs = new ProjectEditorTests.FakeDialogs();
        var main = new MainViewModel(dialogs, new ProjectEditorTests.MemoryRecent());
        main.Editor.AddNode(NodeType.FileIntake, new Point(10, 20));
        var node = main.Editor.Nodes.Last();
        node.SetFileIntakeSettings(new FileIntakeNodeSettings { Path = IntakeTestData.PathOf("i01_sample") });

        var expected = Path.Combine(TestLineageFolder.Folder, LineageLocation.FileName);
        var before = ScansOfI01(expected);

        dialogs.OnIntakeStudio = vm => Task.Run(vm.ScanAsync).Wait(TimeSpan.FromSeconds(30));
        main.ShowIntakeStudio(node);

        Assert.Equal(expected, AppLineage.Shared.FilePath);
        Assert.Equal(before + 1, ScansOfI01(expected));
        Assert.False(File.Exists(LineageLocation.DefaultPath()), "lineage.log was written next to the test program.");
    }

    private static int ScansOfI01(string path)
        => File.Exists(path) ? File.ReadAllLines(path).Count(line => line.Contains("\"intake.scanned\"") && line.Contains("i01_sample.txt")) : 0;
}
