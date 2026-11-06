using System.Diagnostics;
using EtlDesigner.Engine.Csv;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class CsvPreviewReaderTests
{
    [Fact]
    public async Task ReadAsync_C01Basic_Preview5_Returns4RowsAnd32Columns()
    {
        var reader = new CsvPreviewReader();
        var settings = CreateSettings(CompositePath("c01_basic.csv"), ",", true, 0, "utf-8", 5);

        var preview = await TestTimeout.Run(() => reader.ReadAsync(settings, CancellationToken.None));

        Assert.Null(preview.Error);
        Assert.Equal(32, preview.Columns.Count);
        Assert.Equal("id", preview.Columns[0]);
        Assert.Equal("batch", preview.Columns[1]);
        Assert.Equal("name", preview.Columns[2]);
        Assert.Equal("sample", preview.Columns[3]);
        Assert.Equal("r01", preview.Columns[4]);
        Assert.Equal(4, preview.Rows.Count);
        Assert.False(preview.HasMoreRows);
    }

    [Fact]
    public async Task ReadAsync_C01Basic_Preview2_Returns2RowsAndHasMoreRows()
    {
        var reader = new CsvPreviewReader();
        var settings = CreateSettings(CompositePath("c01_basic.csv"), ",", true, 0, "utf-8", 2);

        var preview = await TestTimeout.Run(() => reader.ReadAsync(settings, CancellationToken.None));

        Assert.Null(preview.Error);
        Assert.Equal(2, preview.Rows.Count);
        Assert.True(preview.HasMoreRows);
    }

    [Fact]
    public async Task ReadAsync_C06Semicolon_ReturnsSemicolonParsedRows()
    {
        var reader = new CsvPreviewReader();
        var settings = CreateSettings(CompositePath("c06_semicolon.csv"), ";", true, 0, "utf-8", 5);

        var preview = await TestTimeout.Run(() => reader.ReadAsync(settings, CancellationToken.None));

        Assert.Null(preview.Error);
        Assert.Equal(3, preview.Rows.Count);
        Assert.Equal("Person 1", preview.Rows[0][2]);
        Assert.Equal("1,11", preview.Rows[0][4]);
    }

    [Fact]
    public async Task ReadAsync_C05Quoting_QuotedCommaValuesStayInSingleCells()
    {
        var reader = new CsvPreviewReader();
        var settings = CreateSettings(CompositePath("c05_quoting.csv"), ",", true, 0, "utf-8", 10);

        var preview = await TestTimeout.Run(() => reader.ReadAsync(settings, CancellationToken.None));

        Assert.Null(preview.Error);
        Assert.Equal(4, preview.Rows.Count);

        var flattened = preview.Rows.SelectMany(r => r).ToList();
        Assert.Contains("Rao, Asha", flattened);
        Assert.Contains("2,5", flattened);
    }

    [Fact]
    public async Task ReadAsync_C07NoHeader_ReturnsCol1ToCol28()
    {
        var reader = new CsvPreviewReader();
        var settings = CreateSettings(CompositePath("c07_no_header.csv"), ",", false, 0, "utf-8", 10);

        var preview = await TestTimeout.Run(() => reader.ReadAsync(settings, CancellationToken.None));

        Assert.Null(preview.Error);
        Assert.Equal(28, preview.Columns.Count);
        Assert.Equal("Col1", preview.Columns[0]);
        Assert.Equal("Col28", preview.Columns[27]);
    }

    [Fact]
    public async Task ReadAsync_C14Preamble_WithSkipLinesAndHeader_ReturnsExpectedColumnsAndFirstRow()
    {
        var reader = new CsvPreviewReader();
        var settings = CreateSettings(CompositePath("c14_preamble.csv"), ",", true, 3, "utf-8", 5);

        var preview = await TestTimeout.Run(() => reader.ReadAsync(settings, CancellationToken.None));

        Assert.Null(preview.Error);
        Assert.Equal(32, preview.Columns.Count);
        Assert.Equal("id", preview.Columns[0]);
        Assert.Equal("extra_10", preview.Columns[31]);
        Assert.NotEmpty(preview.Rows);
        Assert.Equal("1", preview.Rows[0][0]);
    }

    [Fact]
    public async Task ReadAsync_Windows1252File_ReadsMexicoValue()
    {
        var reader = new CsvPreviewReader();
        var settings = CreateSettings(TestDataPath("06_encoding_windows1252.csv"), ",", true, 0, "windows-1252", 20);

        var preview = await TestTimeout.Run(() => reader.ReadAsync(settings, CancellationToken.None));

        Assert.Null(preview.Error);
        var flattened = preview.Rows.SelectMany(r => r).ToList();
        Assert.Contains("México|Puebla", flattened);
    }

    [Fact]
    public async Task ReadAsync_C10Large_IsFast_HasMoreRows_AndReleasesFileLock()
    {
        var reader = new CsvPreviewReader();
        var settings = CreateSettings(CompositePath("c10_large.csv"), ",", true, 0, "utf-8", 5);

        var preview = await TestTimeout.Run(() => reader.ReadAsync(settings, CancellationToken.None));

        Assert.Null(preview.Error);
        Assert.Equal(5, preview.Rows.Count);
        Assert.True(preview.HasMoreRows);
        Assert.True(preview.ElapsedMs < 2000, $"Expected elapsed < 2000ms but was {preview.ElapsedMs}ms.");

        using var stream = new FileStream(settings.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public async Task ReadAsync_MissingFile_SetsError()
    {
        var reader = new CsvPreviewReader();
        var settings = CreateSettings(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.csv"), ",", true, 0, "utf-8", 5);

        var preview = await TestTimeout.Run(() => reader.ReadAsync(settings, CancellationToken.None));

        Assert.NotNull(preview.Error);
        Assert.False(preview.IsFormatError);
    }

    [Fact]
    public async Task ReadAsync_MoreThanTheLimitAsked_ReadsAtMost1000Rows()
    {
        var reader = new CsvPreviewReader();
        var settings = CreateSettings(CompositePath("c10_large.csv"), ",", true, 0, "utf-8", 6000);

        var preview = await TestTimeout.Run(() => reader.ReadAsync(settings, CancellationToken.None));

        Assert.Null(preview.Error);
        Assert.Equal(CsvPreviewReader.MaxPreviewRows, preview.Rows.Count);
        Assert.True(preview.HasMoreRows);
    }

    private static CsvReadOutcome Outcome(string? settingsError = null, Exception? failure = null, bool licence = false, string? structuralError = null)
        => new([], 0, structuralError is null ? [] : [new CsvStructuralError(2, structuralError)], settingsError, failure, licence, false, 1);

    [Fact]
    public void ErrorPreview_OnlyAFormatFaultIsAFormatError()
    {
        Assert.Null(CsvPreviewReader.ErrorPreview(Outcome()));

        Assert.False(CsvPreviewReader.ErrorPreview(Outcome(licence: true))!.IsFormatError);
        Assert.False(CsvPreviewReader.ErrorPreview(Outcome(settingsError: "File was not found. Check the path and try again."))!.IsFormatError);
        Assert.False(CsvPreviewReader.ErrorPreview(Outcome(failure: new FileNotFoundException("gone")))!.IsFormatError);
        Assert.False(CsvPreviewReader.ErrorPreview(Outcome(failure: new UnauthorizedAccessException("denied")))!.IsFormatError);

        Assert.True(CsvPreviewReader.ErrorPreview(Outcome(failure: new InvalidOperationException("bad record")))!.IsFormatError);
        Assert.True(CsvPreviewReader.ErrorPreview(Outcome(structuralError: "Row: 3 - unclosed quote"))!.IsFormatError);
    }

    [Fact]
    public async Task ReadAsync_EmptyFile_SetsError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, string.Empty);

        try
        {
            var reader = new CsvPreviewReader();
            var settings = CreateSettings(path, ",", true, 0, "utf-8", 5);

            var preview = await TestTimeout.Run(() => reader.ReadAsync(settings, CancellationToken.None));

            Assert.NotNull(preview.Error);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task ReadAsync_UnclosedQuote_SetsErrorWithRecordHint()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bad-quote-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path,
        [
            "id,name,email",
            "1,Good,good@example.com",
            "2,Unclosed,\"Quote,broken@example.com"
        ]);

        try
        {
            var reader = new CsvPreviewReader();
            var settings = CreateSettings(path, ",", true, 0, "utf-8", 50);

            var preview = await TestTimeout.Run(() => reader.ReadAsync(settings, CancellationToken.None));

            Assert.NotNull(preview.Error);
            Assert.Contains("row", preview.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task ReadAsync_CallerCancellationToken_ReturnsWithoutHanging()
    {
        var reader = new CsvPreviewReader();
        var settings = CreateSettings(CompositePath("c10_large.csv"), ",", true, 0, "utf-8", 5);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = Stopwatch.StartNew();
        var preview = await TestTimeout.Run(() => reader.ReadAsync(settings, cts.Token));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000, $"Expected cancellation return under 2s but took {sw.ElapsedMilliseconds}ms.");
        Assert.True(preview.Cancelled);
        Assert.Null(preview.Error);
    }

    private static CsvFileSettings CreateSettings(string path, string separator, bool hasHeader, int skipLines, string encodingName, int previewRows)
        => new(path, separator, hasHeader, skipLines, encodingName, previewRows);

    private static string CompositePath(string fileName)
        => Path.Combine(TestDataPath("composite"), fileName);

    private static string TestDataPath(string relative)
        => Path.Combine(RepoRoot(), "Nodify_ETLBox", "SampleData", "TestData", relative);

    private static string RepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (current.GetFiles("*.slnx").Length > 0 || current.GetFiles("*.sln").Length > 0)
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root was not found from test base directory.");
    }
}
