using System.IO;
using System.Text;
using EtlDesigner.Engine.Csv;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>
/// Task G6c step 0a: CsvHelper's delimiter detection (on by default in ETLBox's CsvSource, [P] G6b) looks
/// at the first text in the buffer. Title lines full of commas above a semicolon or tab table can make it
/// pick a comma. A part read must use the delimiter of its selector, so detection is off for the part
/// overload only; the path overload (the CsvFile node) keeps ETLBox's default.
/// </summary>
public sealed class G6cDelimiterDetectionTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _folder = Directory.CreateTempSubdirectory("g6c_detect_").FullName;

    public G6cDelimiterDetectionTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    /// <summary>
    /// Two title lines with commas, then a header and three rows with <paramref name="d"/>; the header and
    /// the values also hold commas (a decimal comma, a unit after a comma), so every line has the same comma
    /// count. Found by probing [P] 2026-11-11; titles with commas alone did not mislead the detector.
    /// </summary>
    internal static string MisleadingText(char d)
        => "Plate 1, reader A, 450 nm\nUser X, 2026-11-11, site 2\n"
           + $"Well{d}Conc, ng/ml{d}OD, 450\nA1{d}0,5{d}0,6\nA2{d}0,7{d}0,8\nA3{d}0,9{d}1,0";

    private const string Before = "Instrument: X\nOperator: Y\n\n";

    private const string After = "\n\nEnd of report\n";

    private CsvPartSource WritePart(string text, string delimiter)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".txt");
        var encoding = new UTF8Encoding(false);
        File.WriteAllBytes(path, encoding.GetBytes(Before + text + After));
        return new CsvPartSource(path, encoding.GetByteCount(Before), encoding.GetByteCount(text), "utf-8", delimiter, true, 2, 4);
    }

    [Theory]
    [InlineData(';')]
    [InlineData('\t')]
    public async Task PartRead_UsesTheSelectorsDelimiter_EvenWhenTheTitleLinesAreFullOfCommas(char d)
    {
        var part = WritePart(MisleadingText(d), d.ToString());
        var (outcome, records) = await CsvPartReaderTests.ReadPart(part);
        _output.WriteLine($"{(d == '\t' ? "tab" : d.ToString())}: {outcome.Columns.Count} columns [{string.Join(" | ", outcome.Columns)}], {records.Count} records, "
            + $"{outcome.StructuralErrors.Count} errors, failure {outcome.Failure?.Message ?? "none"}");

        Assert.Null(outcome.SettingsError);
        Assert.Null(outcome.Failure);
        Assert.Equal(["Well", "Conc, ng/ml", "OD, 450"], outcome.Columns);
        Assert.Equal(["A1|0,5|0,6", "A2|0,7|0,8", "A3|0,9|1,0"], records.Select(r => string.Join("|", r.Values)));
        Assert.Equal([7L, 8L, 9L], records.Select(r => r.FileLine!.Value));
    }

    /// <summary>
    /// The CsvFile path is unchanged: the same misleading text as a whole file is still read with a comma
    /// (detection on), as before G6c. Observed [P] 2026-11-11 before the change: three columns
    /// "Well;Conc", "ng/ml;OD", "450" for ';'. This pins the old behaviour so the part fix cannot leak in.
    /// </summary>
    [Theory]
    [InlineData(';')]
    [InlineData('\t')]
    public async Task PathRead_IsUnchanged_DetectionStaysOn(char d)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, MisleadingText(d), new UTF8Encoding(false));
        var (outcome, records) = await CsvPartReaderTests.ReadPath(new CsvFileSettings(path, d.ToString(), true, 2, "utf-8"));
        _output.WriteLine($"{(d == '\t' ? "tab" : d.ToString())}: {outcome.Columns.Count} columns [{string.Join(" | ", outcome.Columns)}], {records.Count} records, "
            + $"{outcome.StructuralErrors.Count} errors, failure {outcome.Failure?.Message ?? "none"}");

        Assert.Null(outcome.SettingsError);
        Assert.Null(outcome.Failure);
        Assert.Equal([$"Well{d}Conc", $"ng/ml{d}OD", "450"], outcome.Columns);
        Assert.Equal($"A1{d}0|5{d}0|6", string.Join("|", records[0].Values));
    }
}
