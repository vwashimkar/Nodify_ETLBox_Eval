using System.Diagnostics;
using System.IO;
using EtlDesigner.Engine.Generation;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>
/// Task G7a: the DLL of a pipeline made in the guided builder (no ParseValue, one column split), generated with the real
/// generator, run in-process and by the existing console consumer in its own process on a second file with more rows.
/// </summary>
public sealed class GuidedBuilderDllTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _folder = Directory.CreateTempSubdirectory("g7a_dll_").FullName;

    public GuidedBuilderDllTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    [Fact]
    public async Task TheDllOfABuilderPipeline_ReadsASecondFileWithMoreRows_LikeTheApp_InProcessAndInTheConsoleConsumer()
    {
        var sample = G6bParts.Write(_folder, "i10.txt", G6bParts.Variant("example"));
        var b = await GuidedBuilderTests.ToTryAsync(_folder, sample, columns: vm =>
        {
            vm.SplitEnabled = true;
            vm.SplitColumn = "sample_id";
            vm.SelectedSeparator = "other";
            vm.OtherSeparator = "-";
            vm.PartNamesText = "Prefix, Number";
        });
        var definition = b.Vm.TriedDefinition!;
        Assert.Null(b.Main.GenerateDllBlockedReason);

        using var dll = GeneratedDll.Create(definition, b.Vm.TriedResult!.Columns);
        Assert.True(dll.Generation.Success, string.Join(" ", dll.Generation.Messages));
        var readme = File.ReadAllText(dll.Generation.ReadmePath!);
        Assert.Contains("CSV part -> DotNetObject", readme);
        Assert.DoesNotContain("CSV part -> ParseValue", readme);
        Assert.Contains(typeof(EtlDesigner.Engine.Pipeline.ColumnSplit).Name, string.Join(",", CsvPipelineDllGenerator.SourceFilesFor(definition)));

        var second = G6bParts.Write(_folder, "second.txt", G6bParts.Variant("many rows"));
        var app = await G6bParts.Run(definition, second);
        Assert.Null(app.Error);
        Assert.Equal(4_005, app.Counts.Out);
        var (inProcess, facts) = await PartDll.RunAsync(dll, second);
        PartDll.Equal(app, inProcess, facts);

        // The existing console consumer, built against this DLL, in its own process.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Nodify_ETLBox_Eval.slnx")))
        {
            root = root.Parent;
        }

        var project = Path.Combine(root!.FullName, "Consumer", "CsvPipelineConsumer", "CsvPipelineConsumer.csproj");
        var output = Path.Combine(_folder, "consumer");
        var build = await Exec("dotnet", ["build", project, "-c", "Release", "-o", output, $"-p:PipelineDll={dll.Generation.DllPath}", "-nologo", "-v:q"], root.FullName);
        Assert.True(build.Code == 0, "consumer build failed: " + build.Output);
        var run = await Exec("dotnet", [Path.Combine(output, "CsvPipelineConsumer.dll"), second, "--all"], output);
        Assert.Equal(0, run.Code);
        var printed = run.Output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("row: ", StringComparison.Ordinal)).Select(l => l[5..]).ToList();
        _output.WriteLine($"consumer printed {printed.Count} rows; first: {printed.FirstOrDefault()}");
        Assert.Equal(G6bParts.RowTexts(app.Columns, app.Rows), printed);
        Assert.Equal(["Prefix", "Number", "weight_g", "volume_ml", "chloride_pct", "result"], app.Columns);
        Assert.False(File.Exists(Path.Combine(output, "lineage.log")));
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
