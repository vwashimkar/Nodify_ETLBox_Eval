using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Generation;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>
/// A generated CsvFile -> ParseValue -> DotNetObject DLL gives exactly the app's results.
/// Each DLL is generated into a temp folder and loaded from bytes into a collectible
/// AssemblyLoadContext; ETLBox comes from the test's own dependencies.
/// </summary>
[Collection(LargeFileCollection.Name)]
public class CsvPipelineDllTests
{
    public static TheoryData<string> ManifestRows() => CompositeManifestTests.ManifestRows();

    [Theory]
    [MemberData(nameof(ManifestRows))]
    public async Task ManifestRow_DllResult_EqualsAppResult(string testId)
    {
        var definition = await PipelineRunnerTests.ManifestDefinitionAsync(testId);
        var app = await RunApp(definition);
        using var dll = GeneratedDll.Create(definition, app.Columns);

        var result = await dll.RunAsync(definition.CsvFile.Path);

        Assert.Null(app.Error);
        Assert.Null(result.Error);
        Assert.False(result.Cancelled);
        Assert.Equal(app.Columns, result.Columns);
        Assert.Equal(app.Rows.Count, result.Rows.Count);
        for (var i = 0; i < app.Rows.Count; i++)
        {
            Assert.Equal(app.Columns.Select(c => app.Rows[i][c]), result.Columns.Select(c => result.Rows[i][c]));
            Assert.Equal(app.Rows[i].OrderBy(p => p.Key, StringComparer.Ordinal), result.Rows[i].OrderBy(p => p.Key, StringComparer.Ordinal));
        }

        Assert.Equal(app.Rejects, result.Rejects);
        Assert.Equal(app.Flagged, result.Flagged);
        Assert.Equal(app.Counts, result.Counts);
        Assert.Equal(app.LicenceLimitReached, result.LicenceLimitReached);
    }

    [Fact]
    public async Task Cancel_AfterTheFirstRows_ReturnsCancelled_AndReleasesTheFile()
    {
        var definition = PipelineDefinitionTests.C01Definition();
        using var dll = GeneratedDll.Create(definition, (await RunApp(definition)).Columns);

        var cancelledAfterRows = false;
        for (var delay = 5; delay <= 640 && !cancelledAfterRows; delay *= 2)
        {
            var path = PipelineRunnerTests.PrivateCopyOfC10();
            try
            {
                using var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromMilliseconds(delay));

                var result = await dll.RunAsync(path, cts.Token);

                Assert.True(result.Cancelled || result.LicenceLimitReached || result.Error is null, $"unexpected error: {result.Error}");
                cancelledAfterRows = result.Cancelled && result.Counts.Read > 0;
            }
            finally
            {
                File.Delete(path);
            }

            Assert.False(File.Exists(path));
        }

