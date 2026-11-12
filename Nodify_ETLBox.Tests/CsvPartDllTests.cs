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

/// <summary>
/// The DLLs of task G6b, each generated once for the whole collection and deleted at the end: the i10 CSV part
/// DLL, a TSV part DLL, two DLLs with a tampered selector (delimiter, example range) and one CsvFile DLL (C01).
/// </summary>
public sealed class CsvPartDllFixture : IAsyncLifetime
{
    private readonly Dictionary<string, GeneratedDll> _dlls = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Folder { get; } = Directory.CreateTempSubdirectory("g6b_dll_").FullName;

    public CsvPartSelector Selector { get; private set; } = null!;

    public PipelineDefinition Definition { get; private set; } = null!;

    internal GeneratedDll Main { get; private set; } = null!;

    public long MainCompileMs { get; private set; }

    public async Task InitializeAsync()
    {
        Selector = await G6bParts.I10SelectorAsync();
        Definition = G6bParts.Definition(G6bParts.I10Path, Selector);
        var clock = Stopwatch.StartNew();
        Main = await GetAsync("main", Definition);
        MainCompileMs = clock.ElapsedMilliseconds;
    }

    /// <summary>The DLL of <paramref name="definition"/>, generated on first use under <paramref name="key"/>.</summary>
    internal async Task<GeneratedDll> GetAsync(string key, PipelineDefinition definition)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_dlls.TryGetValue(key, out var dll))
            {
                var app = await G6bParts.Run(definition, null);
                dll = GeneratedDll.Create(definition, app.Columns);
                _dlls[key] = dll;
            }

            return dll;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task DisposeAsync()
    {
        foreach (var dll in _dlls.Values)
        {
            dll.Dispose();
        }

        ProjectEditorTests.DeleteTempFolder(Folder);
        return Task.CompletedTask;
    }
}

[CollectionDefinition("CSV part DLL")]
public sealed class CsvPartDllCollection : ICollectionFixture<CsvPartDllFixture>;

/// <summary>Helpers that compare a DLL run with an app run.</summary>
internal static class PartDll
{
    public static readonly string[] FactNames = ["FileName", "Sha256", "TemplateId", "TemplateName", "Similarity", "Match", "PartNumber", "FirstLine", "LastLine"];

    public static async Task<(DllRunResult Result, Dictionary<string, object?> Facts)> RunAsync(GeneratedDll dll, string path, CancellationToken token = default)
    {
        var result = await dll.RunAsync(path, token);
        var raw = dll.LastResult!;
        var facts = FactNames.ToDictionary(n => n, n => raw.GetType().GetProperty(n)!.GetValue(raw));
        facts["PartColumns"] = raw.GetType().GetProperty("PartColumns")!.GetValue(raw) is IReadOnlyList<string> c ? string.Join(",", c) : null;
        return (result, facts);
    }

    public static Dictionary<string, object?> FactsOf(PipelineResult app) => app.CsvPart is not { } f
        ? FactNames.Append("PartColumns").ToDictionary(n => n, _ => (object?)null)
        : new Dictionary<string, object?>
        {
            ["FileName"] = f.FileName, ["Sha256"] = f.Sha256, ["TemplateId"] = f.TemplateId, ["TemplateName"] = f.TemplateName,
            ["Similarity"] = f.Similarity, ["Match"] = f.Match, ["PartNumber"] = f.PartNumber, ["FirstLine"] = f.FirstLine,
            ["LastLine"] = f.LastLine, ["PartColumns"] = string.Join(",", f.ColumnNames)
        };

    /// <summary>Row for row, rejects, flagged rows, columns, counts, error and the facts.</summary>
    public static void Equal(PipelineResult app, DllRunResult dll, Dictionary<string, object?> dllFacts)
    {
        Assert.Equal(app.Error, dll.Error);
        Assert.Equal(app.Cancelled, dll.Cancelled);
        Assert.Equal(app.LicenceLimitReached, dll.LicenceLimitReached);
        Assert.Equal(app.Columns, dll.Columns);
        Assert.Equal(G6bParts.RowTexts(app.Columns, app.Rows), G6bParts.RowTexts(dll.Columns, dll.Rows));
        Assert.Equal(app.Rejects, dll.Rejects);
        Assert.Equal(app.Flagged, dll.Flagged);
        Assert.Equal(app.Counts, dll.Counts);
        Assert.Equal(FactsOf(app), dllFacts);
    }

