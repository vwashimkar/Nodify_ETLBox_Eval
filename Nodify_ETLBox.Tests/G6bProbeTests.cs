using System.Dynamic;
using System.IO;
using ETLBox;
using ETLBox.Csv;
using ETLBox.DataFlow;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>
/// Task G6b step 0a: why a tab delimiter with skip lines above 0 gave one column (found in G6a).
/// Each case reads a small file through a raw ETLBox CsvSource and records the column count.
/// </summary>
public sealed class G6bProbeTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _folder = Directory.CreateTempSubdirectory("g6b_probe_").FullName;

    public G6bProbeTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private async Task<(int Columns, string? Error)> Read(string text, Action<CsvSource<ExpandoObject>> configure)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".tsv");
        File.WriteAllText(path, text);
        var source = new CsvSource<ExpandoObject>(path);
        configure(source);
        var sink = new MemoryDestination<ExpandoObject>();
        source.LinkTo(sink);
        string? error = null;
        try
        {
            await TestTimeout.Run(() => Network.ExecuteAsync(source));
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }

        var first = sink.Data.FirstOrDefault() as IDictionary<string, object?>;
        return (first?.Count ?? 0, error);
    }

    private const string Data = "id\tname\tnote\n1\ta\tx\n2\tb\ty\n";

    private static Action<CsvSource<ExpandoObject>> Tab(int skip, bool? detect = null, Action<string>? seen = null) => s =>
    {
        s.Configuration.Delimiter = "\t";
        if (detect is not null)
        {
            s.Configuration.DetectDelimiter = detect.Value;
        }

        if (skip > 0)
        {
            s.Configuration.ShouldSkipRecord = a =>
            {
                seen?.Invoke(a.Row.Parser.Delimiter);
                return a.Row.Parser.Row <= skip;
            };
        }
    };

    /// <summary>
    /// Result [P] on 2026-11-11: ETLBox's CsvSource has CsvHelper's DetectDelimiter switched ON by default.
    /// The detector looks at the first text in the buffer; title lines in front of a tab table make it
    /// pick another delimiter (the parser reports it in ShouldSkipRecord), so every line is one column.
    /// DetectDelimiter = false keeps the configured tab.
    /// </summary>
    [Fact]
    public async Task TabWithSkipLines_WhichSettingMatters()
    {
        var g6a = CsvPartReaderTests.PartText('\t', true, 1);
        var seenDelimiters = new List<string>();
        var cases = new (string Name, string Text, Action<CsvSource<ExpandoObject>> Configure)[]
        {
            ("tab, skip 0", Data, Tab(0)),
            ("tab, one-word title, skip 1", "Title\n" + Data, Tab(1)),
            ("tab, two-word title, skip 1", "Title line\n" + Data, Tab(1, seen: d => { lock (seenDelimiters) { seenDelimiters.Add(d); } })),
            ("tab, two-word title, skip 1, DetectDelimiter false", "Title line\n" + Data, Tab(1, detect: false)),
            ("G6a text, skip 1", g6a, Tab(1)),
            ("G6a text, skip 1, DetectDelimiter false", g6a, Tab(1, detect: false)),
            ("comma, two-word title, skip 1", "Title line\nid,name,note\n1,a,x\n", s => { s.Configuration.Delimiter = ","; s.Configuration.ShouldSkipRecord = a => a.Row.Parser.Row <= 1; }),
        };

        foreach (var (name, text, configure) in cases)
        {
            var (columns, error) = await Read(text, configure);
            _output.WriteLine($"{name}: {columns} columns{(error is null ? "" : ", " + error)}");
        }

        _output.WriteLine($"DetectDelimiter default: {new CsvSource<ExpandoObject>().Configuration.DetectDelimiter}");
        _output.WriteLine("Parser delimiters seen (two-word title): " + string.Join(" ", seenDelimiters.Distinct().Select(d => d == "\t" ? "tab" : d == " " ? "space" : d)));
    }

    [Fact]
    public void ResolveDelimiter_ATabCharacter_IsATab()
    {
        Assert.Equal("	", EtlDesigner.Engine.Csv.CsvRecordReader.ResolveDelimiter("	", out var error));
        Assert.Null(error);
        Assert.Equal("	", EtlDesigner.Engine.Csv.CsvRecordReader.ResolveDelimiter("tab", out _));
        Assert.Equal(",", EtlDesigner.Engine.Csv.CsvRecordReader.ResolveDelimiter("", out _));
        Assert.Equal(",", EtlDesigner.Engine.Csv.CsvRecordReader.ResolveDelimiter(" ", out _));
    }

    /// <summary>
    /// The real TSV files as read-only parts, located and read through the G6a locator. Only the column
    /// count and the first column name are asserted; the parts used and their first rows are written out.
    /// </summary>
    [Theory]
    [InlineData("r04_quantstudio.txt")]
    [InlineData("r05_agilent_gen5.txt")]
    [InlineData("r10_methodical_mind.txt")]
    [InlineData("r11_roche_cedex_bioht.txt")]
    public async Task RealTsvParts_AreLocatedAndRead_WithAllTheirColumns(string file)
    {
        var path = Path.Combine(IntakeTestData.Folder, "real", file);
        var item = new EtlDesigner.Engine.Intake.FileIntakeFactory().CreateItem(path);
        var scan = await new EtlDesigner.Engine.Intake.IntakeScanService(new EtlDesigner.Engine.Intake.IntakeSettings()).ScanAsync(item, null, CancellationToken.None);
        var session = new EtlDesigner.Engine.Intake.IntakeEditSession(item, scan, null, "test");
        var read = 0;
        for (var i = 0; i < session.Containers.Count; i++)
        {
            var container = session.Containers[i];
            if (container.Delimiter != "	" && container.Format != "tsv")
            {
                continue;
            }

            var selector = EtlDesigner.Engine.Intake.CsvPartSelector.FromSession(session, i, out var message);
            if (selector is null)
            {
                _output.WriteLine($"{file} part {i + 1} ({container.Format}, lines {container.ContentFirstLine} to {container.ContentLastLine}): no selector: {message}");
                continue;
            }

            var location = await EtlDesigner.Engine.Intake.CsvPartLocator.LocateAsync(path, selector, CancellationToken.None);
            if (!location.Ok)
            {
                _output.WriteLine($"{file} part {i + 1}: not located: {location.Message}");
                continue;
            }

            var (outcome, records) = await CsvPartReaderTests.ReadPart(location.Part!);
            Assert.Null(outcome.SettingsError);
            Assert.Null(outcome.Failure);
            _output.WriteLine($"{file} part {location.PartNumber} lines {location.FirstLine} to {location.LastLine}, skip {selector.Expected.SkipLines}, header {selector.Expected.HasHeader}: "
                + $"{outcome.Columns.Count} columns read, {selector.Expected.ColumnCount} expected; first column '{outcome.Columns.FirstOrDefault()}' (expected '{selector.Expected.ColumnNames.FirstOrDefault()}'); "
                + $"{records.Count} records; first row: {(records.Count > 0 ? string.Join(" | ", records[0].Values.Take(6)) : "-")}");
            Assert.True(outcome.Columns.Count > 1, "a tab part must not be read as one column");
            // The selector drops trailing empty fields (G6a rule), ETLBox keeps them as columns, so ETLBox may have more.
            Assert.True(outcome.Columns.Count >= selector.Expected.ColumnCount, $"{outcome.Columns.Count} columns read, {selector.Expected.ColumnCount} expected");
            var firstName = selector.Expected.ColumnNames[0];
            Assert.Equal(string.IsNullOrWhiteSpace(firstName) ? "Col1" : firstName, outcome.Columns[0]);
            read++;
        }

        Assert.True(read > 0, $"{file}: no tab part was read");
    }
}