        Assert.True(cancelledAfterRows, "no run was cancelled after its first rows");
    }

    [Fact]
    public async Task Cancel_BeforeStart_ReturnsCancelled()
    {
        var definition = PipelineDefinitionTests.C01Definition();
        using var dll = GeneratedDll.Create(definition, (await RunApp(definition)).Columns);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await dll.RunAsync(definition.CsvFile.Path, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(0, result.Counts.Read);
    }

    [Fact]
    public async Task Progress_IsReportedAtLeastOnce()
    {
        var definition = PipelineDefinitionTests.C01Definition();
        using var dll = GeneratedDll.Create(definition, (await RunApp(definition)).Columns);

        var result = await dll.RunAsync(definition.CsvFile.Path);

        Assert.True(result.ProgressReports >= 1);
        Assert.Equal(new PipelineCounts(4, 4, 0, 0, 24), result.Counts);
    }

    [Fact]
    public async Task MissingFile_GivesAClearError_AndNoException()
    {
        var definition = PipelineDefinitionTests.C01Definition();
        using var dll = GeneratedDll.Create(definition, (await RunApp(definition)).Columns);

        var result = await dll.RunAsync(Path.Combine(Path.GetTempPath(), $"taskE_missing_{Guid.NewGuid():N}.csv"));

        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task DefinitionJson_RoundTrips_AndOutputColumnsEqualTheAppColumns()
    {
        var definition = await PipelineRunnerTests.ManifestDefinitionAsync("C12");
        var app = await RunApp(definition);
        using var dll = GeneratedDll.Create(definition, app.Columns);

        var json = (string)dll.Entry.GetProperty("DefinitionJson")!.GetValue(null)!;
        var columns = (IReadOnlyList<string>)dll.Entry.GetProperty("OutputColumns")!.GetValue(null)!;

        Assert.Equal(definition, SettingsJson.DeserializePipelineDefinition(json));
        Assert.Equal(app.Columns, columns);
    }

    [Fact]
    public async Task QuoteAndBackslashInSettings_CompileAndRun()
    {
        var folder = Directory.CreateTempSubdirectory("taskE_quote_").FullName;
        var path = Path.Combine(folder, "quote.csv");
        await File.WriteAllLinesAsync(path, ["sample,r01,r02,r03,\"we\"\"ird\\col\"", "S1:a|b,1,2,3,x\"y\\z", "S2:c,4,5,6,\\"]);
        try
        {
            var definition = new PipelineDefinition(
                new CsvFileSettings(path, ",", true, 0, "utf-8"),
                new CsvColumnSelection(),
                new ParseValueSettings
                {
                    SourceColumn = "sample",
                    Slots = ParseValueSettings.SlotsFromGroups(["r01", "r02", "r03"], 3),
                    CarryColumns = CarryColumns.Of("we\"ird\\col")
                },
                OutputSettings.Default);
            var app = await RunApp(definition);
            Assert.Null(app.Error);
            Assert.Contains("we\"ird\\col", app.Columns);

            using var dll = GeneratedDll.Create(definition, app.Columns);
            var result = await dll.RunAsync(path);

            Assert.Null(result.Error);
            Assert.Equal(app.Columns, result.Columns);
            Assert.Equal(app.Counts, result.Counts);
            Assert.Equal(app.Rows.Select(r => r["we\"ird\\col"]), result.Rows.Select(r => r["we\"ird\\col"]));
            Assert.Equal(definition, SettingsJson.DeserializePipelineDefinition((string)dll.Entry.GetProperty("DefinitionJson")!.GetValue(null)!));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("a \"\"\" b \"\"\"\" c")]
    [InlineData("back\\slash \\\" end\"")]
    public void RawStringLiteral_KeepsTheTextExactly(string text)
    {
        var literal = CsvPipelineDllGenerator.RawStringLiteral(text);
        var tree = CSharpSyntaxTree.ParseText($"class C {{ const string S = {literal}; }}");
        var token = tree.GetRoot().DescendantTokens().Single(t => t.IsKind(SyntaxKind.MultiLineRawStringLiteralToken));

        Assert.Empty(tree.GetDiagnostics());
        Assert.Equal(text, token.ValueText);
    }

    [Fact]
    public async Task PublicSurface_IsTheEntryClassAndTheThreeResultTypes_WithoutEtlBoxTypes()
    {
        var definition = PipelineDefinitionTests.C01Definition();
        using var dll = GeneratedDll.Create(definition, (await RunApp(definition)).Columns, "MyPipeline", "Lab.Exports");

        var exported = dll.Assembly.GetExportedTypes();

        Assert.Equal(
            new[] { "Lab.Exports.MyPipeline", "Lab.Exports.PipelineRunProgress", "Lab.Exports.PipelineRunResult", "Lab.Exports.RejectInfo" },
            exported.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal));

        foreach (var type in exported)
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (var used in TypesIn(member))
                {
                    var assembly = used.Assembly.GetName().Name ?? string.Empty;
                    Assert.False(assembly.StartsWith("ETLBox", StringComparison.OrdinalIgnoreCase), $"{type.Name}.{member.Name} exposes {used}");
                    Assert.False(used.Assembly == dll.Assembly && !used.IsPublic && !used.IsNestedPublic, $"{type.Name}.{member.Name} exposes the internal {used}");
                }
            }
        }
    }

    [Fact]
    public void InvalidDefinition_IsRefused_WithTheValidatorMessages()
    {
        var definition = PipelineDefinitionTests.C01Definition() with
        {
            ParseValue = PipelineDefinitionTests.C01Definition().ParseValue with { SourceColumn = "" }
        };
        var folder = Path.Combine(Path.GetTempPath(), $"taskE_invalid_{Guid.NewGuid():N}");

        var result = CsvPipelineDllGenerator.Generate(new CsvPipelineDllRequest(definition, [], folder));

        Assert.False(result.Success);
        Assert.Equal(PipelineDefinitionValidator.Validate(definition), result.Messages);
        Assert.False(Directory.Exists(folder));
    }

    [Theory]
    [InlineData("CsvPipeline", true)]
    [InlineData("_Lab2", true)]
    [InlineData("2Lab", false)]
    [InlineData("class", false)]
    [InlineData("my pipeline", false)]
    [InlineData("SystemPipe", false)]
    [InlineData("RejectInfo", false)]
    [InlineData("", false)]
    public void CheckAssemblyName(string name, bool valid)
        => Assert.Equal(valid, CsvPipelineDllGenerator.CheckAssemblyName(name) is null);

    [Theory]
    [InlineData("Generated", true)]
    [InlineData("Lab.Exports", true)]
    [InlineData("Lab..Exports", false)]
    [InlineData("Lab.class", false)]
    [InlineData(" ", false)]
    public void CheckNamespace(string name, bool valid)
        => Assert.Equal(valid, CsvPipelineDllGenerator.CheckNamespace(name) is null);

    [Fact]
    public async Task Readme_HasThePackagesTheLicenceNoteAndTheSettings()
    {
        var definition = PipelineDefinitionTests.C01Definition();
        using var dll = GeneratedDll.Create(definition, (await RunApp(definition)).Columns);

        var readme = await File.ReadAllTextAsync(dll.Generation.ReadmePath!);

        Assert.Contains("ETLBox.Csv 3.11.1", readme);
        Assert.Contains("the free edition stops after 5,000 rows read; the consuming app needs its own valid ETLBox licence", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CsvPipeline.RunAsync(", readme);
        Assert.Contains(SettingsJson.Serialize(definition), readme);
    }

    internal static Task<PipelineResult> RunApp(PipelineDefinition definition)
        => TestTimeout.Run(() => PipelineRunner.RunAsync(definition, null, null, CancellationToken.None));

    private static IEnumerable<Type> TypesIn(MemberInfo member)
    {
        var direct = member switch
        {
            MethodInfo m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType),
            ConstructorInfo c => c.GetParameters().Select(p => p.ParameterType),
            PropertyInfo p => new[] { p.PropertyType },
            FieldInfo f => new[] { f.FieldType },
            _ => Array.Empty<Type>()
        };

        return direct.SelectMany(Expand);

        static IEnumerable<Type> Expand(Type t)
        {
            yield return t;
            var inner = t.HasElementType ? new[] { t.GetElementType()! } : t.IsGenericType ? t.GetGenericArguments() : Array.Empty<Type>();
            foreach (var i in inner.SelectMany(Expand))
            {
                yield return i;
            }
        }
    }
}

