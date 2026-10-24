using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EtlDesigner.Engine.Generation;

public static class DllGenerator
{
    private static readonly string[] SourceFiles =
    {
        "Engine/PipelineCore.cs",
        "Engine/PipelineSettings.cs",
        "Engine/PipelineExecutionStats.cs",
        "Engine/PipelineFailureNode.cs",
        "Engine/CountryListResult.cs"
    };

    public static DllGenerationResult Generate(PipelineSettings settings, string outputFolder, string assemblyName)
    {
        var outputSourcePaths = new List<string>();
        var diagnostics = new List<string>();

        try
        {
            if (string.IsNullOrWhiteSpace(outputFolder))
                return new DllGenerationResult(false, null, Array.Empty<string>(), Array.Empty<string>(), "Output folder is required.");
            if (string.IsNullOrWhiteSpace(assemblyName))
                return new DllGenerationResult(false, null, Array.Empty<string>(), Array.Empty<string>(), "Assembly name is required.");

            Directory.CreateDirectory(outputFolder);

            var generatedSources = new List<(string FileName, string Content)>();

            foreach (var rel in SourceFiles)
            {
                var content = ReadSourceContent(rel);
                const string sourceNamespace = "namespace EtlDesigner.Engine;";
                var count = CountOccurrences(content, sourceNamespace);
                if (count != 1)
                    return new DllGenerationResult(false, null, Array.Empty<string>(), Array.Empty<string>(), $"Expected exactly one namespace line in {rel}, found {count}.");

                var rewritten = content.Replace(sourceNamespace, "namespace EtlDesigner.Generated;");
                generatedSources.Add((Path.GetFileName(rel), rewritten));
            }

            var globalUsingsPath = FindGlobalUsingsFile();
            if (string.IsNullOrWhiteSpace(globalUsingsPath))
                return new DllGenerationResult(false, null, Array.Empty<string>(), Array.Empty<string>(), "Project global usings file not found under obj.");

            generatedSources.Add(("Generated.GlobalUsings.g.cs", BuildGlobalUsingsSource(globalUsingsPath)));
            generatedSources.Add(("GeneratedPipeline.cs", BuildGeneratedPipelineSource(settings)));

            foreach (var source in generatedSources)
            {
                var outPath = Path.Combine(outputFolder, source.FileName);
                File.WriteAllText(outPath, source.Content);
                outputSourcePaths.Add(outPath);
            }

            var syntaxTrees = generatedSources
                .Select(s => CSharpSyntaxTree.ParseText(s.Content, path: s.FileName, options: new CSharpParseOptions(LanguageVersion.Latest)))
                .ToList();

            var compilation = CSharpCompilation.Create(
                assemblyName,
                syntaxTrees,
                BuildReferences(),
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable,
                    deterministic: true));

            var tempDllPath = Path.Combine(outputFolder, $"{assemblyName}.{Guid.NewGuid():N}.tmp.dll");
            var finalDllPath = Path.Combine(outputFolder, $"{assemblyName}.dll");

            using (var fs = new FileStream(tempDllPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var emitResult = compilation.Emit(fs);
                diagnostics.AddRange(emitResult.Diagnostics
                    .Where(d => d.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
                    .Select(d => d.ToString()));

                var hasErrors = emitResult.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
                if (hasErrors)
                {
                    fs.Close();
                    if (File.Exists(tempDllPath))
                        File.Delete(tempDllPath);
                    return new DllGenerationResult(false, null, outputSourcePaths, diagnostics, "Compilation failed.");
                }
            }

            if (File.Exists(finalDllPath))
                File.Delete(finalDllPath);
            File.Move(tempDllPath, finalDllPath);

            var forbiddenReference = GetForbiddenReference(finalDllPath);
            if (forbiddenReference != null)
            {
                diagnostics.Add($"Forbidden framework implementation reference detected: {forbiddenReference}");
                if (File.Exists(finalDllPath))
                    File.Delete(finalDllPath);
                return new DllGenerationResult(false, null, outputSourcePaths, diagnostics, "Generated assembly has forbidden references.");
            }

            return new DllGenerationResult(true, finalDllPath, outputSourcePaths, diagnostics, null);
        }
        catch (Exception ex)
        {
            diagnostics.Add(ex.ToString());
            return new DllGenerationResult(false, null, outputSourcePaths, diagnostics, ex.Message);
        }
    }

    private static string ReadSourceContent(string relativePath)
    {
        var logicalName = relativePath.Replace('/', '.').Replace('\\', '.');
        var embeddedName = $"EtlDesigner.{logicalName}";

        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(embeddedName);
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        var sourcePath = Path.Combine(AppContext.BaseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath))
        {
            sourcePath = Path.Combine(Directory.GetCurrentDirectory(), "Nodify_ETLBox", relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Source file not found: {relativePath}");

        return File.ReadAllText(sourcePath);
    }

    private static string? FindGlobalUsingsFile()
    {
        var candidates = new List<string>();
        var cwd = Directory.GetCurrentDirectory();
        var baseDir = AppContext.BaseDirectory;

        candidates.Add(Path.Combine(cwd, "Nodify_ETLBox", "obj"));
        candidates.Add(Path.Combine(cwd, "obj"));
        candidates.Add(Path.Combine(baseDir, "obj"));

        var baseInfo = new DirectoryInfo(baseDir);
        for (var i = 0; i < 5 && baseInfo != null; i++)
        {
            candidates.Add(Path.Combine(baseInfo.FullName, "obj"));
            baseInfo = baseInfo.Parent;
        }

        var files = candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.GetFiles(dir, "*.GlobalUsings.g.cs", SearchOption.AllDirectories))
            .Where(path => path.Contains("Nodify_ETLBox", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return files.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private static IEnumerable<MetadataReference> BuildReferences()
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var frameworkRef in GetNet10ReferenceAssemblies())
        {
            if (seen.Add(frameworkRef))
            {
                refs.Add(MetadataReference.CreateFromFile(frameworkRef));
            }
        }

        foreach (var dependencyRef in GetNonFrameworkDependencyAssemblies())
        {
            if (seen.Add(dependencyRef))
            {
                refs.Add(MetadataReference.CreateFromFile(dependencyRef));
            }
        }

        return refs;
    }

    private static IEnumerable<string> GetNet10ReferenceAssemblies()
    {
        var searchedRoots = new List<string>();
        var dotnetRoots = GetDotnetRoots();
        searchedRoots.AddRange(dotnetRoots);

        var packCandidates = dotnetRoots
            .Select(root => Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var versionFolders = new List<(Version Version, bool IsStable, string FolderPath)>();

        foreach (var packRoot in packCandidates)
        {
            searchedRoots.Add(packRoot);
            if (!Directory.Exists(packRoot))
            {
                continue;
            }

            foreach (var versionDir in Directory.GetDirectories(packRoot))
            {
                var folderName = Path.GetFileName(versionDir);
                if (!TryParseVersionPrefix(folderName, out var version))
                {
                    continue;
                }

                if (version.Major != 10)
                {
                    continue;
                }

                var refNet10Path = Path.Combine(versionDir, "ref", "net10.0");
                searchedRoots.Add(refNet10Path);
                if (!Directory.Exists(refNet10Path))
                {
                    continue;
                }

                versionFolders.Add((version, !folderName.Contains('-'), refNet10Path));
            }
        }

        if (versionFolders.Count == 0)
        {
            throw new InvalidOperationException(
                "Could not locate .NET 10 reference assemblies. " +
                "Searched: " + string.Join("; ", searchedRoots.Distinct(StringComparer.OrdinalIgnoreCase)) + ". " +
                "Install the .NET SDK / Microsoft.NETCore.App.Ref targeting pack for net10.0.");
        }

        var selectedFolder = versionFolders
            .OrderByDescending(v => v.Version)
            .ThenByDescending(v => v.IsStable)
            .First()
            .FolderPath;

        var referenceDlls = Directory.GetFiles(selectedFolder, "*.dll", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (referenceDlls.Count == 0)
        {
            throw new InvalidOperationException(
                $"No reference assemblies found in '{selectedFolder}'. Install the .NET SDK / Microsoft.NETCore.App.Ref targeting pack for net10.0.");
        }

        return referenceDlls;
    }

    private static IEnumerable<string> GetNonFrameworkDependencyAssemblies()
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;
        var paths = tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var currentAssemblyName = Assembly.GetExecutingAssembly().GetName().Name ?? string.Empty;
        var excludedAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            currentAssemblyName,
            "Nodify_ETLBox",
            "EtlDesigner",
            "EtlDesigner.Engine",
            "EtlDesigner.UI",
            "EtlDesigner.Domain",
            "EtlDesigner.Spike"
        };

        foreach (var path in paths)
        {
            var normalized = path.Replace('\\', '/');
            if (normalized.Contains("/shared/Microsoft.NETCore.App/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/shared/Microsoft.WindowsDesktop.App/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/shared/Microsoft.AspNetCore.App/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = Path.GetFileName(path);
            var assemblyName = Path.GetFileNameWithoutExtension(path);
            if (fileName.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("Nodify", StringComparison.OrdinalIgnoreCase)
                || assemblyName.StartsWith("EtlDesigner", StringComparison.OrdinalIgnoreCase)
                || excludedAssemblyNames.Contains(assemblyName))
            {
                continue;
            }

            if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    private static List<string> GetDotnetRoots()
    {
        var roots = new List<string>();

        var envRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot))
        {
            roots.Add(envRoot);
        }

        var objectAssemblyLocation = typeof(object).Assembly.Location;
        if (!string.IsNullOrWhiteSpace(objectAssemblyLocation))
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(objectAssemblyLocation)!);
            while (dir != null)
            {
                if (dir.Name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    roots.Add(dir.FullName);
                    break;
                }

                dir = dir.Parent;
            }
        }

        return roots
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryParseVersionPrefix(string input, out Version version)
    {
        version = default!;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var prefix = new string(input.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return false;
        }

        return Version.TryParse(prefix, out version!);
    }

    private static string? GetForbiddenReference(string assemblyPath)
    {
        var asm = Assembly.LoadFrom(assemblyPath);
        return asm.GetReferencedAssemblies()
            .Select(a => a.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name) && name.StartsWith("System.Private.", StringComparison.Ordinal));
    }

    private static string BuildGlobalUsingsSource(string globalUsingsPath)
    {
        var lines = File.ReadAllLines(globalUsingsPath)
            .Where(line => line.TrimStart().StartsWith("global using ", StringComparison.Ordinal))
            .Where(line => !line.Contains("System.Windows", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string BuildGeneratedPipelineSource(PipelineSettings settings)
    {
        var delimiterLiteral = SymbolDisplay.FormatLiteral(settings.Delimiter, quote: true);
        var locationLiteral = SymbolDisplay.FormatLiteral(settings.LocationColumn, quote: true);
        var separatorLiteral = SymbolDisplay.FormatLiteral(settings.Separator, quote: true);
        var fields = string.Join(", ", settings.ParseFields.Select(f => SymbolDisplay.FormatLiteral(f, quote: true)));

        return $"// Requires ETLBox 3.11.1 packages and a valid ETLBox license at runtime (free edition: 5,000 rows){Environment.NewLine}" +
               "namespace EtlDesigner.Generated;" + Environment.NewLine + Environment.NewLine +
               "public static class GeneratedPipeline" + Environment.NewLine +
               "{" + Environment.NewLine +
               $"    public static PipelineSettings DefaultSettings => new PipelineSettings({delimiterLiteral}, {locationLiteral}, {separatorLiteral}, new[] {{ {fields} }});" + Environment.NewLine + Environment.NewLine +
               "    public static PipelineCoreResult Run(string csvPath, string country, string? delimiter = null)" + Environment.NewLine +
               "    {" + Environment.NewLine +
               "        if (string.IsNullOrWhiteSpace(country))" + Environment.NewLine +
               "        {" + Environment.NewLine +
               "            return new PipelineCoreResult(\"[]\", \"[]\", 0, \"Country is required.\", new PipelineExecutionStats(0, 0, 0, 0, 0), PipelineFailureNode.CsvSource);" + Environment.NewLine +
               "        }" + Environment.NewLine +
               string.Empty + Environment.NewLine +
               "        var s = DefaultSettings;" + Environment.NewLine +
               "        if (!string.IsNullOrEmpty(delimiter))" + Environment.NewLine +
               "        {" + Environment.NewLine +
               "            s = s with { Delimiter = delimiter };" + Environment.NewLine +
               "        }" + Environment.NewLine +
               string.Empty + Environment.NewLine +
               "        return PipelineCore.Run(s, csvPath, country);" + Environment.NewLine +
               "    }" + Environment.NewLine +
               "}" + Environment.NewLine;
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var idx = 0;
        while ((idx = content.IndexOf(value, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += value.Length;
        }

        return count;
    }
}
