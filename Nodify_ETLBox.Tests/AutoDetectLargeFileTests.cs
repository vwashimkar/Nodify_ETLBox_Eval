using System.Diagnostics;
using System.Globalization;
using System.IO;
using EtlDesigner.Engine.Intake;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>The auto-detector on the i09 text repeated N times, in a temp folder.</summary>
[Collection("Intake memory")]
public class AutoDetectLargeFileTests : IDisposable
{
    private static readonly TimeSpan LargeLimit = TimeSpan.FromSeconds(60);

    private readonly ITestOutputHelper _output;
    private readonly string _folder = Directory.CreateTempSubdirectory("g2_large_").FullName;

    public AutoDetectLargeFileTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private string WriteRepeated(int copies)
    {
        var sample = File.ReadAllBytes(IntakeTestData.PathOf("i09_sample_nomarkers"));
        var path = Path.Combine(_folder, $"i09_x{copies}.txt");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        for (var i = 0; i < copies; i++)
        {
            output.Write(sample);
        }

        return path;
    }

    private static IntakeItem WithStream(IntakeItem item, Func<Stream, Stream> wrap)
    {
        var open = item.OpenRead;
        return item with { OpenRead = () => wrap(open()) };
    }

    private void Log(FormattableString message)
    {
        var line = message.ToString(CultureInfo.InvariantCulture);
        _output.WriteLine(line);
        File.AppendAllText(Path.Combine(Path.GetDirectoryName(AutoDetectTruthTests.ReportPath())!, "g2-large-file.txt"), $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
    }

    [Fact]
    public async Task N2000_GivesTheSameContainers_WhateverTheReadSize()
    {
        const int copies = 2000;
        var path = WriteRepeated(copies);
        var item = new FileIntakeFactory().CreateItem(path);

        var clock = Stopwatch.StartNew();
        var whole = await TestTimeout.Run(() => IntakeReader.ReadAsync(item, new IntakeSettings(), null, CancellationToken.None), LargeLimit);
        var ms = clock.ElapsedMilliseconds;
        var small = await TestTimeout.Run(() => IntakeReader.ReadAsync(WithStream(item, s => new SmallReadStream(s, 4093)), new IntakeSettings(), null, CancellationToken.None), LargeLimit);

        Assert.Null(whole.Error);
        Assert.Equal(whole.Containers.Select(c => c with { Reasons = null }), small.Containers.Select(c => c with { Reasons = null }));
        Assert.Equal(185L * copies, whole.File!.LineCount);
        Assert.Equal(Enumerable.Range(1, whole.Containers.Count), whole.Containers.Select(c => c.Order));
        Log($"N={copies}: {new FileInfo(path).Length:N0} bytes, {whole.Containers.Count:N0} containers, {ms:N0} ms; read in 4,093-byte pieces: {small.Containers.Count:N0} containers");
    }

    [Fact]
    public void Memory_DoesNotGrowWithN_ApartFromTheContainerList()
    {
        var small = Measure(200);
        var large = Measure(2000);

        Assert.True(large.MaxExcess < 4 * 1024 * 1024, $"the scan keeps {large.MaxExcess:N0} bytes beyond the container list");
        Assert.True(large.MaxExcess - small.MaxExcess < 1024 * 1024, $"excess grew from {small.MaxExcess:N0} to {large.MaxExcess:N0} bytes");
    }

    /// <summary>The G1 method: full collections at 20 points of the file, minus the container list's share so far.</summary>
    private (int Containers, long Retained, long Peak, long MaxExcess, long Ms) Measure(int copies)
    {
        var path = WriteRepeated(copies);
        var size = new FileInfo(path).Length;
        var samples = new List<(long Bytes, long Live)>();
        var nextSample = size / 20;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var item = WithStream(new FileIntakeFactory().CreateItem(path), inner => new IntakeLargeFileTests.ObservingStream(inner, position =>
        {
            if (position >= nextSample)
            {
                nextSample += size / 20;
                samples.Add((position, GC.GetTotalMemory(forceFullCollection: true) - baseline));
            }
        }));

        var clock = Stopwatch.StartNew();
        var result = IntakeReader.Read(item, new IntakeSettings(), null, CancellationToken.None);
        clock.Stop();
        var retained = GC.GetTotalMemory(forceFullCollection: true) - baseline;
        var excess = samples.Max(s => s.Live - (long)(retained * ((double)s.Bytes / size)));

        Assert.Null(result.Error);
        var numbers = (result.Containers.Count, retained, samples.Max(s => s.Live), excess, clock.ElapsedMilliseconds);
        Log($"memory N={copies}: containers {numbers.Count:N0}, retained {retained:N0} bytes, peak live {numbers.Item3:N0} bytes, max excess beyond the container list {excess:N0} bytes, {clock.ElapsedMilliseconds:N0} ms");
        GC.KeepAlive(result);
        return numbers;
    }

    [Fact]
    public async Task Cancel_20Times_ReleasesTheFile()
    {
        for (var i = 0; i < 20; i++)
        {
            var path = WriteRepeated(200);
            using var cts = new CancellationTokenSource();
            var lines = 0;
            var item = WithStream(new FileIntakeFactory().CreateItem(path), inner => new IntakeLargeFileTests.ObservingStream(inner, _ =>
            {
                if (++lines == 2 + (i % 10))
                {
                    cts.Cancel();
                }
            }));

            var result = await TestTimeout.Run(() => IntakeReader.ReadAsync(item, new IntakeSettings(), null, cts.Token), LargeLimit);

            Assert.True(result.Cancelled, $"run {i + 1} was not cancelled");
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public async Task Progress_HasReadingDetectingAndDone_AndTheLastReportEqualsTheTotals()
    {
        var path = WriteRepeated(2000);
        var reports = new List<IntakeProgress>();

        var result = await TestTimeout.Run(() => IntakeReader.ReadAsync(new FileIntakeFactory().CreateItem(path), new IntakeSettings(), p => { lock (reports) reports.Add(p); }, CancellationToken.None), LargeLimit);

        Assert.Contains(reports, r => r.Phase == IntakePhase.Detecting);
        var last = reports[^1];
        Assert.Equal((IntakePhase.Done, result.File!.Size, result.Containers.Count), (last.Phase, last.BytesRead, last.ContainersFound));
    }

    [Fact]
    /// <summary>In this collection because the memory numbers cover the whole process.</summary>
    public void ManyIdenticalCsvLines_AreOneContainer_AndMemoryStaysFlat()
    {
        var path = Path.Combine(Path.GetTempPath(), $"g2_identical_{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, "a,b,c\n" + string.Concat(Enumerable.Repeat("1,2,3\n", 100_000)));
            GC.Collect();
            var before = GC.GetTotalMemory(forceFullCollection: true);

            var result = IntakeReader.Read(new FileIntakeFactory().CreateItem(path), new IntakeSettings(), null, CancellationToken.None);

            var retained = GC.GetTotalMemory(forceFullCollection: true) - before;
            var container = Assert.Single(result.Containers);
            Assert.Equal(("csv", 1L, 100_001L, ContainerConfidence.Sure), (container.Format, container.ContentFirstLine!.Value, container.ContentLastLine!.Value, container.Confidence));
            Assert.True(retained < 1024 * 1024, $"the result keeps {retained:N0} bytes");
            GC.KeepAlive(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Returns at most a few bytes per read, so every block boundary falls somewhere else.</summary>
    private sealed class SmallReadStream(Stream inner, int max) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, max));

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

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