/// <summary>The result of a generated DLL's RunAsync, read by reflection and turned into the engine's shapes for comparison.</summary>
internal sealed record DllRunResult(
    List<Dictionary<string, string>> Rows,
    IReadOnlyList<string> Columns,
    List<RecordIssue> Rejects,
    List<RecordIssue> Flagged,
    PipelineCounts Counts,
    bool Cancelled,
    string? Error,
    bool LicenceLimitReached,
    int ProgressReports);

/// <summary>An IProgress for a type known only at run time.</summary>
internal sealed class CallbackProgress<T> : IProgress<T>
{
    private readonly Action<object?> _callback;

    public CallbackProgress(Action<object?> callback) => _callback = callback;

    public void Report(T value) => _callback(value);
}

/// <summary>A generated DLL in a temp folder, loaded into a collectible context. Dispose unloads it and deletes the folder.</summary>
internal sealed class GeneratedDll : IDisposable
{
    private readonly AssemblyLoadContext _context;

    private GeneratedDll(CsvPipelineDllResult generation, string folder, string className, string ns)
    {
        Generation = generation;
        Folder = folder;
        _context = new AssemblyLoadContext($"taskE-{Guid.NewGuid():N}", isCollectible: true);
        Assembly = _context.LoadFromStream(new MemoryStream(File.ReadAllBytes(generation.DllPath!)));
        Entry = Assembly.GetType($"{ns}.{className}", throwOnError: true)!;
    }

