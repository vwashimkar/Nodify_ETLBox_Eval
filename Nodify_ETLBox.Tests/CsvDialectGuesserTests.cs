using EtlDesigner.Engine.Csv;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class CsvDialectGuesserTests
{
    [Fact]
    public void Guess_SuggestsCommaAndHeader_ForC01Basic()
    {
        var path = CompositePath("c01_basic.csv");

        var suggestion = CsvDialectGuesser.Guess(path);

        Assert.Equal(",", suggestion.Separator);
        Assert.True(suggestion.HasHeader);
    }

    [Fact]
    public void Guess_SuggestsSemicolonAndHeader_ForC06Semicolon()
    {
        var path = CompositePath("c06_semicolon.csv");

        var suggestion = CsvDialectGuesser.Guess(path);

        Assert.Equal(";", suggestion.Separator);
        Assert.True(suggestion.HasHeader);
    }

    [Fact]
    public void Guess_SuggestsNoHeader_ForC07NoHeader()
    {
        var path = CompositePath("c07_no_header.csv");

        var suggestion = CsvDialectGuesser.Guess(path);

        Assert.Equal(",", suggestion.Separator);
        Assert.False(suggestion.HasHeader);
    }

    [Fact]
    public void Guess_DoesNotThrow_ForC14Preamble()
    {
        var path = CompositePath("c14_preamble.csv");

        var ex = Record.Exception(() => CsvDialectGuesser.Guess(path));

        Assert.Null(ex);
    }

    [Fact]
    public void Guess_SuggestsTabAndHeader_ForTabSeparatedFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tab-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path,
        [
            "id\tname\tage",
            "1\tAlice\t10",
            "2\tBob\t11"
        ]);

        try
        {
            var suggestion = CsvDialectGuesser.Guess(path);

            Assert.Equal("tab", suggestion.Separator);
            Assert.True(suggestion.HasHeader);
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
    public void Guess_SuggestsPipeAndHeader_ForPipeSeparatedFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pipe-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path,
        [
            "id|name|city",
            "1|Alice|Pune",
            "2|Bob|Nashik"
        ]);

        try
        {
            var suggestion = CsvDialectGuesser.Guess(path);

            Assert.Equal("|", suggestion.Separator);
            Assert.True(suggestion.HasHeader);
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
    public void Guess_ReturnsDefaults_WhenFileMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.csv");

        var suggestion = CsvDialectGuesser.Guess(path);

        Assert.Equal(",", suggestion.Separator);
        Assert.True(suggestion.HasHeader);
    }

    [Fact]
    public void Guess_ReturnsDefaults_WhenFileEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, string.Empty);

        try
        {
            var suggestion = CsvDialectGuesser.Guess(path);

            Assert.Equal(",", suggestion.Separator);
            Assert.True(suggestion.HasHeader);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static string CompositePath(string fileName)
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "SampleData", "TestData", "composite", fileName));
    }
}