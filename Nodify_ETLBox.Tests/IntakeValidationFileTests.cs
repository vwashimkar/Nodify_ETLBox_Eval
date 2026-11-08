using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using EtlDesigner.Engine.Intake;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>
/// The validator over every test file, written to reports/g3-validation-report.md. Warnings and
/// errors in the files never fail it; only an exception, a run error or a container without a status does.
/// </summary>
public class IntakeValidationFileTests
{
    public static IEnumerable<(string Path, IntakeMode Mode)> AllRuns()
    {
        foreach (var file in Directory.GetFiles(IntakeTestData.Folder, "i*.txt").Order())
        {
            yield return (file, IntakeMode.AutoDetect);
        }

        foreach (var name in IntakeTestData.MarkerFiles)
        {
            yield return (IntakeTestData.PathOf(name), IntakeMode.Markers);
        }

        foreach (var file in AutoDetectTruthTests.DevelopmentRealFiles)
        {
            yield return (Path.Combine(RealIntakeFileTests.Folder, file), IntakeMode.AutoDetect);
        }
    }

    [Fact]
    public void EveryTestFile_IsValidated_WithAStatusPerContainer_AndTheReportIsWritten()
    {
        var text = new StringBuilder();
        text.AppendLine("# G4a validation report");
        text.AppendLine();
        text.AppendLine("Written by `IntakeValidationFileTests`. Per file and mode: each container's format (display name), lines, status, whether its format has checks, and issues (code, line (rows or lines when grouped): message). Issues beyond the cap are counted.");
        text.AppendLine();

        foreach (var (path, mode) in AllRuns())
        {
            var settings = new IntakeSettings { Mode = mode };
            var item = new FileIntakeFactory().CreateItem(path);
            var result = IntakeReader.Read(item, settings, null, CancellationToken.None);
            var validation = IntakeValidator.Validate(item, result, null, CancellationToken.None, settings);

            Assert.Null(result.Error);
            Assert.Null(validation.Error);
            Assert.False(validation.Cancelled);
            Assert.Equal(result.Containers.Select(c => c.Order), validation.Containers.Select(v => v.Order));
            Assert.All(validation.Containers, v => Assert.True(Enum.IsDefined(v.Status)));
            Assert.All(validation.Containers, IntakeValidatorTests.AssertPlain);
            if (mode == IntakeMode.AutoDetect)
            {
                AutoDetectPropertyTests.AssertEveryNonBlankLineInExactlyOneContainer(path);
            }

            text.AppendLine($"## {Path.GetFileName(path)} ({mode})");
            text.AppendLine();
            text.AppendLine($"Encoding {result.EncodingUsed}{(result.EncodingGuessed ? " (guessed)" : "")}, invalid UTF-8 seen: {(result.InvalidUtf8Seen ? "yes" : "no")}. Errors {validation.Errors}, warnings {validation.Warnings}, infos {validation.Infos}, {validation.ElapsedMs} ms.");
            text.AppendLine();
            text.AppendLine("| # | Format | Lines | Status | Checked | Issues |");
            text.AppendLine("|---|---|---|---|---|---|");
            foreach (var (container, check) in result.Containers.Zip(validation.Containers))
            {
                var issues = string.Join("<br>", check.Issues.Select(i => $"{i.Severity} `{i.Code}` {i.Line}{(i.LineCount > 1 ? $" ({i.LineCount})" : "")}: {i.Message.Replace("|", "\\|")}"));
                if (check.MoreNotShown)
                {
                    issues += $"<br>({check.TotalIssues - check.Issues.Count} more not shown)";
                }

                text.AppendLine($"| {container.Order} | {ContainerDisplay.Format(container)} | {container.ContentFirstLine}-{container.ContentLastLine} | {check.Status} | {(check.Checked ? "yes" : "no")} | {issues} |");
            }

            text.AppendLine();
        }

        var report = Path.Combine(Path.GetDirectoryName(AutoDetectTruthTests.ReportPath())!, "g4a-validation-report.md");
        File.WriteAllText(report, text.ToString());
    }
}

/// <summary>Validation of the large stress file: cancel, progress and flat memory. Not parallel, because the memory numbers cover the whole process.</summary>
[Collection("Intake memory")]
public class IntakeValidationLargeFileTests : IDisposable
{
    private static readonly TimeSpan LargeLimit = TimeSpan.FromSeconds(60);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "intake-g3-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private readonly StressFiles _stress;

