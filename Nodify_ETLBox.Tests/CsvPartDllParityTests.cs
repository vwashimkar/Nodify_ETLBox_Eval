using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Generation;
using EtlDesigner.Engine.Intake;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>Task G6b step 3: the generated CSV part DLL against the app's runner.</summary>
[Collection("CSV part DLL")]
public sealed class CsvPartDllParityTests
{
    private readonly CsvPartDllFixture _fixture;
    private readonly ITestOutputHelper _output;

    public CsvPartDllParityTests(CsvPartDllFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private async Task Compare(GeneratedDll dll, PipelineDefinition definition, string path, string label)
    {
        var app = await G6bParts.Run(definition, path);
        var (result, facts) = await PartDll.RunAsync(dll, path);
        _output.WriteLine($"{label}: error {app.Error ?? "none"}; {app.Counts}; part {app.CsvPart?.PartNumber} lines {app.CsvPart?.FirstLine} to {app.CsvPart?.LastLine}, {app.CsvPart?.Match} {app.CsvPart?.Similarity}");
        PartDll.Equal(app, result, facts);
    }

    // (1) and (2)
    [Theory]
    [InlineData("example")]
    [InlineData("more rows")]
    [InlineData("blank line before the table")]
    [InlineData("other title lines")]
    public async Task I10AndVariants_DllEqualsTheApp_AndTheStandaloneCsv(string kind)
    {
        var path = kind == "example" ? G6bParts.I10Path : G6bParts.Write(_fixture.Folder, kind.Replace(' ', '_') + ".txt", G6bParts.Variant(kind));
        await Compare(_fixture.Main, _fixture.Definition, path, kind);

        var standalone = G6bParts.Write(_fixture.Folder, kind.Replace(' ', '_') + "_table.csv", G6bParts.TableOf(G6bParts.Variant(kind)));
        var whole = await G6bParts.Run(G6bParts.StandaloneDefinition(standalone), null);
        var (dll, _) = await PartDll.RunAsync(_fixture.Main, path);
        Assert.Null(dll.Error);
        Assert.Equal(whole.Columns, dll.Columns);
        Assert.Equal(G6bParts.RowTexts(whole.Columns, whole.Rows), G6bParts.RowTexts(dll.Columns, dll.Rows));
        Assert.Equal(whole.Counts, dll.Counts);
    }

    /// <summary>
    /// i01 has a YAML part, so the scan runs YamlShape (its regexes are rewritten for the DLL) and YamlCheck (YamlDotNet):
    /// the similarity of the app and of the DLL must be the same.
    /// </summary>
    [Fact]
    public async Task I01WithYaml_GivesTheSameAnswerAndSimilarity_InAppAndDll()
    {
        var path = IntakeTestData.PathOf("i01_sample");
        var app = await G6bParts.Run(_fixture.Definition, path);
        Assert.NotNull(app.CsvPart?.Similarity);
        await Compare(_fixture.Main, _fixture.Definition, path, "i01");
    }

    [Fact]
    public void ReplaceGeneratedRegex_TurnsTheYamlShapeRegexesIntoCachedRegexObjects()
    {
        var source = DllGeneratorSource("Engine/Intake/YamlShape.cs");
        var replaced = CsvPipelineDllGenerator.ReplaceGeneratedRegex(source);
        Assert.DoesNotContain("[GeneratedRegex", replaced);
        Assert.Contains("Regex KeyLine() => __KeyLine ??= new global::System.Text.RegularExpressions.Regex(@\"", replaced);
        Assert.Equal(3, Regex.Matches(replaced, @"\?\?= new global::System\.Text\.RegularExpressions\.Regex").Count);
        Assert.Equal("class C { }", CsvPipelineDllGenerator.ReplaceGeneratedRegex("class C { }"));
    }

    private static string DllGeneratorSource(string rel) => (string)typeof(CsvPipelineDllGenerator).Assembly
        .GetType("EtlDesigner.Engine.Generation.DllGenerator")!.GetMethod("ReadSourceContent", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [rel])!;

    [Fact]
    public async Task ARejectedRecord_HasTheSameReasonWithItsFileLine_InAppAndDll()
    {
        var path = G6bParts.Write(_fixture.Folder, "reject.txt", G6bParts.Variant("a reject"));
        await Compare(_fixture.Main, _fixture.Definition, path, "a reject");
        var (dll, _) = await PartDll.RunAsync(_fixture.Main, path);
        Assert.EndsWith("(file line 28)", Assert.Single(dll.Rejects).Reason);
    }

    // (3) and (5)
    [Theory]
    [MemberData(nameof(PipelineRunnerCsvPartTests.Failures), MemberType = typeof(PipelineRunnerCsvPartTests))]
    public async Task EachLocatorFailure_GivesTheSameSentence_InAppAndDll(string kind, CsvPartFailure reason)
    {
        var (path, selector) = PipelineRunnerCsvPartTests.FailureCase(kind, _fixture.Selector, _fixture.Folder);
        var definition = G6bParts.Definition(G6bParts.I10Path, selector);
        var dll = selector == _fixture.Selector ? _fixture.Main : await _fixture.GetAsync(kind, definition);

        var app = await G6bParts.Run(definition, path);
        var (result, facts) = await PartDll.RunAsync(dll, path);

        _output.WriteLine($"{kind} ({reason}): {result.Error}");
        Assert.NotNull(app.Error);
        Assert.Equal((await CsvPartLocator.LocateAsync(path, selector, CancellationToken.None)).Reason, reason);
        PartDll.Equal(app, result, facts);
    }

    [Fact]
    public async Task TheExampleMovedDll_StillReadsAnotherFile()
    {
        var (_, selector) = PipelineRunnerCsvPartTests.FailureCase("example moved", _fixture.Selector, _fixture.Folder);
        var definition = G6bParts.Definition(G6bParts.I10Path, selector);
        var dll = await _fixture.GetAsync("example moved", definition);
        var path = G6bParts.Write(_fixture.Folder, "moved_other.txt", G6bParts.Variant("more rows"));
        await Compare(dll, definition, path, "example-moved DLL on another file");
        Assert.Null((await PartDll.RunAsync(dll, path)).Result.Error);
    }

    // (4)
    private static string Tsv(int rows) =>
        "Instrument: PlateReader 9\nOperator: A. Verma\nDate: 2026-11-05\nRun: 12\nSoftware: Reader 2.1\n\nResults of the run\n"
        + "well\tsample\tvalue\tflag\n"
        + string.Join("\n", Enumerable.Range(1, rows).Select(n => $"{(char)('A' + (n - 1) / 12)}{(n - 1) % 12 + 1}\tS-{n:000}\t0.{n:000}\t{(n % 5 == 0 ? "high" : "ok")}"))
        + "\n\nEnd of report\n";

    [Fact]
    public async Task ATsvPartWithATitleLine_DllOnASecondFile_EqualsTheApp()
    {
        var example = G6bParts.Write(_fixture.Folder, "plate_1.txt", Tsv(6));
        var second = G6bParts.Write(_fixture.Folder, "plate_2.txt", Tsv(30));
        var selector = await CsvPartLocatorTests.SelectorAsync(example);
        _output.WriteLine($"TSV selector: delimiter {CsvPartLocator.DelimiterWord(selector.Expected.Delimiter)}, skip {selector.Expected.SkipLines}, header {selector.Expected.HasHeader}, columns {string.Join(",", selector.Expected.ColumnNames)}");
        Assert.Equal("\t", selector.Expected.Delimiter);
        var parseValue = new ParseValueSettings
        {
            SourceColumn = "sample",
            DelimiterLevels = [new("-", SplitMode.Once), new("|", SplitMode.All)],
            Slots = [new Slot("S1", ["well", "value", "flag"])]
        };
        var definition = PipelineDefinition.ForCsvPart(example, selector, new CsvColumnSelection(), parseValue, OutputSettings.Default);
        var dll = await _fixture.GetAsync("tsv", definition);

        await Compare(dll, definition, second, "TSV second file");
        var (result, _) = await PartDll.RunAsync(dll, second);
        Assert.Null(result.Error);
        Assert.Equal(30, result.Counts.Read);
        Assert.Equal(30, result.Rows.Count);
    }

    // (6)
    [Theory]
    [InlineData("utf-8 with BOM")]
    [InlineData("utf-16le with BOM")]
    [InlineData("utf-16be with BOM")]
    public async Task Utf16AndBomCopiesOfI10_DllEqualsTheApp(string kind)
    {
        Encoding encoding = kind switch
        {
            "utf-8 with BOM" => new UTF8Encoding(true),
            "utf-16le with BOM" => new UnicodeEncoding(false, true),
            _ => new UnicodeEncoding(true, true)
        };
        var path = G6bParts.Write(_fixture.Folder, kind.Replace(' ', '_') + ".txt", G6bParts.Variant("more rows"), encoding);
        await Compare(_fixture.Main, _fixture.Definition, path, kind);
        Assert.Null((await PartDll.RunAsync(_fixture.Main, path)).Result.Error);
    }

    // (7)
    [Fact]
    public async Task Cancel_AfterTheFirstRows_ReturnsCancelled_AndReleasesTheFile()
    {
        var text = G6bParts.Variant("many rows");
        var cancelledAfterRows = false;
        // The read of 4,000 rows takes well under 100 ms after the scan, so the delay grows in small steps to land in it.
        for (var delay = 5.0; delay <= 1280 && !cancelledAfterRows; delay *= 1.15)
        {
            var path = G6bParts.Write(_fixture.Folder, $"cancel_{(int)delay}.txt", text);
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(delay));
            var clock = Stopwatch.StartNew();
            var (result, _) = await PartDll.RunAsync(_fixture.Main, path, cts.Token);
            _output.WriteLine($"delay {(int)delay} ms: cancelled {result.Cancelled}, read {result.Counts.Read}, {clock.ElapsedMilliseconds} ms");
            Assert.True(!result.Cancelled || clock.ElapsedMilliseconds < 5_000, $"the cancel took {clock.ElapsedMilliseconds} ms");
            Assert.True(result.Cancelled || result.Error is null, $"unexpected error: {result.Error}");
            cancelledAfterRows = result.Cancelled && result.Counts.Read > 0;
            File.Delete(path);
            Assert.False(File.Exists(path));
        }

        Assert.True(cancelledAfterRows, "no run was cancelled after its first rows");
    }

