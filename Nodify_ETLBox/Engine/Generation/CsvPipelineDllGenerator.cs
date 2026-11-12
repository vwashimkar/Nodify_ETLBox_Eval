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

    /// <summary>
    /// The extra Engine files of a DLL whose pipeline reads one CSV part of a larger file (task G6b): the part
    /// halves of the reader, definition and runner, and the intake code that finds the part. Only such a DLL
    /// compiles them, so a CsvFile DLL is built from exactly the files above. Each one is an EmbeddedResource.
    /// </summary>
    public static readonly IReadOnlyList<string> CsvPartSourceFiles = new[]
    {
        "Engine/Csv/CsvRecordReader.Part.cs",
        "Engine/Pipeline/PipelineDefinition.Part.cs",
        "Engine/Pipeline/PipelineRunner.Part.cs",
        "Engine/Pipeline/ColumnSplit.cs",
        "Engine/Intake/AutoDetector.cs",
        "Engine/Intake/CsvPartLocator.cs",
        "Engine/Intake/CsvPartSelector.cs",
        "Engine/Intake/IntakeContracts.cs",
        "Engine/Intake/IntakeEdit.cs",
        "Engine/Intake/IntakeEditSession.cs",
        "Engine/Intake/IntakeEditSession.Templates.cs",
        "Engine/Intake/IntakeEncoding.cs",
        "Engine/Intake/IntakeModel.cs",
        "Engine/Intake/IntakePlan.cs",
        "Engine/Intake/IntakePlanParts.cs",
        "Engine/Intake/IntakeReader.cs",
        "Engine/Intake/IntakeScanService.cs",
        "Engine/Intake/IntakeSources.cs",
        "Engine/Intake/IntakeTemplate.cs",
        "Engine/Intake/IntakeText.cs",
        "Engine/Intake/IntakeValidation.cs",
        "Engine/Intake/IntakeValidator.cs",
        "Engine/Intake/LineIndexer.cs",
        "Engine/Intake/MarkerScanner.cs",
        "Engine/Intake/PartStream.cs",
        "Engine/Intake/RowShape.cs",
        "Engine/Intake/StructureTrackers.cs",
        "Engine/Intake/TemplateStore.cs",
        "Engine/Intake/TextFileLineageLog.cs",
        "Engine/Intake/YamlCheck.cs",
        "Engine/Intake/YamlShape.cs"
    };

    /// <summary>The Engine files compiled into the DLL of <paramref name="definition"/>.</summary>
    public static IReadOnlyList<string> SourceFilesFor(PipelineDefinition definition)
        => definition.CsvPart is null ? SourceFiles : SourceFiles.Concat(CsvPartSourceFiles).ToList();

    /// <summary>What a CSV part DLL returns when YamlDotNet.dll cannot be loaded (the part search needs it).</summary>
    public const string MissingYamlMessage =
        "YamlDotNet.dll was not found next to the pipeline DLL; copy YamlDotNet.dll (version 18.1.0) into the app folder and run again.";

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
            foreach (var rel in SourceFilesFor(request.Definition))
            {
                sources.Add((Path.GetFileName(rel), MakeTypesInternal(ReplaceGeneratedRegex(DllGenerator.ReadSourceContent(rel)))));
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
    /// <c>[GeneratedRegex(pattern[, options])] partial Regex Name();</c> needs the regex source generator, which this
    /// compile does not run (task G6b, YamlShape.cs). Each such method becomes a cached <c>new Regex(pattern[, options])</c>
    /// with the same arguments; .NET documents both as the same regex semantics [D]. Other forms are left as they are,
    /// so the compiler names them.
    /// </summary>
    internal static string ReplaceGeneratedRegex(string source)
    {
        if (!source.Contains("GeneratedRegex", StringComparison.Ordinal))
        {
            return source;
        }

        var root = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)).GetRoot();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Select(m => (Method: m, Attribute: m.AttributeLists.SelectMany(a => a.Attributes)
                .FirstOrDefault(a => a.Name.ToString() is "GeneratedRegex" or "GeneratedRegexAttribute")))
            .Where(x => x.Attribute is { ArgumentList.Arguments.Count: 1 or 2 } && x.Method.Body is null && x.Method.ExpressionBody is null)
            .OrderByDescending(x => x.Method.SpanStart)
            .ToList();

        var text = new StringBuilder(source);
        foreach (var (method, attribute) in methods)
        {
            var name = method.Identifier.Text;
            var modifiers = string.Join(" ", method.Modifiers.Where(t => !t.IsKind(SyntaxKind.PartialKeyword)).Select(t => t.Text));
            var replacement = $"private static global::System.Text.RegularExpressions.Regex? __{name};\n\n    "
                              + $"{modifiers} global::System.Text.RegularExpressions.Regex {name}() => __{name} ??= new global::System.Text.RegularExpressions.Regex{attribute!.ArgumentList};";
            text.Remove(method.Span.Start, method.Span.Length).Insert(method.Span.Start, replacement);
        }

        return text.ToString();
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

    /// <summary>The additive result fields of a CSV part DLL: what the run found (the file name, never its path).</summary>
    private const string PartResultFields = """
,
    string? FileName = null,
    string? Sha256 = null,
    Guid? TemplateId = null,
    string? TemplateName = null,
    double? Similarity = null,
    string? Match = null,
    int? PartNumber = null,
    long? FirstLine = null,
    long? LastLine = null,
    IReadOnlyList<string>? PartColumns = null
""";

    /// <summary>Map of a CSV part DLL: the CsvFile mapping plus the facts. ENGINE is replaced by the engine namespace.</summary>
    private const string PartMap = """
MapCore(r) with
    {
        FileName = r.CsvPart?.FileName,
        Sha256 = r.CsvPart?.Sha256,
        TemplateId = r.CsvPart?.TemplateId,
        TemplateName = r.CsvPart?.TemplateName,
        Similarity = r.CsvPart?.Similarity,
        Match = r.CsvPart?.Match,
        PartNumber = r.CsvPart?.PartNumber,
        FirstLine = r.CsvPart?.FirstLine,
        LastLine = r.CsvPart?.LastLine,
        PartColumns = r.CsvPart?.ColumnNames.ToArray()
    };

    private static PipelineRunResult MapCore(ENGINE.PipelineResult r) => new(
""";

    /// <summary>The YamlDotNet check of a CSV part DLL: a missing YamlDotNet.dll gives one sentence instead of a load error.</summary>
    private const string PartMembers = """

    private static string? MissingYaml()
    {
        try
        {
            System.Reflection.Assembly.Load(new System.Reflection.AssemblyName("YamlDotNet"));
            return null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return "YamlDotNet.dll was not found next to the pipeline DLL; copy YamlDotNet.dll (version 18.1.0) into the app folder and run again.";
        }
    }

""";

    private static string BuildEntrySource(CsvPipelineDllRequest request, string definitionJson)
    {
        const string engine = "global::EtlDesigner.Engine.Pipeline";
        var columns = string.Join(", ", request.OutputColumns.Select(c => SymbolDisplay.FormatLiteral(c, quote: true)));
        var cls = request.AssemblyName;
        var isPart = request.Definition.CsvPart is not null;
        var header = isPart
            ? $"// Generated by Nodify_ETLBox_Eval: {PartChain(request.Definition)}. It finds one CSV part of a larger\n"
              + "// file by its structure and reads only that part. Needs ETLBox, ETLBox.Csv, ETLBox.Json 3.11.1 and YamlDotNet 18.1.0"
            : "// Generated by Nodify_ETLBox_Eval: CsvFile -> ParseValue -> DotNetObject.\n// Needs the ETLBox and ETLBox.Csv 3.11.1 packages";
        var partFields = isPart ? PartResultFields : string.Empty;
        var yamlCheck = isPart ? "\n        if (MissingYaml() is { } missing)\n        {\n            return Failed(missing);\n        }\n" : string.Empty;
        var partMembers = isPart ? PartMembers : string.Empty;
        var map = isPart ? PartMap.Replace("ENGINE", engine) : "new(";

        return $$"""
{{header}} at run time. The consuming app does its own
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
    long ElapsedMs{{partFields}});

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
{{yamlCheck}}
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

    private static PipelineRunResult Map({{engine}}.PipelineResult r) => {{map}}
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
{{partMembers}}}

""";
    }

    private static string BuildReadme(CsvPipelineDllRequest request, string definitionJson, DateTime generatedAt)
    {
        var text = new StringBuilder();
        if (request.Definition.CsvPart is not null)
        {
            AppendPartReadme(text, request, definitionJson, generatedAt);
            return text.ToString();
        }

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

    /// <summary>The steps of a CSV part DLL as text; a guided pipeline (task G7a) has no ParseValue.</summary>
    private static string PartChain(PipelineDefinition definition)
        => definition.Guided is null ? "CSV part -> ParseValue -> DotNetObject" : "CSV part -> DotNetObject";

    /// <summary>The README of a CSV part DLL: what it reads, what to deploy, and each failure in one line.</summary>
    private static void AppendPartReadme(StringBuilder text, CsvPipelineDllRequest request, string definitionJson, DateTime generatedAt)
    {
        var part = request.Definition.CsvPart!;
        text.AppendLine($"{request.AssemblyName}.dll: {PartChain(request.Definition)}");
        text.AppendLine($"Generated: {generatedAt:yyyy-MM-dd HH:mm}");
        text.AppendLine();
        text.AppendLine("What it does:");
        text.AppendLine($"  It reads one CSV part of a larger file: the part '{part.PartName}' of the example layout. It scans the file,");
        text.AppendLine("  finds the part by the file's structure (the saved layout), checks it, and reads only that part.");
        text.AppendLine("  It writes no file: no lineage log and no templates. The facts of the run come back in the result");
        text.AppendLine("  (FileName, Sha256, TemplateId, TemplateName, Similarity, Match, PartNumber, FirstLine, LastLine, PartColumns).");
        text.AppendLine();
        text.AppendLine("Put these next to the DLL (or reference the NuGet packages in the consuming app):");
        text.AppendLine("  ETLBox.dll, ETLBox.Csv.dll, ETLBox.Json.dll (3.11.1), CsvHelper.dll (as ETLBox.Csv needs it), YamlDotNet.dll (18.1.0).");
        text.AppendLine("  Generate DLL does not copy them. Without YamlDotNet.dll, RunAsync returns this Error:");
        text.AppendLine($"  {MissingYamlMessage}");
        text.AppendLine("Target framework: net10.0.");
        text.AppendLine();
        text.AppendLine("Licence:");
        text.AppendLine("  The free edition stops after 5,000 rows read; the consuming app needs its own valid ETLBox licence.");
        text.AppendLine("  This DLL contains no licence key and never sets one.");
        text.AppendLine();
        text.AppendLine("When the part is not found, RunAsync returns no rows and one sentence in Error:");
        text.AppendLine("  The file is missing or open in another program.");
        text.AppendLine("  The file does not look like the example (the similarity is given).");
        text.AppendLine("  The part was not found in the file.");
        text.AppendLine("  The file has several parts like it; the pipeline reads one.");
        text.AppendLine("  The part is not a table, or it has another delimiter.");
        text.AppendLine("  The part has another number of columns, or other column names.");
        text.AppendLine("  On the example file itself: the part is no longer where it was chosen.");
        text.AppendLine();
        text.AppendLine("Usage:");
        text.AppendLine();
        text.Append(UsageExample(request.AssemblyName, request.Namespace));
        text.AppendLine();
        text.AppendLine($"Output columns: {string.Join(", ", request.OutputColumns)}");
        text.AppendLine();
        text.AppendLine("Baked settings (csvFile.path is the example file; csvPart is the saved layout and the expected columns; RunAsync takes the file to run):");
        text.AppendLine(definitionJson);
    }
}