    public static IReadOnlyList<string> References(string dllPath)
    {
        using var stream = File.OpenRead(dllPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.AssemblyReferences.Select(h => metadata.GetAssemblyReference(h))
            .Select(r => $"{metadata.GetString(r.Name)} {r.Version}").OrderBy(n => n, StringComparer.Ordinal).ToList();
    }
}

/// <summary>Task G6b step 2: the generator for a CSV part pipeline.</summary>
[Collection("CSV part DLL")]
public sealed class CsvPartDllGenerationTests
{
    private readonly CsvPartDllFixture _fixture;
    private readonly ITestOutputHelper _output;

    public CsvPartDllGenerationTests(CsvPartDllFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task CsvPartDll_Compiles_WithTheIntakeFiles_AndReferencesYamlDotNet_ButACsvFileDllDoesNot()
    {
        var dll = _fixture.Main;
        var references = PartDll.References(dll.Generation.DllPath!);
        _output.WriteLine($"CSV part DLL: {new FileInfo(dll.Generation.DllPath!).Length:N0} bytes, generated and compiled in {_fixture.MainCompileMs:N0} ms (with the app run for the columns)");
        _output.WriteLine("References: " + string.Join("; ", references));
        Assert.Contains(references, r => r.StartsWith("YamlDotNet ", StringComparison.Ordinal));
        Assert.Contains(references, r => r.StartsWith("ETLBox ", StringComparison.Ordinal));
        Assert.Contains(references, r => r.StartsWith("ETLBox.Csv ", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("System.Private.", StringComparison.Ordinal));
        Assert.Equal(CsvPipelineDllGenerator.SourceFiles.Concat(CsvPipelineDllGenerator.CsvPartSourceFiles), CsvPipelineDllGenerator.SourceFilesFor(_fixture.Definition));
        foreach (var file in CsvPipelineDllGenerator.SourceFilesFor(_fixture.Definition))
        {
            Assert.True(File.Exists(Path.Combine(dll.Folder, Path.GetFileName(file))), file);
        }

        var c01 = PipelineDefinitionTests.C01Definition();
        var csvFile = await _fixture.GetAsync("c01", c01);
        var csvFileReferences = PartDll.References(csvFile.Generation.DllPath!);
        _output.WriteLine($"CsvFile DLL: {new FileInfo(csvFile.Generation.DllPath!).Length:N0} bytes; references: " + string.Join("; ", csvFileReferences));
        Assert.Same(CsvPipelineDllGenerator.SourceFiles, CsvPipelineDllGenerator.SourceFilesFor(c01));
        Assert.DoesNotContain(csvFileReferences, r => r.StartsWith("YamlDotNet", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(csvFile.Folder, "CsvPartLocator.cs")));
    }

    [Fact]
    public void PublicSurface_IsTheEntryClassAndTheThreeResultTypes_PlusTheAdditivePartFields()
    {
        var exported = _fixture.Main.Assembly.GetExportedTypes();
        Assert.Equal(
            new[] { "Generated.CsvPipeline", "Generated.PipelineRunProgress", "Generated.PipelineRunResult", "Generated.RejectInfo" },
            exported.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal));

        var result = exported.Single(t => t.Name == "PipelineRunResult");
        var names = result.GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var name in new[] { "Rows", "Columns", "Rejects", "Flagged", "Counts", "Cancelled", "Error", "LicenceLimitReached", "ElapsedMs" }
                     .Concat(PartDll.FactNames).Append("PartColumns"))
        {
            Assert.Contains(name, names);
        }

        Assert.All(result.GetProperties(), p => Assert.False((p.PropertyType.Assembly.GetName().Name ?? "").StartsWith("ETLBox", StringComparison.Ordinal)));
        var run = exported.Single(t => t.Name == "CsvPipeline").GetMethod("RunAsync")!;
        Assert.Equal(["filePath", "progress", "cancellationToken"], run.GetParameters().Select(p => p.Name));
    }

