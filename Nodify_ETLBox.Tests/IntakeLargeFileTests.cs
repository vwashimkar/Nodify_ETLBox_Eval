using System.Diagnostics;
using System.IO;
using EtlDesigner.Engine.Intake;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>The memory numbers cover the whole process, so nothing else may run next to these tests.</summary>
[CollectionDefinition("Intake memory", DisableParallelization = true)]
public class IntakeMemoryCollection : ICollectionFixture<StressFiles>;

/// <summary>
/// The i09 text repeated 200 and 2,000 times, written once for the whole collection, and the
/// auto-detect result of the 2,000 copy, read once. Tests that delete a file use their own copy.
/// </summary>
public sealed class StressFiles : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("p2_stress_").FullName;
    private readonly Lazy<string> _n200;
    private readonly Lazy<string> _n2000;
    private readonly Lazy<(IntakeResult Result, long Ms)> _n2000Result;

    public StressFiles()
    {
        _n200 = new(() => Write(200));
        _n2000 = new(() => Write(2000));
        _n2000Result = new(() =>
        {
            var clock = Stopwatch.StartNew();
            var result = IntakeReader.Read(new FileIntakeFactory().CreateItem(N2000), new IntakeSettings(), null, CancellationToken.None);
            return (result, clock.ElapsedMilliseconds);
        });
    }

    public string N200 => _n200.Value;

    public string N2000 => _n2000.Value;

    /// <summary>Auto-detect of <see cref="N2000"/> with the default settings, and how long it took.</summary>
    public (IntakeResult Result, long Ms) N2000Result => _n2000Result.Value;

    public string Path(int copies) => copies switch
    {
        200 => N200,
        2000 => N2000,
        _ => throw new ArgumentOutOfRangeException(nameof(copies))
    };

    private string Write(int copies)
    {
        var sample = File.ReadAllBytes(IntakeTestData.PathOf("i09_sample_nomarkers"));
        var path = System.IO.Path.Combine(_folder, $"i09_x{copies}.txt");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        for (var i = 0; i < copies; i++)
        {
            output.Write(sample);
        }

        return path;
    }

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);
}

internal static class LiveMemory
{
    /// <summary>
    /// Live bytes after two blocking full collections. <c>GC.GetTotalMemory(true)</c> repeats full
    /// collections until the number settles, up to 20 times per call, which is slow on a big heap.
    /// </summary>
    public static long Bytes()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        return GC.GetTotalMemory(forceFullCollection: false);
    }
}

/// <summary>Runs only when INTAKE_SLOW_TESTS=1.</summary>
public sealed class SlowFactAttribute : FactAttribute
{
    public SlowFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("INTAKE_SLOW_TESTS") != "1")
        {
            Skip = "slow: set INTAKE_SLOW_TESTS=1 to run it";
        }
    }
}

[Collection("Intake memory")]
public class IntakeLargeFileTests : IDisposable
{
    // The expected file of i01 has 17 marker segments; the plan and the README say 18 (user decision, 2026-11-06: 17).
    private const int MarkerSegmentsPerCopy = 17;

    private static readonly TimeSpan LargeLimit = TimeSpan.FromSeconds(60);

