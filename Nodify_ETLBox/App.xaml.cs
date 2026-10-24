using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using System.Windows;
using EtlDesigner.Engine;
using EtlDesigner.Engine.Generation;
using EtlDesigner.Spike;
using EtlDesigner.UI.ViewModels;

namespace Nodify_ETLBox;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Check CLI arguments
        if (e.Args.Contains("--generate-dll"))
        {
            RunGenerateDllAndExit(e.Args);
            return;
        }

        if (e.Args.Contains("--run-tests"))
        {
            var dllIndex = Array.IndexOf(e.Args, "--dll");
            if (dllIndex >= 0 && dllIndex + 1 < e.Args.Length)
            {
                RunTestsAndExit(e.Args[dllIndex + 1]);
            }
            else
            {
                RunTestsAndExit(null);
            }
            return;
        }

        // Manually compose ViewModels
        var mainViewModel = new MainViewModel();

        // Set MainWindow DataContext
        if (MainWindow != null)
        {
            MainWindow.DataContext = mainViewModel;
        }
    }

    private void RunTestsAndExit(string? dllPath)
    {
        Task.Run(async () =>
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var sampleDataDir = Path.Combine(baseDir, "SampleData");

                Console.WriteLine($"Test Harness CLI");
                Console.WriteLine($"Base directory: {baseDir}");
                Console.WriteLine($"Sample data directory: {sampleDataDir}");
                Console.WriteLine();

                if (!Directory.Exists(sampleDataDir))
                {
                    Console.Error.WriteLine($"ERROR: SampleData directory not found at {sampleDataDir}");
                    Environment.Exit(1);
                    return;
                }

                var runner = string.IsNullOrWhiteSpace(dllPath)
                    ? new TestRunner(sampleDataDir)
                    : new TestRunner(sampleDataDir, BuildGeneratedDllExecutor(dllPath));
                Console.WriteLine("Running all tests...");
                Console.WriteLine();

                var results = await runner.RunAllTests();

                Console.WriteLine($"Test Results: {results.Count} tests");
                Console.WriteLine();

                // Summary
                var passed = results.Count(r => r.Result == "PASS");
                var failed = results.Count(r => r.Result == "FAIL");
                var observed = results.Count(r => r.Result == "OBSERVED");

                Console.WriteLine($"Summary: {passed} PASS / {failed} FAIL / {observed} OBSERVED");
                Console.WriteLine();

                // Print failures
                if (failed > 0)
                {
                    Console.WriteLine("FAILED TESTS:");
                    Console.WriteLine();
                    foreach (var result in results.Where(r => r.Result == "FAIL"))
                    {
                        Console.WriteLine($"  {result.TestId}: {result.File} ({result.Country})");
                        Console.WriteLine($"    Expectation: {result.Expectation}");
                        Console.WriteLine($"    Detail: {result.Detail}");
                        if (result.ActualFiltered.HasValue)
                            Console.WriteLine($"    Filtered: {result.ActualFiltered} rows");
                        if (result.ActualParsed.HasValue)
                            Console.WriteLine($"    Parsed: {result.ActualParsed} rows");
                        if (result.ActualSkipped.HasValue)
                            Console.WriteLine($"    Skipped: {result.ActualSkipped} rows");
                        Console.WriteLine();
                    }
                }

                // Write SpikeResults.csv
                var resultsPath = Path.Combine(baseDir, string.IsNullOrWhiteSpace(dllPath) ? "SpikeResults.csv" : "SpikeResults_dll.csv");
                WriteSpikeResultsCsv(resultsPath, results);
                Console.WriteLine($"Results written to: {resultsPath}");

                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                Environment.Exit(1);
            }
        }).Wait();
    }

    private static void WriteSpikeResultsCsv(string filePath, List<TestResult> results)
    {
        using var writer = new StreamWriter(new FileStream(filePath, FileMode.Create, FileAccess.Write), System.Text.Encoding.UTF8);

        // Header
        writer.WriteLine("test_id,file,country,expectation,actual_filtered,actual_parsed,actual_skipped,result,detail,elapsed_ms,license_file_present,rows_read,rows_matched,rows_not_matching,rows_skipped,reconciles");

        // Rows
        foreach (var result in results)
        {
            var filteredStr = result.ActualFiltered?.ToString() ?? "";
            var parsedStr = result.ActualParsed?.ToString() ?? "";
            var skippedStr = result.ActualSkipped?.ToString() ?? "";
            var licenseStr = result.LicenseFilePresent ? "true" : "false";
            var rowsReadStr = result.RowsRead?.ToString() ?? "";
            var rowsMatchedStr = result.RowsMatched?.ToString() ?? "";
            var rowsNotMatchingStr = result.RowsNotMatching?.ToString() ?? "";
            var rowsSkippedStr = result.RowsSkipped?.ToString() ?? "";
            var reconcilesStr = result.Reconciles.HasValue ? (result.Reconciles.Value ? "true" : "false") : "";

            var detail = CsvEscape(result.Detail);
            writer.WriteLine($"{result.TestId},{result.File},{result.Country},{result.Expectation},{filteredStr},{parsedStr},{skippedStr},{result.Result},{detail},{result.ElapsedMs},{licenseStr},{rowsReadStr},{rowsMatchedStr},{rowsNotMatchingStr},{rowsSkippedStr},{reconcilesStr}");
        }

        writer.Flush();
    }

    private static Func<string, string?, string, PipelineRunResult> BuildGeneratedDllExecutor(string dllPath)
    {
        if (!File.Exists(dllPath))
            throw new FileNotFoundException($"DLL not found: {dllPath}");

        var asm = Assembly.LoadFrom(dllPath);
        var pipelineType = asm.GetType("EtlDesigner.Generated.GeneratedPipeline", throwOnError: true)!;
        var runMethod = pipelineType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("GeneratedPipeline.Run method not found.");

        return (csvPath, country, delimiter) =>
        {
            var runResultObj = runMethod.Invoke(null, new object?[] { csvPath, country ?? string.Empty, delimiter });
            if (runResultObj == null)
                return new PipelineRunResult(new PipelineResult("[]", "[]", 0, "Generated pipeline returned null."), new PipelineExecutionStats(0, 0, 0, 0, 0), PipelineFailureNode.Unknown);

            var resultType = runResultObj.GetType();
            var filteredJson = resultType.GetProperty("FilteredJson")?.GetValue(runResultObj)?.ToString() ?? "[]";
            var parsedJson = resultType.GetProperty("ParsedJson")?.GetValue(runResultObj)?.ToString() ?? "[]";
            var skipped = Convert.ToInt32(resultType.GetProperty("SkippedMalformedCount")?.GetValue(runResultObj) ?? 0);
            var error = resultType.GetProperty("ErrorMessage")?.GetValue(runResultObj)?.ToString();

            var statsObj = resultType.GetProperty("Stats")?.GetValue(runResultObj);
            var rowsRead = Convert.ToInt32(statsObj?.GetType().GetProperty("RowsRead")?.GetValue(statsObj) ?? 0);
            var rowsMatched = Convert.ToInt32(statsObj?.GetType().GetProperty("RowsMatched")?.GetValue(statsObj) ?? 0);
            var rowsNotMatched = Convert.ToInt32(statsObj?.GetType().GetProperty("RowsNotMatched")?.GetValue(statsObj) ?? 0);
            var rowsSkipped = Convert.ToInt32(statsObj?.GetType().GetProperty("RowsMalformedSkipped")?.GetValue(statsObj) ?? 0);
            var parsedRecordCount = Convert.ToInt32(statsObj?.GetType().GetProperty("ParsedRecordCount")?.GetValue(statsObj) ?? 0);

            var failureNodeObj = resultType.GetProperty("FailureNode")?.GetValue(runResultObj)?.ToString();
            var failureNode = failureNodeObj switch
            {
                "CsvSource" => PipelineFailureNode.CsvSource,
                "ToJson" => PipelineFailureNode.ToJson,
                "ParseJson" => PipelineFailureNode.ParseJson,
                _ => PipelineFailureNode.Unknown
            };

            return new PipelineRunResult(
                new PipelineResult(filteredJson, parsedJson, skipped, error),
                new PipelineExecutionStats(rowsRead, rowsMatched, rowsNotMatched, rowsSkipped, parsedRecordCount),
                failureNode);
        };
    }

    private void RunGenerateDllAndExit(string[] args)
    {
        Task.Run(() =>
        {
            string outputFolder;
            string logPath;
            try
            {
                var idx = Array.IndexOf(args, "--generate-dll");
                outputFolder = (idx >= 0 && idx + 1 < args.Length) ? args[idx + 1] : AppContext.BaseDirectory;
                Directory.CreateDirectory(outputFolder);
                logPath = Path.Combine(outputFolder, "generate-dll.log");

                var settings = new PipelineSettings(
                    Delimiter: ",",
                    LocationColumn: "location",
                    Separator: "|",
                    ParseFields: new[] { "first_name", "last_name", "email" });

                var result = DllGenerator.Generate(settings, outputFolder, "GeneratedPipeline");

                var logLines = new List<string>();
                if (result.Success)
                {
                    var successLine = $"DLL: {result.DllPath}";
                    Console.WriteLine(successLine);
                    logLines.Add(successLine);
                    foreach (var d in result.Diagnostics)
                    {
                        Console.WriteLine(d);
                        logLines.Add(d);
                    }

                    File.WriteAllLines(logPath, logLines);
                    Environment.Exit(0);
                    return;
                }

                var failReason = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "Unknown error." : result.ErrorMessage;
                var failedLine = $"FAILED: {failReason}";
                Console.Error.WriteLine(failedLine);
                logLines.Add(failedLine);
                foreach (var d in result.Diagnostics)
                {
                    Console.Error.WriteLine(d);
                    logLines.Add(d);
                }

                File.WriteAllLines(logPath, logLines);
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                outputFolder = Array.IndexOf(args, "--generate-dll") is var i && i >= 0 && i + 1 < args.Length
                    ? args[i + 1]
                    : AppContext.BaseDirectory;
                Directory.CreateDirectory(outputFolder);
                logPath = Path.Combine(outputFolder, "generate-dll.log");

                var failedLine = $"FAILED: {ex.Message}";
                Console.Error.WriteLine(failedLine);
                Console.Error.WriteLine(ex.ToString());
                File.WriteAllLines(logPath, new[] { failedLine, ex.ToString() });
                Environment.Exit(1);
            }
        }).Wait();
    }

    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