    // (10)
    [Fact]
    public async Task TheOriginalFiles_AreUnchanged_AndDeletableRightAfterADllRun()
    {
        var original = G6bParts.Sha(G6bParts.I10Path);
        var originalTime = File.GetLastWriteTimeUtc(G6bParts.I10Path);
        await PartDll.RunAsync(_fixture.Main, G6bParts.I10Path);
        Assert.Equal(original, G6bParts.Sha(G6bParts.I10Path));
        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(G6bParts.I10Path));

        var path = G6bParts.Write(_fixture.Folder, "delete_me.txt", G6bParts.Variant("more rows"));
        var sha = G6bParts.Sha(path);
        var time = File.GetLastWriteTimeUtc(path);
        var (result, _) = await PartDll.RunAsync(_fixture.Main, path);
        Assert.Null(result.Error);
        Assert.Equal(sha, G6bParts.Sha(path));
        Assert.Equal(time, File.GetLastWriteTimeUtc(path));
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    // (8), (9) and (11): the console consumer in its own process.
    [Fact]
    public async Task TheConsoleConsumer_BuildsAndRunsTheDll_InItsOwnProcess_AndWithoutYamlDotNetSaysSo()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Nodify_ETLBox_Eval.slnx")))
        {
            root = root.Parent;
        }