    private readonly ITestOutputHelper _output;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "intake_large_" + Guid.NewGuid().ToString("N"));

    public IntakeLargeFileTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>With <paramref name="invalidUtf8AtTheEnd"/> the first byte of the last copy (a text line) is 0xB5, so the UTF-8 check runs over the whole file and Auto guesses windows-1252.</summary>
    private string WriteStressFile(int copies, bool invalidUtf8AtTheEnd = false)
    {
        var sample = File.ReadAllBytes(IntakeTestData.PathOf("i01_sample"));
        var path = Path.Combine(_folder, $"stress_{copies}{(invalidUtf8AtTheEnd ? "_1252" : "")}.txt");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        for (var i = 0; i < copies; i++)
        {
            if (invalidUtf8AtTheEnd && i == copies - 1)
            {
                output.WriteByte(0xB5);
                output.Write(sample, 1, sample.Length - 1);
                continue;
            }

            output.Write(sample);
        }

        return path;
    }

    private static IntakeItem WithStream(IntakeItem item, Func<Stream, Stream> wrap)
    {
        var open = item.OpenRead;
        return item with { OpenRead = () => wrap(open()) };
    }

    private static int ExpectedContainers(int copies) => (MarkerSegmentsPerCopy * copies) + copies + 1;

    private sealed record MemoryNumbers(int Containers, long FileBytes, long RetainedBytes, long PeakBytes, long MaxExcessBytes, long Ms);

    /// <summary>
    /// Scans with a full garbage collection at 20 points of the file. RetainedBytes is what the
    /// finished result holds. The container list grows with the file, so at each point the
    /// list's share so far is taken off; MaxExcessBytes is what is left: everything else the scan
    /// keeps in memory, which is what must not grow.
    /// </summary>
    private MemoryNumbers MeasureScan(string path, int copies, bool guessed = false)
    {
        var size = new FileInfo(path).Length;
        var samples = new List<(long Bytes, long Live)>();
        var nextSample = size / 20;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var baseline = LiveMemory.Bytes();

        var item = WithStream(new FileIntakeFactory().CreateItem(path), inner => new ObservingStream(inner, position =>
        {
            if (position >= nextSample)
            {
                nextSample += size / 20;
                samples.Add((position, LiveMemory.Bytes() - baseline));
            }
        }));

        var clock = Stopwatch.StartNew();
        var result = IntakeTestData.ReadMarkers(item);
        clock.Stop();

        var retained = LiveMemory.Bytes() - baseline;
        var excess = samples.Max(s => s.Live - (long)(retained * ((double)s.Bytes / size)));

        Assert.Null(result.Error);
        Assert.Equal(ExpectedContainers(copies), result.Containers.Count);
        Assert.Equal((guessed, guessed ? "windows-1252" : "utf-8"), (result.EncodingGuessed, result.EncodingUsed));

        var numbers = new MemoryNumbers(result.Containers.Count, size, retained, samples.Max(s => s.Live), excess, clock.ElapsedMilliseconds);
        _output.WriteLine(numbers.ToString());
        GC.KeepAlive(result);
        return numbers;
    }

    [Fact]
    public async Task N2000_Gives18NPlus1Containers()
    {
        const int copies = 2000;
        var path = WriteStressFile(copies);

        var result = await TestTimeout.Run(() => IntakeReader.ReadAsync(new FileIntakeFactory().CreateItem(path), IntakeTestData.Markers, null, CancellationToken.None), LargeLimit);

        Assert.Equal(18 * copies + 1, result.Containers.Count);
        Assert.Equal(219L * copies, result.File!.LineCount);
        Assert.Equal(copies + 1, result.Containers.Count(c => c.Kind == ContainerKind.GapText));
        Assert.Equal(Enumerable.Range(1, result.Containers.Count), result.Containers.Select(c => c.Order));
    }

    [Fact]
    public void Memory_DoesNotGrowWithN_ApartFromTheContainerList()
    {
        var small = MeasureScan(WriteStressFile(200), 200);
        var large = MeasureScan(WriteStressFile(2000), 2000);

        var bytesPerContainer = (double)large.RetainedBytes / large.Containers;
        _output.WriteLine($"bytes per container in the result: {bytesPerContainer:F0}");

        Assert.True(large.MaxExcessBytes < 2 * 1024 * 1024, $"the scan keeps {large.MaxExcessBytes:N0} bytes beyond the container list");
        Assert.True(large.MaxExcessBytes - small.MaxExcessBytes < 1024 * 1024, $"excess grew from {small.MaxExcessBytes:N0} to {large.MaxExcessBytes:N0} bytes");
        Assert.True(bytesPerContainer < 1024, $"a container takes {bytesPerContainer:F0} bytes");
    }

    [Fact]
    public void Memory_StaysFlat_WithTheUtf8Check_AndAGuessAtTheEnd()
    {
        var small = MeasureScan(WriteStressFile(200, invalidUtf8AtTheEnd: true), 200, guessed: true);
        var large = MeasureScan(WriteStressFile(2000, invalidUtf8AtTheEnd: true), 2000, guessed: true);

        File.AppendAllText(Path.Combine(Path.GetDirectoryName(AutoDetectTruthTests.ReportPath())!, "g4a-large-file.txt"),
            $"{DateTime.Now:HH:mm:ss} encoding check, invalid byte in the last copy: N=200 {small}; N=2000 {large}{Environment.NewLine}");
        Assert.True(large.MaxExcessBytes < 2 * 1024 * 1024, $"the scan keeps {large.MaxExcessBytes:N0} bytes beyond the container list");
        Assert.True(large.MaxExcessBytes - small.MaxExcessBytes < 1024 * 1024, $"excess grew from {small.MaxExcessBytes:N0} to {large.MaxExcessBytes:N0} bytes");
    }

    [SlowFact]
    [Trait("Slow", "true")]
    public void N20000_TimeAndMemory()
    {
        var numbers = MeasureScan(WriteStressFile(20_000), 20_000);

        Assert.True(numbers.MaxExcessBytes < 2 * 1024 * 1024, $"the scan keeps {numbers.MaxExcessBytes:N0} bytes beyond the container list");
    }

    [Fact]
    public async Task Cancel_AfterTheFirstContainers_ReleasesTheFile_20Times()
    {
        const int copies = 2000;
        var source = WriteStressFile(copies);

        for (var run = 0; run < 20; run++)
        {
            var copy = Path.Combine(_folder, $"cancel_{run}.txt");
            File.Copy(source, copy);

            using var cancel = new CancellationTokenSource();
            var item = WithStream(new FileIntakeFactory().CreateItem(copy), inner => new ObservingStream(inner, position =>
            {
                if (position >= 1024 * 1024)
                {
                    cancel.Cancel();
                }
            }));

            var result = await TestTimeout.Run(() => IntakeReader.ReadAsync(item, IntakeTestData.Markers, null, cancel.Token));

            Assert.True(result.Cancelled);
            Assert.Null(result.Error);
            Assert.Null(result.File);
            Assert.InRange(result.Containers.Count, 1, ExpectedContainers(copies) - 1);

            File.Delete(copy);
            Assert.False(File.Exists(copy));
        }
    }

    [Fact]
    public async Task Cancel_ReportsTheTotalsItReached()
    {
        var path = WriteStressFile(200);
        using var cancel = new CancellationTokenSource();
        var item = WithStream(new FileIntakeFactory().CreateItem(path), inner => new ObservingStream(inner, position =>
        {
            if (position >= 200 * 1024)
            {
                cancel.Cancel();
            }
        }));
        var reports = new List<IntakeProgress>();

        var result = await TestTimeout.Run(() => IntakeReader.ReadAsync(item, IntakeTestData.Markers, reports.Add, cancel.Token));

        Assert.Equal(result.Containers.Count, reports[^1].ContainersFound);
        Assert.True(reports[^1].BytesRead < new FileInfo(path).Length);
    }

    [Fact]
    public async Task LastProgressReport_EqualsTheFinalTotals()
    {
        var path = WriteStressFile(2000);
        var reports = new List<IntakeProgress>();

        var result = await TestTimeout.Run(() => IntakeReader.ReadAsync(new FileIntakeFactory().CreateItem(path), IntakeTestData.Markers, reports.Add, CancellationToken.None), LargeLimit);

        Assert.NotEmpty(reports);
        Assert.Equal(new IntakeProgress(result.File!.Size, result.File.Size, result.Containers.Count, IntakePhase.Done), reports[^1]);
    }

    [Fact]
    public async Task Progress_ComesAboutEvery250Ms_WhileReading()
    {
        var path = WriteStressFile(200);
        var item = WithStream(new FileIntakeFactory().CreateItem(path), inner => new ObservingStream(inner, _ => Thread.Sleep(30)));
        var reports = new List<IntakeProgress>();

        await TestTimeout.Run(() => IntakeReader.ReadAsync(item, IntakeTestData.Markers, reports.Add, CancellationToken.None), LargeLimit);

        var reading = reports.Where(r => r.Phase == IntakePhase.Reading).ToList();
        Assert.NotEmpty(reading);
        Assert.Equal(reading.OrderBy(r => r.BytesRead).Select(r => r.BytesRead), reading.Select(r => r.BytesRead));
        Assert.All(reading, r => Assert.InRange(r.ContainersFound, 1, ExpectedContainers(200)));
    }

    /// <summary>Calls back after every read, with the position reached.</summary>
    internal sealed class ObservingStream(Stream inner, Action<long> onRead) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            _position += read;
            onRead(_position);
            return read;
        }

        public override void Flush()
        {
        }

        /// <summary>Passed on (the validator seeks to each container); the observed position follows.</summary>
        public override long Seek(long offset, SeekOrigin origin) => _position = inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
