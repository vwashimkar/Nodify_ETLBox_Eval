using System.IO;
using System.Text;
using EtlDesigner.Engine.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EtlDesigner.Engine.Generation;

/// <summary>What to generate. <paramref name="OutputColumns"/> are the columns of the tested Execute run.</summary>
public sealed record CsvPipelineDllRequest(
    PipelineDefinition Definition,
    IReadOnlyList<string> OutputColumns,
    string OutputFolder,
    string AssemblyName = CsvPipelineDllGenerator.DefaultClassName,
    string Namespace = CsvPipelineDllGenerator.DefaultNamespace);

/// <summary>One compiler error, with the line in the generated file it points at.</summary>
public sealed record CompileError(string File, int Line, string Message);

/// <summary>
/// The outcome of a generation. On success <see cref="DllPath"/> and <see cref="ReadmePath"/> are set.
/// Otherwise <see cref="Messages"/> says why (invalid definition, names, folder) and
/// <see cref="CompileErrors"/> lists what the compiler refused.
/// </summary>
public sealed record CsvPipelineDllResult(
    bool Success,
    string? DllPath,
    string? ReadmePath,
    IReadOnlyList<string> Messages,
    IReadOnlyList<CompileError> CompileErrors);

/// <summary>
/// Generates a DLL for CsvFile -> ParseValue -> DotNetObject. The Engine files below are compiled
/// unchanged except that their top-level types become internal; only a small generated wrapper is
/// public. The settings are baked as JSON. The DLL never contains or sets a licence key.
/// Compiling goes through the legacy generator's <see cref="DllGenerator.Compile"/>.
/// </summary>
public static class CsvPipelineDllGenerator
{
    public const string DefaultClassName = "CsvPipeline";
    public const string DefaultNamespace = "Generated";
    public const string ReadmeFileName = "README_usage.txt";

    /// <summary>Every Engine file a generated pipeline needs. Each one is an EmbeddedResource in the project file.</summary>
    public static readonly IReadOnlyList<string> SourceFiles = new[]
    {
        "Engine/Csv/CsvFileSettings.cs",
        "Engine/Csv/CsvColumnSelection.cs",
        "Engine/Csv/CsvErrorText.cs",
        "Engine/Csv/CsvRecordReader.cs",
        "Engine/Csv/CsvFileValidator.cs",
        "Engine/Parsing/ParseValueSettings.cs",
        "Engine/Parsing/CompositeCellParser.cs",
        "Engine/Parsing/ParseValueTransform.cs",
        "Engine/Parsing/ParseValueValidator.cs",
        "Engine/Pipeline/SettingsJson.cs",
        "Engine/Pipeline/OutputSettings.cs",
        "Engine/Pipeline/PipelineDefinition.cs",
        "Engine/Pipeline/PipelineRunner.cs"
    };

    /// <summary>The public types the generated wrapper defines; the entry class may not use these names.</summary>
    public static readonly IReadOnlyList<string> PublicTypeNames = new[] { "PipelineRunResult", "RejectInfo", "PipelineRunProgress" };

    /// <summary>The implicit usings of the app project, without WPF. Fixed, so generation does not depend on obj/.</summary>
    private const string GlobalUsings =
        "global using System;\n" +
        "global using System.Collections.Generic;\n" +
        "global using System.IO;\n" +
        "global using System.Linq;\n" +
        "global using System.Net.Http;\n" +
        "global using System.Threading;\n" +
        "global using System.Threading.Tasks;\n";

