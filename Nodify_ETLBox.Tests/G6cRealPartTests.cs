using System.IO;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Intake;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>Real TSV parts on temp copies (the originals in SampleData are only read).</summary>
internal static class G6cRealParts
{
    /// <summary>A temp copy of a real file and the selector of its part <paramref name="partNumber"/> (1-based, Auto-detect).</summary>
    public static async Task<(string Path, CsvPartSelector Selector)> CopyAndSelectAsync(string folder, string file, int partNumber)
    {
        var path = Path.Combine(folder, file);
        File.Copy(Path.Combine(IntakeTestData.Folder, "real", file), path, overwrite: true);
        var item = new FileIntakeFactory().CreateItem(path);
        var scan = await new IntakeScanService(new IntakeSettings()).ScanAsync(item, null, CancellationToken.None);
        var session = new IntakeEditSession(item, scan, null, "test");
        var selector = CsvPartSelector.FromSession(session, partNumber - 1, out var message);
        Assert.True(selector is not null, message);
        return (path, selector);
    }

    /// <summary>Every column a full read of the located part gives: the run's reader, to the last record.</summary>
    public static async Task<(CsvPartLocation Location, CsvReadOutcome Outcome)> FullReadAsync(string path, CsvPartSelector selector)
    {
        var location = await CsvPartLocator.LocateAsync(path, selector, CancellationToken.None);
        Assert.True(location.Ok, location.Message);
        var (outcome, _) = await CsvPartReaderTests.ReadPart(location.Part!);
        return (location, outcome);
    }

    /// <summary>A pipeline over the part: ParseValue splits <paramref name="source"/> once at <paramref name="split"/>, one slot holds <paramref name="value"/>.</summary>
    public static PipelineDefinition Definition(string example, CsvPartSelector selector, string source, string value, string split = "-")
        => PipelineDefinition.ForCsvPart(example, selector, new CsvColumnSelection(), new ParseValueSettings
        {
            SourceColumn = source,
            DelimiterLevels = [new(split, SplitMode.Once), new("|", SplitMode.All)],
            Slots = [new Slot("S1", [value])]
        }, OutputSettings.Default);
}

/// <summary>
/// Task G6c step 0b: the columns shown at design time (<see cref="CsvPartColumns"/>) are exactly the columns a
/// run reads and produces, in order, also where the selector's own names differ (trailing empty fields, an
/// empty header cell).
/// </summary>
public sealed class CsvPartColumnsTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _folder = Directory.CreateTempSubdirectory("g6c_cols_").FullName;

    public CsvPartColumnsTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    [Theory]
    [InlineData("r04_quantstudio.txt", 2)]
    [InlineData("r10_methodical_mind.txt", 4)]
    [InlineData("r11_roche_cedex_bioht.txt", 1)]
    public async Task DesignTimeColumns_AreTheColumnsOfARun_InOrder(string file, int partNumber)
    {
        var (path, selector) = await G6cRealParts.CopyAndSelectAsync(_folder, file, partNumber);

        var design = await TestTimeout.Run(() => CsvPartColumns.ReadAsync(path, selector, CancellationToken.None));
        Assert.True(design.Ok, design.Error);
        Assert.Equal(partNumber, design.Location.PartNumber);

        var (location, full) = await G6cRealParts.FullReadAsync(path, selector);
        Assert.Null(full.SettingsError);
        Assert.Null(full.Failure);
        Assert.Equal(full.Columns, design.Columns);

        // The run itself uses the LAST two design-time columns by name (for r10 and r11 names the selector does
        // not have), so a run that finds them proves the names are the run's own.
        var run = await G6bParts.Run(G6cRealParts.Definition(path, selector, design.Columns[^1], design.Columns[^2]), null);
        Assert.Null(run.Error);
        Assert.Equal(full.RecordsRead, run.Counts.Read);

        _output.WriteLine($"{file} part {partNumber}: design {design.Columns.Count} [{string.Join(", ", design.Columns.Take(10))}...]; "
            + $"selector names {location.ColumnNames.Count} [{string.Join(", ", location.ColumnNames.Take(10).Select(n => n.Length == 0 ? "''" : n))}...]");
    }

    [Fact]
    public async Task ThePartNotFound_GivesTheLocatorSentence_AndNoColumns()
    {
        var (path, selector) = await G6cRealParts.CopyAndSelectAsync(_folder, "r04_quantstudio.txt", 2);
        File.Delete(path);
        var design = await TestTimeout.Run(() => CsvPartColumns.ReadAsync(path, selector, CancellationToken.None));
        Assert.Equal(CsvPartLocator.FileMissingMessage, design.Error);
        Assert.Empty(design.Columns);
    }
}

/// <summary>Task G6c step 0c: real TSV parts through a generated DLL, compared with the app (rows, columns, facts).</summary>
[Collection("CSV part DLL")]
public sealed class CsvPartDllRealFileTests
{
    private readonly CsvPartDllFixture _fixture;
    private readonly ITestOutputHelper _output;

    public CsvPartDllRealFileTests(CsvPartDllFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData("r04_quantstudio.txt", 2, 16, "Allele1 Quencher", "-", "Allele1 Name")]
    [InlineData("r10_methodical_mind.txt", 2, 25, "1", "5", "2")]
    public async Task RealTsvPart_DllEqualsTheApp(string file, int partNumber, int columnCount, string source, string split, string value)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_fixture.Folder, "real_" + partNumber + "_" + Path.GetFileNameWithoutExtension(file))).FullName;
        var (path, selector) = await G6cRealParts.CopyAndSelectAsync(folder, file, partNumber);
        var columns = await TestTimeout.Run(() => CsvPartColumns.ReadAsync(path, selector, CancellationToken.None));
        Assert.True(columns.Ok, columns.Error);
        Assert.Equal(columnCount, columns.Columns.Count);

        // r04: "NFQ-MGB" splits at '-'. r10 has only numbers, so column "1" is split at '5': some rows parse, others are rejects.
        var definition = G6cRealParts.Definition(path, selector, source, value, split);
        var dll = await _fixture.GetAsync("real " + file, definition);
        var app = await G6bParts.Run(definition, path);
        var (result, facts) = await PartDll.RunAsync(dll, path);
        _output.WriteLine($"{file} part {partNumber}: app error {app.Error ?? "none"}, {app.Counts}, {app.Columns.Count} output columns; "
            + $"dll error {result.Error ?? "none"}, {result.Counts}; part {app.CsvPart?.PartNumber} lines {app.CsvPart?.FirstLine} to {app.CsvPart?.LastLine}");
        Assert.Null(app.Error);
        Assert.True(app.Counts.Read > 0);
        Assert.True(app.Counts.Out > 0);
        PartDll.Equal(app, result, facts);
    }
}