    public IntakeValidationLargeFileTests(ITestOutputHelper output, StressFiles stress)
    {
        _output = output;
        _stress = stress;
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private static IntakeItem Observed(string path, Action<long> onRead)
    {
        var item = new FileIntakeFactory().CreateItem(path);
        var open = item.OpenRead;
        return item with { OpenRead = () => new IntakeLargeFileTests.ObservingStream(open(), onRead) };
    }

    private void Log(FormattableString message)
    {
        var line = message.ToString(CultureInfo.InvariantCulture);
        _output.WriteLine(line);
        File.AppendAllText(Path.Combine(Path.GetDirectoryName(AutoDetectTruthTests.ReportPath())!, "g3-large-file.txt"), $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
    }

    /// <summary>Reports at once, on the calling thread, so the order of the reports is kept.</summary>
    private sealed class Recorder(Action<IntakeProgress> report) : IProgress<IntakeProgress>
    {
        public void Report(IntakeProgress value) => report(value);
    }

    [Fact]
    public async Task Cancel_20Times_DuringValidation_ReleasesTheFile()
    {
        var original = _stress.N2000;
        var result = _stress.N2000Result.Result;
        Assert.Null(result.Error);

        for (var i = 0; i < 20; i++)
        {
            var path = Path.Combine(_folder, $"copy{i}.txt");
            File.Copy(original, path);
            using var cts = new CancellationTokenSource();
            var reads = 0;
            var item = Observed(path, _ =>
            {
                if (++reads == 20 + 37 * i)
                {
                    cts.Cancel();
                }
            });

            var validation = await TestTimeout.Run(() => IntakeValidator.ValidateAsync(item, result, null, cts.Token), LargeLimit);

            Assert.True(validation.Cancelled, $"run {i + 1} was not cancelled");
            Assert.True(validation.Containers.Count < result.Containers.Count);
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public async Task Progress_IsReported_AndTheLastReportEqualsTheTotals()
    {
        var item = new FileIntakeFactory().CreateItem(_stress.N2000);
        var result = _stress.N2000Result.Result;
        var reports = new List<IntakeProgress>();

        var clock = Stopwatch.StartNew();
        var validation = await TestTimeout.Run(() => IntakeValidator.ValidateAsync(item, result, new Recorder(reports.Add), CancellationToken.None), LargeLimit);
        var ms = clock.ElapsedMilliseconds;

        Assert.Null(validation.Error);
        Assert.All(reports, r => Assert.True(r.Phase is IntakePhase.Validating or IntakePhase.Done));
        var last = reports[^1];
        Assert.Equal((IntakePhase.Done, result.Containers.Count, result.Containers.Count), (last.Phase, last.ContainersValidated, last.ContainersFound));
        Assert.Equal(result.Containers.Count, validation.Containers.Count);
        Log($"validate N=2000: {result.Containers.Count:N0} containers, {ms:N0} ms, {reports.Count} progress reports, errors {validation.Errors}, warnings {validation.Warnings}, infos {validation.Infos}");
    }

    [Fact]
    public void Memory_DoesNotGrowWithN_ApartFromTheResult()
    {
        var small = Measure(200);
        var large = Measure(2000);

        Assert.True(large.MaxExcess < 8 * 1024 * 1024, $"validation keeps {large.MaxExcess:N0} bytes beyond its result");
        Assert.True(large.MaxExcess - small.MaxExcess < 2 * 1024 * 1024, $"excess grew from {small.MaxExcess:N0} to {large.MaxExcess:N0} bytes");
    }

    /// <summary>The G1 method: full collections at 20 points of the file, minus the share of the validation result so far.</summary>
    private (long Retained, long MaxExcess) Measure(int copies)
    {
        var path = _stress.Path(copies);
        var size = new FileInfo(path).Length;
        var result = copies == 2000
            ? _stress.N2000Result.Result
            : IntakeReader.Read(new FileIntakeFactory().CreateItem(path), new IntakeSettings(), null, CancellationToken.None);
        var samples = new List<(long Bytes, long Live)>();
        var nextSample = size / 20;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var baseline = LiveMemory.Bytes();
        var item = Observed(path, position =>
        {
            if (position >= nextSample)
            {
                nextSample += size / 20;
                samples.Add((position, LiveMemory.Bytes() - baseline));
            }
        });

        var clock = Stopwatch.StartNew();
        var validation = IntakeValidator.Validate(item, result, null, CancellationToken.None);
        clock.Stop();
        var retained = LiveMemory.Bytes() - baseline;
        var excess = samples.Max(s => s.Live - (long)(retained * ((double)s.Bytes / size)));

        Assert.Null(validation.Error);
        Log($"validate memory N={copies}: containers {validation.Containers.Count:N0}, result retained {retained:N0} bytes, peak live {samples.Max(s => s.Live):N0} bytes, max excess {excess:N0} bytes, {clock.ElapsedMilliseconds:N0} ms");
        GC.KeepAlive(validation);
        GC.KeepAlive(result);
        return (retained, excess);
    }
}