    /// <summary>A message when <paramref name="name"/> cannot be the assembly and entry class name; null when it can.</summary>
    public static string? CheckAssemblyName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter an assembly name, for example CsvPipeline.";
        }

        if (!IsIdentifier(name))
        {
            return $"\"{name}\" is not a valid C# name. Use letters, digits and _ only, start with a letter, and do not use a C# keyword.";
        }

        if (name.StartsWith("System", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ETLBox", StringComparison.OrdinalIgnoreCase) || name.Equals("CsvHelper", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{name}\" is reserved. Names starting with System or Microsoft, and ETLBox or CsvHelper, clash with other libraries. Choose another name.";
        }

        if (PublicTypeNames.Contains(name, StringComparer.Ordinal))
        {
            return $"\"{name}\" is the name of a result type in the DLL. Choose another name.";
        }

        return null;
    }

    /// <summary>A message when <paramref name="name"/> cannot be the namespace; null when it can. Dotted names are allowed.</summary>
    public static string? CheckNamespace(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a namespace, for example Generated.";
        }

        return name.Split('.').All(IsIdentifier)
            ? null
            : $"\"{name}\" is not a valid C# namespace. Use names made of letters, digits and _, separated by dots, and no C# keyword.";
    }

    public static CsvPipelineDllResult Generate(CsvPipelineDllRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var messages = new List<string>();
        messages.AddRange(PipelineDefinitionValidator.Validate(request.Definition));
        if (CheckAssemblyName(request.AssemblyName) is { } nameError)
        {
            messages.Add(nameError);
        }

        if (CheckNamespace(request.Namespace) is { } namespaceError)
        {
            messages.Add(namespaceError);
        }

        if (string.IsNullOrWhiteSpace(request.OutputFolder))
        {
            messages.Add("Choose an output folder.");
        }

        if (messages.Count > 0)
        {
            return Failed(messages);
        }

        try
        {
            Directory.CreateDirectory(request.OutputFolder);

            var definitionJson = SettingsJson.Serialize(request.Definition);
            var sources = new List<(string FileName, string Content)>();
            foreach (var rel in SourceFiles)
            {
                sources.Add((Path.GetFileName(rel), MakeTypesInternal(DllGenerator.ReadSourceContent(rel))));
            }

            sources.Add(("Generated.GlobalUsings.g.cs", GlobalUsings));
            sources.Add(($"{request.AssemblyName}.cs", BuildEntrySource(request, definitionJson)));

            var (compiled, diagnostics) = DllGenerator.CompileWithDiagnostics(sources, request.OutputFolder, request.AssemblyName);
            if (!compiled.Success || compiled.DllPath is null)
            {
                var errors = diagnostics
                    .Select(d => new CompileError(
                        d.Location.GetLineSpan().Path,
                        d.Location.GetLineSpan().StartLinePosition.Line + 1,
                        $"{d.Id}: {d.GetMessage()}"))
                    .ToList();
                var failure = compiled.ErrorMessage ?? "The DLL could not be compiled.";
                return new CsvPipelineDllResult(false, null, null, new[] { failure }, errors);
            }

            var readmePath = Path.Combine(request.OutputFolder, ReadmeFileName);
            File.WriteAllText(readmePath, BuildReadme(request, definitionJson, DateTime.Now), new UTF8Encoding(false));
            return new CsvPipelineDllResult(true, compiled.DllPath, readmePath, Array.Empty<string>(), Array.Empty<CompileError>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(new[] { $"Could not write to the output folder. Choose another folder, or close the program that uses the DLL. Details: {ex.Message}" });
        }
    }

    /// <summary>A ten-line example for the consuming app: RunAsync with progress and a cancel token.</summary>
    public static string UsageExample(string assemblyName = DefaultClassName, string ns = DefaultNamespace) =>
        $"using {ns};\n" +
        "\n" +
        "using var cts = new CancellationTokenSource();   // call cts.Cancel() to stop the run\n" +
        "var progress = new Progress<PipelineRunProgress>(p => Console.WriteLine($\"Read {p.Counts.Read} rows\"));\n" +
        $"var result = await {assemblyName}.RunAsync(@\"C:\\data\\export.csv\", progress, cts.Token);\n" +
        "if (result.Error is not null) Console.WriteLine(result.Error);\n" +
        "Console.WriteLine($\"Read {result.Counts.Read}, out {result.Counts.Out}, rejected {result.Counts.Rejected}\");\n" +
        "foreach (var row in result.Rows.Take(5))\n" +
        $"    Console.WriteLine(string.Join(\", \", {assemblyName}.OutputColumns.Select(c => row[c])));\n" +
        "foreach (var reject in result.Rejects) Console.WriteLine($\"Record {reject.RecordNumber}: {reject.Reason}\");\n";

    private static CsvPipelineDllResult Failed(IReadOnlyList<string> messages)
        => new(false, null, null, messages, Array.Empty<CompileError>());

    private static bool IsIdentifier(string name)
        => SyntaxFacts.IsValidIdentifier(name)
           && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None
           && SyntaxFacts.GetContextualKeywordKind(name) == SyntaxKind.None;

    /// <summary>Top-level <c>public</c> types become <c>internal</c>, so the engine does not show in the DLL's public surface.</summary>
    internal static string MakeTypesInternal(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)).GetRoot();
        var topLevel = root.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Where(m => m is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
            .Where(m => m.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax);

        return root.ReplaceNodes(topLevel, (_, node) =>
        {
            var publicToken = node.Modifiers.FirstOrDefault(t => t.IsKind(SyntaxKind.PublicKeyword));
            if (publicToken == default)
            {
                return node;
            }

            var internalToken = SyntaxFactory.Token(publicToken.LeadingTrivia, SyntaxKind.InternalKeyword, publicToken.TrailingTrivia);
            return node.WithModifiers(node.Modifiers.Replace(publicToken, internalToken));
        }).ToFullString();
    }

    /// <summary>
    /// A raw string literal that cannot be broken by the text: the quote run is longer than any run
    /// of quotes inside, and the text sits on its own lines.
    /// </summary>
    internal static string RawStringLiteral(string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var ch in text)
        {
            run = ch == '"' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var quotes = new string('"', Math.Max(3, longest + 1));
        return $"{quotes}\n{text.ReplaceLineEndings("\n")}\n{quotes}";
    }

    private static string BuildEntrySource(CsvPipelineDllRequest request, string definitionJson)
    {
        const string engine = "global::EtlDesigner.Engine.Pipeline";
        var columns = string.Join(", ", request.OutputColumns.Select(c => SymbolDisplay.FormatLiteral(c, quote: true)));
        var cls = request.AssemblyName;

        return $$"""
// Generated by Nodify_ETLBox_Eval: CsvFile -> ParseValue -> DotNetObject.
// Needs the ETLBox and ETLBox.Csv 3.11.1 packages at run time. The consuming app does its own
// ETLBox licensing; this DLL contains no licence key and never sets one.
#nullable enable
namespace {{request.Namespace}};

/// <summary>Counts of a run, as a tuple: Read, Parsed, Rejected, Flagged, Out.</summary>
public sealed record PipelineRunProgress((int Read, int Parsed, int Rejected, int Flagged, int Out) Counts, long ElapsedMs);

/// <summary>A rejected or flagged record: its number in the file and the reason.</summary>
public sealed record RejectInfo(int RecordNumber, string Reason);

/// <summary>The result of <see cref="{{cls}}.RunAsync"/>. Error is null when the run ended normally.</summary>
public sealed record PipelineRunResult(
    List<Dictionary<string, string>> Rows,
    IReadOnlyList<string> Columns,
    IReadOnlyList<RejectInfo> Rejects,
    IReadOnlyList<RejectInfo> Flagged,
    (int Read, int Parsed, int Rejected, int Flagged, int Out) Counts,
    bool Cancelled,
    string? Error,
    bool LicenceLimitReached,
    long ElapsedMs);

/// <summary>The pipeline with its settings baked in.</summary>
public static class {{cls}}
{
    private const string BakedJson = {{RawStringLiteral(definitionJson)}};

    private static readonly {{engine}}.PipelineDefinition Definition = {{engine}}.SettingsJson.DeserializePipelineDefinition(BakedJson);

    /// <summary>The baked settings as JSON.</summary>
    public static string DefinitionJson => BakedJson;

    /// <summary>The output column names, in order, as seen in the tested run.</summary>
    public static IReadOnlyList<string> OutputColumns { get; } = new string[] { {{columns}} };

    /// <summary>Runs the pipeline over <paramref name="filePath"/>. Problems come back in Error; this method does not throw for them.</summary>
    public static async Task<PipelineRunResult> RunAsync(string filePath, IProgress<PipelineRunProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Failed("No file was given. Pass the path of the CSV file to run.");
        }

        try
        {
            var relay = progress is null ? null : new ProgressRelay(progress);
            var result = await {{engine}}.PipelineRunner.RunAsync(Definition, filePath, relay, cancellationToken).ConfigureAwait(false);
            return Map(result);
        }
        catch (Exception ex)
        {
            return Failed($"The run stopped with an unexpected error. Check the file and run again. Details: {ex.Message}");
        }
    }

    private static PipelineRunResult Failed(string error)
        => new(new List<Dictionary<string, string>>(), Array.Empty<string>(), Array.Empty<RejectInfo>(), Array.Empty<RejectInfo>(), (0, 0, 0, 0, 0), false, error, false, 0);

    private static (int, int, int, int, int) Counts({{engine}}.PipelineCounts c) => (c.Read, c.Parsed, c.Rejected, c.Flagged, c.Out);

    private static PipelineRunResult Map({{engine}}.PipelineResult r) => new(
        r.Rows,
        r.Columns.ToArray(),
        r.Rejects.Select(i => new RejectInfo(i.RecordNumber, i.Reason)).ToArray(),
        r.Flagged.Select(i => new RejectInfo(i.RecordNumber, i.Reason)).ToArray(),
        Counts(r.Counts),
        r.Cancelled,
        r.Error,
        r.LicenceLimitReached,
        r.ElapsedMs);

    private sealed class ProgressRelay : IProgress<{{engine}}.PipelineProgress>
    {
        private readonly IProgress<PipelineRunProgress> _target;

        public ProgressRelay(IProgress<PipelineRunProgress> target) => _target = target;

        public void Report({{engine}}.PipelineProgress value) => _target.Report(new PipelineRunProgress(Counts(value.Counts), value.ElapsedMs));
    }
}

""";
    }

    private static string BuildReadme(CsvPipelineDllRequest request, string definitionJson, DateTime generatedAt)
    {
        var text = new StringBuilder();
        text.AppendLine($"{request.AssemblyName}.dll: CsvFile -> ParseValue -> DotNetObject");
        text.AppendLine($"Generated: {generatedAt:yyyy-MM-dd HH:mm}");
        text.AppendLine();
        text.AppendLine("Packages the consuming app needs (NuGet):");
        text.AppendLine("  ETLBox 3.11.1");
        text.AppendLine("  ETLBox.Csv 3.11.1 (NuGet adds what it depends on, such as CsvHelper)");
        text.AppendLine("Target framework: net10.0.");
        text.AppendLine();
        text.AppendLine("Licence:");
        text.AppendLine("  The free edition stops after 5,000 rows read; the consuming app needs its own valid ETLBox licence.");
        text.AppendLine("  This DLL contains no licence key and never sets one.");
        text.AppendLine();
        text.AppendLine("Usage:");
        text.AppendLine();
        text.Append(UsageExample(request.AssemblyName, request.Namespace));
        text.AppendLine();
        text.AppendLine($"Output columns: {string.Join(", ", request.OutputColumns)}");
        text.AppendLine();
        text.AppendLine("Baked settings (the path is the file used at design time; RunAsync takes the file to run):");
        text.AppendLine(definitionJson);
        return text.ToString();
    }
}
