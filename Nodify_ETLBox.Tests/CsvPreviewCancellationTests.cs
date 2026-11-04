using System.Diagnostics;
using EtlDesigner.Engine.Csv;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>
/// A caller's cancel must end ReadAsync promptly, as a cancel and not as an error.
/// Before the fix a cancel that landed while ETLBox started the network hung for ever.
/// </summary>
[Collection(LargeFileCollection.Name)]
public class CsvPreviewCancellationTests
{
    private const int StressRepeats = 50;

    private readonly ITestOutputHelper _output;

    public CsvPreviewCancellationTests(ITestOutputHelper output) => _output = output;

    public enum CancelCase
    {
        BeforeStart,
        OneMsAfterStart,
        WhileReading
    }

    [Fact]
    public async Task ReadAsync_TokenCancelledBeforeStart_ReturnsCancelled()
    {
        var (preview, ms) = await RunCaseAsync(CancelCase.BeforeStart);

        _output.WriteLine($"cancelled before start: {ms} ms, rows={preview.Rows.Count}, cancelled={preview.Cancelled}");
        Assert.True(preview.Cancelled);
        Assert.Null(preview.Error);
        Assert.Empty(preview.Rows);
    }

    [Fact]
    public async Task ReadAsync_TokenCancelledOneMsAfterStart_ReturnsPromptlyWithoutError()
    {
        var (preview, ms) = await RunCaseAsync(CancelCase.OneMsAfterStart);

        // The Windows timer may fire later than 1 ms; if the read wins the race it returns its
        // rows, which is fine. It must never hang and never be an error.
        _output.WriteLine($"cancelled 1 ms after start: {ms} ms, rows={preview.Rows.Count}, cancelled={preview.Cancelled}");
        AssertCancelledOrComplete(preview, CancelCase.OneMsAfterStart);
    }

    [Fact]
    public async Task ReadAsync_TokenCancelledWhileReadingLargeFile_ReturnsCancelledAndReleasesFile()
    {
        var (preview, ms) = await RunCaseAsync(CancelCase.WhileReading);

        _output.WriteLine($"cancelled while reading: {ms} ms, rows={preview.Rows.Count}, cancelled={preview.Cancelled}");
        Assert.True(preview.Cancelled);
        Assert.Null(preview.Error);
        Assert.Empty(preview.Rows);

        using var exclusive = new FileStream(LargeFile, FileMode.Open, FileAccess.Read, FileShare.None);
    }

    [Theory]
    [InlineData(CancelCase.BeforeStart)]
    [InlineData(CancelCase.OneMsAfterStart)]
    [InlineData(CancelCase.WhileReading)]
    public async Task ReadAsync_CancelCase_Repeated50Times_NeverHangs(CancelCase cancelCase)
    {
        var slowest = 0L;
        var cancelled = 0;
        for (var i = 0; i < StressRepeats; i++)
        {
            var (preview, ms) = await RunCaseAsync(cancelCase);

            AssertCancelledOrComplete(preview, cancelCase);
            cancelled += preview.Cancelled ? 1 : 0;
            slowest = Math.Max(slowest, ms);
        }

        _output.WriteLine($"{cancelCase}: {StressRepeats} repeats, {cancelled} reported cancelled, slowest {slowest} ms");
    }

    [Fact]
    public async Task ReadAsync_RowLimitStop_Repeated50Times_ReturnsRows()
    {
        // The internal stop after N rows uses the same path; check it at its earliest point.
        var reader = new CsvPreviewReader();
        var settings = new CsvFileSettings(LargeFile, ",", true, 0, "utf-8", 1);

        for (var i = 0; i < StressRepeats; i++)
        {
            var preview = await TestTimeout.Run(() => reader.ReadAsync(settings, CancellationToken.None));

            Assert.False(preview.Cancelled);
            Assert.Null(preview.Error);
            Assert.Single(preview.Rows);
            Assert.True(preview.HasMoreRows);
        }
    }

    private static void AssertCancelledOrComplete(CsvPreview preview, CancelCase cancelCase)
    {
        Assert.Null(preview.Error);

        if (preview.Cancelled)
        {
            Assert.Empty(preview.Rows);
            return;
        }

        // Only the timer-based case may lose the race to a finished read.
        Assert.Equal(CancelCase.OneMsAfterStart, cancelCase);
        Assert.Equal(1000, preview.Rows.Count);
    }

    private static async Task<(CsvPreview Preview, long Ms)> RunCaseAsync(CancelCase cancelCase)
    {
        using var cts = new CancellationTokenSource();
        var reader = new CsvPreviewReader();
        var settings = new CsvFileSettings(LargeFile, ",", true, 0, "utf-8", 1000);

        switch (cancelCase)
        {
            case CancelCase.BeforeStart:
                cts.Cancel();
                break;
            case CancelCase.OneMsAfterStart:
                cts.CancelAfter(1);
                break;
            case CancelCase.WhileReading:
                // Cancel from inside the read after 100 rows, so the timing is certain.
                reader.RowCollected = count =>
                {
                    if (count == 100)
                    {
                        cts.Cancel();
                    }
                };
                break;
        }

        var sw = Stopwatch.StartNew();
        var preview = await TestTimeout.Run(() => reader.ReadAsync(settings, cts.Token));
        sw.Stop();

        return (preview, sw.ElapsedMilliseconds);
    }

    private static string LargeFile
        => Path.Combine(AppContext.BaseDirectory, "SampleData", "TestData", "composite", "c10_large.csv");
}