    public CsvPipelineDllResult Generation { get; }

    public string Folder { get; }

    public Assembly Assembly { get; }

    public Type Entry { get; }

    public static GeneratedDll Create(PipelineDefinition definition, IReadOnlyList<string> columns,
        string className = CsvPipelineDllGenerator.DefaultClassName, string ns = CsvPipelineDllGenerator.DefaultNamespace)
    {
        var folder = Path.Combine(Path.GetTempPath(), "taskE_dll", Guid.NewGuid().ToString("N"));
        var result = CsvPipelineDllGenerator.Generate(new CsvPipelineDllRequest(definition, columns, folder, className, ns));
        Assert.True(result.Success, string.Join(Environment.NewLine,
            result.Messages.Concat(result.CompileErrors.Select(e => $"{e.File}({e.Line}): {e.Message}"))));
        Assert.True(File.Exists(result.DllPath));
        Assert.True(File.Exists(result.ReadmePath));
        return new GeneratedDll(result, folder, className, ns);
    }

    public async Task<DllRunResult> RunAsync(string path, CancellationToken token = default)
    {
        var reports = 0;
        var progressType = Assembly.GetType(Entry.Namespace + ".PipelineRunProgress", throwOnError: true)!;
        var progress = Activator.CreateInstance(typeof(CallbackProgress<>).MakeGenericType(progressType),
            new Action<object?>(_ => Interlocked.Increment(ref reports)));

        var result = await TestTimeout.Run(async () =>
        {
            var task = (Task)Entry.GetMethod("RunAsync")!.Invoke(null, [path, progress, token])!;
            await task;
            return task.GetType().GetProperty("Result")!.GetValue(task)!;
        });

        object? Get(string name) => result.GetType().GetProperty(name)!.GetValue(result);
        List<RecordIssue> Issues(string name) => ((System.Collections.IEnumerable)Get(name)!).Cast<object>()
            .Select(i => new RecordIssue((int)i.GetType().GetProperty("RecordNumber")!.GetValue(i)!, (string)i.GetType().GetProperty("Reason")!.GetValue(i)!))
            .ToList();
        var counts = ((int Read, int Parsed, int Rejected, int Flagged, int Out))Get("Counts")!;

        return new DllRunResult(
            (List<Dictionary<string, string>>)Get("Rows")!,
            (IReadOnlyList<string>)Get("Columns")!,
            Issues("Rejects"),
            Issues("Flagged"),
            new PipelineCounts(counts.Read, counts.Parsed, counts.Rejected, counts.Flagged, counts.Out),
            (bool)Get("Cancelled")!,
            (string?)Get("Error"),
            (bool)Get("LicenceLimitReached")!,
            reports);
    }

    public void Dispose()
    {
        _context.Unload();
        try
        {
            Directory.Delete(Folder, true);
        }
        catch (IOException)
        {
            // A temp folder left behind does no harm.
        }
    }
}
