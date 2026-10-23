using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using EtlDesigner.Spike;
using EtlDesigner.UI.ViewModels;

namespace Nodify_ETLBox;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Check for --run-tests command line argument
        if (e.Args.Contains("--run-tests"))
        {
            RunTestsAndExit();
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

    private void RunTestsAndExit()
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

                var runner = new TestRunner(sampleDataDir);
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
                var resultsPath = Path.Combine(baseDir, "SpikeResults.csv");
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
