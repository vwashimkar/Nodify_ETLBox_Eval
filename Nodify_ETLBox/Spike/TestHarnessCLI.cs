using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace EtlDesigner.Spike;

/// <summary>
/// Standalone CLI for running the test harness without WPF UI.
/// Call this from Program.cs or a unit test to execute and report results.
/// </summary>
public static class TestHarnessCLI
{
    public static async Task RunAllTests()
    {
        var baseDir = AppContext.BaseDirectory;
        var sampleDataDir = Path.Combine(baseDir, "SampleData");

        Console.WriteLine($"Test Harness CLI");
        Console.WriteLine($"Base directory: {baseDir}");
        Console.WriteLine($"Sample data directory: {sampleDataDir}");
        Console.WriteLine();

        if (!Directory.Exists(sampleDataDir))
        {
            Console.WriteLine($"ERROR: SampleData directory not found at {sampleDataDir}");
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
    }

    private static void WriteSpikeResultsCsv(string filePath, List<TestResult> results)
    {
        using var writer = new StreamWriter(new FileStream(filePath, FileMode.Create, FileAccess.Write), System.Text.Encoding.UTF8);

        // Header
        writer.WriteLine("test_id,file,country,expectation,actual_filtered,actual_parsed,actual_skipped,result,detail,elapsed_ms,license_file_present");

        // Rows
        foreach (var result in results)
        {
            var filteredStr = result.ActualFiltered?.ToString() ?? "";
            var parsedStr = result.ActualParsed?.ToString() ?? "";
            var skippedStr = result.ActualSkipped?.ToString() ?? "";
            var licenseStr = result.LicenseFilePresent ? "true" : "false";

            var detail = CsvEscape(result.Detail);
            writer.WriteLine($"{result.TestId},{result.File},{result.Country},{result.Expectation},{filteredStr},{parsedStr},{skippedStr},{result.Result},{detail},{result.ElapsedMs},{licenseStr}");
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
