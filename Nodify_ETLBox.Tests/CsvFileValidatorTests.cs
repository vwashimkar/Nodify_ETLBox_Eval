using System.Text;
using EtlDesigner.Engine.Csv;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class CsvFileValidatorTests
{
    [Fact]
    public void Validate_ReturnsMessage_WhenPathEmpty()
    {
        var settings = new CsvFileSettings("", ",", true, 0, "utf-8", 5);

        var messages = CsvFileValidator.Validate(settings);

        Assert.Contains(messages, m => m.Contains("File path is empty", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReturnsMessage_WhenFileMissing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.csv");
        var settings = new CsvFileSettings(missingPath, ",", true, 0, "utf-8", 5);

        var messages = CsvFileValidator.Validate(settings);

        Assert.Contains(messages, m => m.Contains("File was not found", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReturnsMessage_WhenFileEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid():N}.csv");
        File.WriteAllBytes(path, []);

        try
        {
            var settings = new CsvFileSettings(path, ",", true, 0, "utf-8", 5);

            var messages = CsvFileValidator.Validate(settings);

            Assert.Contains(messages, m => m.Contains("File is empty", StringComparison.Ordinal));
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
    public void Validate_ReturnsMessage_WhenSeparatorInvalid()
    {
        var file = CompositePath("c01_basic.csv");
        var settings = new CsvFileSettings(file, "::", true, 0, "utf-8", 5);

        var messages = CsvFileValidator.Validate(settings);

        Assert.Contains(messages, m => m.Contains("Separator is longer than one character", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AllowsTabSeparatorName()
    {
        var file = CompositePath("c01_basic.csv");
        var settings = new CsvFileSettings(file, "tab", true, 0, "utf-8", 5);

        var messages = CsvFileValidator.Validate(settings);

        Assert.DoesNotContain(messages, m => m.Contains("Separator is longer than one character", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReturnsMessage_WhenSkipLinesNegative()
    {
        var file = CompositePath("c01_basic.csv");
        var settings = new CsvFileSettings(file, ",", true, -1, "utf-8", 5);

        var messages = CsvFileValidator.Validate(settings);

        Assert.Contains(messages, m => m.Contains("Skip lines is below 0", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void Validate_ReturnsMessage_WhenPreviewRowsOutOfRange(int previewRows)
    {
        var file = CompositePath("c01_basic.csv");
        var settings = new CsvFileSettings(file, ",", true, 0, "utf-8", previewRows);

        var messages = CsvFileValidator.Validate(settings);

        Assert.Contains(messages, m => m.Contains("Preview rows is out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReturnsMessage_WhenEncodingUnknown()
    {
        var file = CompositePath("c01_basic.csv");
        var settings = new CsvFileSettings(file, ",", true, 0, "unknown-encoding-name", 5);

        var messages = CsvFileValidator.Validate(settings);

        Assert.Contains(messages, m => m.Contains("Encoding name is unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReturnsNoMessages_ForValidSettings()
    {
        var file = CompositePath("c01_basic.csv");
        var settings = new CsvFileSettings(file, ",", true, 0, "utf-8", 5);

        var messages = CsvFileValidator.Validate(settings);

        Assert.Empty(messages);
    }

    private static string CompositePath(string fileName)
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "SampleData", "TestData", "composite", fileName));
    }
}