        var project = Path.Combine(root!.FullName, "Consumer", "CsvPipelineConsumer", "CsvPipelineConsumer.csproj");
        var output = Path.Combine(_fixture.Folder, "consumer");
        var build = await Exec("dotnet", ["build", project, "-c", "Release", "-o", output, $"-p:PipelineDll={_fixture.Main.Generation.DllPath}", "-nologo", "-v:q"], root.FullName);
        Assert.True(build.Code == 0, "consumer build failed: " + build.Output);
        Assert.True(File.Exists(Path.Combine(output, "YamlDotNet.dll")));

        var input = G6bParts.Write(_fixture.Folder, "consumer_input.txt", G6bParts.Variant("more rows"));
        var run = await Exec("dotnet", [Path.Combine(output, "CsvPipelineConsumer.dll"), input, "--all"], output);
        _output.WriteLine(run.Output);
        Assert.Equal(0, run.Code);
        var app = await G6bParts.Run(_fixture.Definition, input);
        var printed = run.Output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("row: ", StringComparison.Ordinal)).Select(l => l[5..]);
        Assert.Equal(G6bParts.RowTexts(app.Columns, app.Rows), printed);
        Assert.Contains($"Part PartNumber: {app.CsvPart!.PartNumber}", run.Output);
        Assert.False(File.Exists(Path.Combine(output, "lineage.log")));
        Assert.False(Directory.Exists(Path.Combine(output, "templates")));

        // (9) Without YamlDotNet.dll. With the consumer's deps.json the .NET host itself refuses to start; without
        // deps.json (a DLL copied into a plugin folder) the DLL's own check answers with one sentence.
        var noYaml = Path.Combine(_fixture.Folder, "consumer_no_yaml");
        CopyFolder(output, noYaml);
        File.Delete(Path.Combine(noYaml, "YamlDotNet.dll"));
        var withDeps = await Exec("dotnet", [Path.Combine(noYaml, "CsvPipelineConsumer.dll"), input], noYaml);
        _output.WriteLine($"Without YamlDotNet.dll, with deps.json: exit {withDeps.Code}: {withDeps.Output}");
        Assert.NotEqual(0, withDeps.Code);

        File.Delete(Path.Combine(noYaml, "CsvPipelineConsumer.deps.json"));
        var withoutDeps = await Exec("dotnet", [Path.Combine(noYaml, "CsvPipelineConsumer.dll"), input], noYaml);
        _output.WriteLine($"Without YamlDotNet.dll and deps.json: exit {withoutDeps.Code}: {withoutDeps.Output}");
        Assert.Equal(2, withoutDeps.Code);
        Assert.Contains("Error: " + CsvPipelineDllGenerator.MissingYamlMessage, withoutDeps.Output);
        Assert.Equal(G6bParts.Sha(input), G6bParts.Sha(input));
        File.Delete(input);
        Assert.False(File.Exists(input));
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static async Task<(int Code, string Output)> Exec(string exe, string[] arguments, string workingFolder)
    {
        var start = new ProcessStartInfo(exe) { WorkingDirectory = workingFolder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout + await stderr);
    }
}
