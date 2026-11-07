using System.Text;
using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.Services;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>Intake Studio without a window (G4b step 2).</summary>
public sealed class IntakeStudioViewModelTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("intake-studio-").FullName;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private static IntakeStudioViewModel Studio(string path, string mode = FileIntakeNodeSettings.AutoDetect, string encoding = IntakeEncodings.Auto)
        => new(new FileIntakeNodeSettings { Path = path, Mode = mode, Encoding = encoding });

    private static async Task Scan(IntakeStudioViewModel vm)
    {
        vm.ScanCommand.Execute(null);
        await TestTimeout.Run(() => vm.ScanTask!, TimeSpan.FromSeconds(60));
    }

    private static async Task<IntakeScanResult> ServiceScan(string path, IntakeMode mode)
        => await new IntakeScanService(new IntakeSettings { Mode = mode }).ScanAsync(new FileIntakeFactory().CreateItem(path), null, CancellationToken.None);

    private string LargeFile(int copies)
    {
        var path = Path.Combine(_folder, $"large-{Guid.NewGuid():N}.txt");
        var bytes = File.ReadAllBytes(IntakeTestData.PathOf("i01_sample"));
        using var stream = File.Create(path);
        for (var i = 0; i < copies; i++)
        {
            stream.Write(bytes);
        }

        return path;
    }

    [Theory]
    [InlineData(FileIntakeNodeSettings.AutoDetect, IntakeMode.AutoDetect, 27)]
    [InlineData(FileIntakeNodeSettings.MarkersMode, IntakeMode.Markers, 19)]
    public async Task ScanI01_FillsTheRows_AndTheTotals(string mode, IntakeMode intakeMode, int count)
    {
        var path = IntakeTestData.PathOf("i01_sample");
        var vm = Studio(path, mode);

        await Scan(vm);
        var expected = await ServiceScan(path, intakeMode);

        Assert.Null(vm.Message);
        Assert.Equal(count, vm.Containers.Count);
        Assert.Equal(expected.Intake!.Containers.Select(c => c.Order), vm.Containers.Select(r => r.Number));
        Assert.Equal($"{count} containers, {IntakeText.Count(expected.Validation!.Errors, "error")}, {IntakeText.Count(expected.Validation.Warnings, "warning")}", vm.TotalsText);
        Assert.Equal(100, vm.ProgressPercent);
        Assert.False(vm.IsScanning);
        Assert.Equal(vm.CurrentSettings, vm.ScannedSettings);
        Assert.Contains(vm.Containers, r => r.Part.Contains("SOAP", StringComparison.Ordinal) && r.Status == ContainerStatus.Warning && r.StatusText == "! Warning");
        Assert.All(vm.Containers, r => Assert.Matches(@"^(\d+( to \d+)?|empty)$", r.Lines));
    }

    [Fact]
    public async Task ScanI01_TotalsAndNodeSummary_ComeFromTheValidator()
    {
        var vm = Studio(IntakeTestData.PathOf("i01_sample"));

        await Scan(vm);

        Assert.Equal("27 containers, 0 errors, 3 warnings", vm.TotalsText);
        Assert.Equal("27 containers, 3 warnings", vm.Summary);
    }

    [Fact]
    public async Task SelectingARow_FillsDetailsAndPreview_WithFileLineNumbers()
    {
        var vm = Studio(IntakeTestData.PathOf("i01_sample"));
        await Scan(vm);
        var row = vm.Containers.First(r => r.Info.ContentFirstLine > 30 && r.Info.ContentLineCount > 2);

        vm.SelectedContainer = row;
        await TestTimeout.Run(() => vm.PreviewTask!);

        Assert.Equal(row.Info.ContentFirstLine, vm.PreviewLines[0].LineNumber);
        Assert.Equal(row.Info.ContentLineCount, vm.PreviewLines.Count);
        Assert.Equal(IntakeStudioViewModel.LinesText(row.Info), vm.Details.Single(d => d.Label == "Lines").Value);
        Assert.Equal(ContainerDisplay.Format(row.Info), vm.Details.Single(d => d.Label == "Format").Value);
        Assert.All(vm.Reasons, r => Assert.EndsWith(".", r));
        Assert.Null(vm.PreviewNote);

        var soap = vm.Containers.First(r => r.Part.Contains("SOAP", StringComparison.Ordinal) && r.Status == ContainerStatus.Warning);
        vm.SelectedContainer = soap;
        await TestTimeout.Run(() => vm.PreviewTask!);
        Assert.Contains(vm.IssueLines, l => l.StartsWith("Warning, line ", StringComparison.Ordinal));
        Assert.Equal(soap.Info.ContentFirstLine, vm.PreviewLines[0].LineNumber);
    }

    [Fact]
    public async Task ASecondScan_ReplacesTheFirst_AndAChangedSettingClearsTheResult()
    {
        var vm = Studio(IntakeTestData.PathOf("i01_sample"));
        await Scan(vm);
        await Scan(vm);
        Assert.Equal(27, vm.Containers.Count);

        vm.SelectedContainer = vm.Containers[3];
        vm.Mode = FileIntakeNodeSettings.MarkersMode;
        Assert.Empty(vm.Containers);
        Assert.Null(vm.SelectedContainer);
        Assert.Null(vm.TotalsText);
        Assert.Empty(vm.Details);

        await Scan(vm);
        Assert.Equal(19, vm.Containers.Count);
    }

    [Fact]
    public async Task AMissingFile_GivesThePlainMessage_AndNoRows()
    {
        var vm = Studio(Path.Combine(_folder, "nothing-here.txt"));

        await Scan(vm);

        Assert.Empty(vm.Containers);
        Assert.NotNull(vm.Message);
        Assert.DoesNotContain(_folder, vm.Message);
        Assert.DoesNotContain("Exception", vm.Message);
        Assert.Null(vm.TotalsText);
    }

    [Fact]
    public async Task AFileOverTheLimit_NamesTheLimitInMb()
    {
        var path = LargeFile(200);
        var vm = new IntakeStudioViewModel(new FileIntakeNodeSettings { Path = path, MaxFileSizeMb = 1 });

        await Scan(vm);

        Assert.Empty(vm.Containers);
        Assert.Contains("limit of 1 MB", vm.Message);
    }

    [Fact]
    public async Task AWindows1252File_InAuto_ShowsTheGuessNotice_AndUtf8ShowsTheBadCharacter()
    {
        var path = Path.Combine(_folder, "w1252.csv");
        File.WriteAllBytes(path, [0x4E, 0x61, 0x6D, 0x65, 0x2C, 0x43, 0x6F, 0x6E, 0x63, 0x20, 0x28, 0xB5, 0x67, 0x2F, 0x6D, 0x4C, 0x29, 0x0D, 0x0A, 0x41, 0x2C, 0x31, 0x2E, 0x35, 0x0D, 0x0A]);
        var vm = Studio(path);

        await Scan(vm);
        Assert.NotNull(vm.EncodingNotice);
        Assert.Contains("Windows-1252", vm.EncodingNotice);
        Assert.Contains("choose another encoding", vm.EncodingNotice);
        vm.SelectedContainer = vm.Containers[0];
        await TestTimeout.Run(() => vm.PreviewTask!);
        Assert.Contains(vm.PreviewLines, l => l.Text.Contains("Conc (µg/mL)", StringComparison.Ordinal));

        vm.Encoding = IntakeEncodings.Utf8;
        Assert.Null(vm.EncodingNotice);
        Assert.Empty(vm.Containers);
        await Scan(vm);
        Assert.Null(vm.EncodingNotice);
        vm.SelectedContainer = vm.Containers[0];
        Assert.Contains(vm.IssueLines, l => l.Contains("1 character could not be read correctly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidUtf8_WithAReplacementCharacter_HasNoNotice()
    {
        var path = Path.Combine(_folder, "u.csv");
        File.WriteAllText(path, "Name,Value\nA\uFFFD,1\nB,2\n", new UTF8Encoding(false));
        var vm = Studio(path);

        await Scan(vm);

        Assert.Null(vm.EncodingNotice);
        Assert.NotEmpty(vm.Containers);
    }

    [Fact]
    public async Task Cancel_MidScan_ShowsScanCancelled_NoRows_AndReleasesTheFile_20Times()
    {
        for (var round = 0; round < 20; round++)
        {
            var path = LargeFile(2000);
            var vm = Studio(path);
            vm.ScanCommand.Execute(null);
            Assert.True(vm.IsScanning);
            Assert.False(vm.ScanCommand.CanExecute(null));
            await Task.Delay(round % 4 * 10);

            vm.CancelCommand.Execute(null);
            await TestTimeout.Run(() => vm.ScanTask!);

            Assert.Equal(IntakeStudioViewModel.ScanCancelledText, vm.Message);
            Assert.Empty(vm.Containers);
            Assert.Null(vm.TotalsText);
            Assert.Null(vm.Summary);
            Assert.False(vm.IsScanning);
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public async Task ChangingASettingDuringAScan_StopsIt_AndShowsNothingOfIt()
    {
        var vm = Studio(LargeFile(2000));
        vm.ScanCommand.Execute(null);

        vm.Encoding = IntakeEncodings.Utf8;
        await TestTimeout.Run(() => vm.ScanTask!);

        Assert.Empty(vm.Containers);
        Assert.Null(vm.Message);
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public void Save_WritesOnlyAChange_AndClosingAsksOnce()
    {
        var saved = new List<FileIntakeNodeSettings>();
        var asked = 0;
        var answer = UnsavedChangesChoice.Cancel;
        var vm = new IntakeStudioViewModel(new FileIntakeNodeSettings { Path = "a.txt" }, save: saved.Add, askUnsaved: () => { asked++; return answer; });
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        Assert.False(vm.SaveCommand.CanExecute(null));
        vm.Save();
        Assert.Empty(saved);

        vm.Mode = FileIntakeNodeSettings.MarkersMode;
        Assert.True(vm.HasUnsavedChanges);

        vm.CloseCommand.Execute(null);
        Assert.Equal(1, asked);
        Assert.Equal(0, closed);

        answer = UnsavedChangesChoice.Save;
        vm.EscapeCommand.Execute(null);
        Assert.Equal(2, asked);
        Assert.Equal(1, closed);
        Assert.Equal(FileIntakeNodeSettings.MarkersMode, Assert.Single(saved).Mode);
        Assert.False(vm.HasUnsavedChanges);
        Assert.True(vm.ConfirmClose());
        Assert.Equal(2, asked);
    }

    [Fact]
    public void Browse_SetsThePath_AndTheFileNameHidesTheFolder()
    {
        var vm = new IntakeStudioViewModel(FileIntakeNodeSettings.Default, pickFile: _ => @"C:\lab\exports\run 7.txt");
        Assert.Equal(NodeViewModel.NoFileChosenText, vm.FileName);
        Assert.False(vm.ScanCommand.CanExecute(null));

        vm.BrowseCommand.Execute(null);

        Assert.Equal("run 7.txt", vm.FileName);
        Assert.Equal(@"C:\lab\exports\run 7.txt", vm.FileToolTip);
        Assert.True(vm.ScanCommand.CanExecute(null));
    }
}