    [Fact]
    public async Task DefinitionJson_HoldsTheSelectorAsData_AndOutputColumnsEqualTheAppColumns()
    {
        var json = (string)_fixture.Main.Entry.GetProperty("DefinitionJson")!.GetValue(null)!;
        var back = SettingsJson.DeserializePipelineDefinition(json);
        Assert.Equal(_fixture.Definition, back);
        Assert.Contains("\"csvPart\"", json);
        Assert.Contains("\"template\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chloride_pct", json);

        var app = await G6bParts.Run(_fixture.Definition, null);
        Assert.Equal(app.Columns, (IReadOnlyList<string>)_fixture.Main.Entry.GetProperty("OutputColumns")!.GetValue(null)!);
    }

    [Fact]
    public void Readme_ListsWhatToDeploy_WhatItReads_AndEachFailure()
    {
        var readme = File.ReadAllText(_fixture.Main.Generation.ReadmePath!);
        foreach (var text in new[]
                 {
                     "CSV part -> ParseValue -> DotNetObject", "ETLBox.dll, ETLBox.Csv.dll, ETLBox.Json.dll (3.11.1), CsvHelper.dll", "YamlDotNet.dll (18.1.0)",
                     "Generate DLL does not copy them", CsvPipelineDllGenerator.MissingYamlMessage, "finds the part by the file's structure",
                     "the consuming app needs its own valid ETLBox licence", "This DLL contains no licence key", "It writes no file",
                     "The file does not look like the example", "several parts like it", "another delimiter", "other column names", "\"csvPart\""
                 })
        {
            Assert.Contains(text, readme);
        }
    }

    [Fact]
    public async Task TheDll_WritesNoFile_NoLineageLog_AndNoTemplatesFolder()
    {
        var path = G6bParts.Write(_fixture.Folder, "nofiles.txt", G6bParts.Variant("more rows"));
        string[] Listing(string folder) => Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var dllFolder = Listing(_fixture.Main.Folder);
        var dataFolder = Listing(_fixture.Folder);
        var lineageBefore = File.Exists(Path.Combine(AppContext.BaseDirectory, "lineage.log"));

        var (result, _) = await PartDll.RunAsync(_fixture.Main, path);

        Assert.Null(result.Error);
        Assert.Equal(dllFolder, Listing(_fixture.Main.Folder));
        Assert.Equal(dataFolder, Listing(_fixture.Folder));
        Assert.False(lineageBefore);
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "lineage.log")));
        Assert.False(Directory.Exists(Path.Combine(AppContext.BaseDirectory, "templates")));
    }

    /// <summary>
    /// No licence key in either kind of DLL: the bytes, the strings and the README are searched for the licence file name, licence
    /// API names and anything shaped like a key (a long base64 run). etlbox.lic itself is never read.
    /// </summary>
    [Theory]
    [InlineData("main")]
    [InlineData("c01")]
    public async Task NoLicenceKey_InTheDll_OrItsReadme(string key)
    {
        var dll = key == "main" ? _fixture.Main : await _fixture.GetAsync("c01", PipelineDefinitionTests.C01Definition());
        var bytes = File.ReadAllBytes(dll.Generation.DllPath!);
        var texts = new[] { Encoding.ASCII.GetString(bytes), Encoding.Unicode.GetString(bytes), Encoding.Unicode.GetString(bytes.Skip(1).ToArray()), File.ReadAllText(dll.Generation.ReadmePath!) };
        var keyShape = new Regex("[A-Za-z0-9+/]{100,}={0,2}");
        foreach (var text in texts)
        {
            foreach (var word in new[] { "etlbox.lic", "LicenseKey", "LicenceKey", "SetLicense", "LicenseCheck" })
            {
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
            }

            Assert.False(keyShape.IsMatch(text), "something shaped like a licence key is in the DLL or README");
        }

        Assert.Empty(dll.Assembly.GetManifestResourceNames());
    }
}
