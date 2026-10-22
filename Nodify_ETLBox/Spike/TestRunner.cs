using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ETLBox.Csv;
using ETLBox.DataFlow;

namespace EtlDesigner.Spike;

/// <summary>
/// Test result record per test case.
/// </summary>
public record TestResult(
    string TestId,
    string File,
    string Country,
    string Expectation,
    int? ActualFiltered,
    int? ActualParsed,
    int? ActualSkipped,
    string Result, // PASS, FAIL, OBSERVED
    string Detail,
    long ElapsedMs,
    bool LicenseFilePresent);

/// <summary>
/// Runs all tests from the test manifest and reports results.
/// </summary>
public class TestRunner
{
    private const int TimeoutSeconds = 60;
    private readonly string _testManifestPath;
    private readonly string _expectedValuesPath;
    private readonly string _sampleDataDir;
    private readonly bool _licenseFilePresent;

    public TestRunner(string sampleDataDir)
    {
        _sampleDataDir = sampleDataDir;
        _testManifestPath = Path.Combine(sampleDataDir, "TestData", "expected_results.csv");
        _expectedValuesPath = Path.Combine(sampleDataDir, "TestData", "expected_values.json");

        // Check if license file exists in AppContext.BaseDirectory or if etlbox env var is set
        var licenseInAppDir = File.Exists(Path.Combine(AppContext.BaseDirectory, "etlbox.lic"));
        var licenseEnvVar = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("etlbox"));
        _licenseFilePresent = licenseInAppDir || licenseEnvVar;
    }

    /// <summary>
    /// Run all tests and return results.
    /// </summary>
    public async Task<List<TestResult>> RunAllTests()
    {
        var results = new List<TestResult>();

        // Read test manifest
        var testCases = new List<ExpandoObject>();
        try
        {
            var csvSource = new CsvSource<ExpandoObject>(_testManifestPath);
            var memoryDest = new MemoryDestination<ExpandoObject>();
            csvSource.LinkTo(memoryDest);
            var network = new Network(csvSource, memoryDest);
            network.Execute();
            testCases = memoryDest.Data.ToList();
        }
        catch (Exception ex)
        {
            return new List<TestResult> { new TestResult(
                "manifest", "", "", "", null, null, null,
                "FAIL", $"Failed to read test manifest: {ex.Message}", 0, _licenseFilePresent) };
        }

        // Load expected values
        var expectedValues = new Dictionary<string, (List<Dictionary<string, object?>> filtered, List<Dictionary<string, object?>> parsed)>();
        try
        {
            if (File.Exists(_expectedValuesPath))
            {
                var json = File.ReadAllText(_expectedValuesPath);
                using (var doc = JsonDocument.Parse(json))
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        var testId = prop.Name;
                        var filtered = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(
                            prop.Value.GetProperty("filtered").GetRawText()) ?? new();
                        var parsed = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(
                            prop.Value.GetProperty("parsed").GetRawText()) ?? new();
                        expectedValues[testId] = (filtered, parsed);
                    }
                }
            }
        }
        catch
        {
            // Continue without expected values
        }

        // Execute each test
        foreach (var testCaseObj in testCases)
        {
            var testCase = testCaseObj as IDictionary<string, object?>;
            var testId = testCase?["test_id"]?.ToString() ?? "unknown";
            var file = testCase?["file"]?.ToString() ?? "";
            var delimiterStr = testCase?["delimiter"]?.ToString() ?? "\"";
            var country = testCase?["country"]?.ToString();
            var expectation = testCase?["expectation"]?.ToString() ?? "pass";
            var expectedFiltered = ParseNullableInt(testCase?["expected_filtered"]);
            var expectedParsed = ParseNullableInt(testCase?["expected_parsed"]);
            var expectedSkipped = ParseNullableInt(testCase?["expected_skipped"]);
            var hasValues = (testCase?["has_values"]?.ToString() ?? "no").Equals("yes", StringComparison.OrdinalIgnoreCase);

            // Normalize delimiter: "\"" -> ",", "\\t" -> tab, ";" -> ";"
            var delimiter = delimiterStr == "\"" ? "," : (delimiterStr == "\\t" ? "\t" : delimiterStr);

            var result = await RunSingleTest(testId, file, country, expectation, delimiter, expectedFiltered, expectedParsed, expectedSkipped, hasValues, expectedValues);
            results.Add(result);
        }

        return results;
    }

    private async Task<TestResult> RunSingleTest(
        string testId, string file, string? country, string expectation, string delimiter,
        int? expectedFiltered, int? expectedParsed, int? expectedSkipped, bool hasValues,
        Dictionary<string, (List<Dictionary<string, object?>> filtered, List<Dictionary<string, object?>> parsed)> expectedValues)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // Resolve file path - files are in SampleData/TestData
            var csvPath = Path.Combine(_sampleDataDir, "TestData", file);

            // Determine delimiter - use the one from manifest
            // No need for fallback detection

            // Run pipeline with timeout
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
            PipelineResult pipelineResult;
            try
            {
                pipelineResult = await Task.Run(() => ETLBoxSpike.RunPipeline(csvPath, country, delimiter), cts.Token);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return new TestResult(testId, file, country ?? "", expectation, null, null, null,
                    "FAIL", "Test timeout (> 60 seconds)", sw.ElapsedMilliseconds, _licenseFilePresent);
            }

            sw.Stop();

            // Parse results
            var filteredJson = JsonDocument.Parse(pipelineResult.FilteredJson ?? "[]");
            var parsedJson = JsonDocument.Parse(pipelineResult.ParsedJson ?? "[]");
            var actualFiltered = filteredJson.RootElement.GetArrayLength();
            var actualParsed = parsedJson.RootElement.GetArrayLength();
            var actualSkipped = pipelineResult.SkippedMalformedCount;
            var hasError = !string.IsNullOrEmpty(pipelineResult.ErrorMessage);

            // Validate expectations
            var mismatches = new List<string>();

            if (expectation == "error_expected")
            {
                if (!hasError)
                    mismatches.Add("Expected error but got success");
                else
                {
                    sw.Stop();
                    return new TestResult(testId, file, country ?? "", expectation, null, null, null,
                        "PASS", $"Error as expected: {pipelineResult.ErrorMessage}", sw.ElapsedMilliseconds, _licenseFilePresent);
                }
            }
            else if (hasError)
            {
                mismatches.Add($"Unexpected error: {pipelineResult.ErrorMessage}");
            }

            // Check counts
            if (expectedFiltered.HasValue && actualFiltered != expectedFiltered.Value)
                mismatches.Add($"Filtered count: expected {expectedFiltered}, got {actualFiltered}");
            if (expectedParsed.HasValue && actualParsed != expectedParsed.Value)
                mismatches.Add($"Parsed count: expected {expectedParsed}, got {actualParsed}");
            if (expectedSkipped.HasValue && actualSkipped != expectedSkipped.Value)
                mismatches.Add($"Skipped count: expected {expectedSkipped}, got {actualSkipped}");

            // Check key counts (all arrays should have same number of keys per object)
            var filteredKeyCount = GetUniformKeyCount(filteredJson.RootElement);
            var parsedKeyCount = GetUniformKeyCount(parsedJson.RootElement);
            if (filteredKeyCount == null || filteredKeyCount < 0)
                mismatches.Add("Filtered objects have inconsistent key counts");
            if (parsedKeyCount == null || parsedKeyCount < 0)
                mismatches.Add("Parsed objects have inconsistent key counts");
            else if (parsedKeyCount > 0 && parsedKeyCount != 3)  // Only check if there are objects
                mismatches.Add($"Parsed objects should have 3 keys, got {parsedKeyCount}");

            // Check SHA-1 fingerprint of filtered IDs
            var filteredIdSha1 = ComputeIdFingerprint(filteredJson.RootElement);

            // Check deep equality if has_values
            if (hasValues && expectedValues.TryGetValue(testId, out var expectedArrays))
            {
                var filteredMismatch = CheckDeepEquality("filtered", expectedArrays.filtered, filteredJson.RootElement);
                var parsedMismatch = CheckDeepEquality("parsed", expectedArrays.parsed, parsedJson.RootElement);
                if (!string.IsNullOrEmpty(filteredMismatch))
                    mismatches.Add(filteredMismatch);
                if (!string.IsNullOrEmpty(parsedMismatch))
                    mismatches.Add(parsedMismatch);
            }

            // Determine result
            string result = "PASS";
            string detail = "";

            if (expectation == "observe")
            {
                result = "OBSERVED";
                detail = $"Filtered: {actualFiltered}, Parsed: {actualParsed}, Skipped: {actualSkipped}";
                if (!string.IsNullOrEmpty(filteredIdSha1))
                    detail += $", SHA1: {filteredIdSha1}";
            }
            else if (mismatches.Count > 0)
            {
                result = "FAIL";
                detail = string.Join("; ", mismatches);
            }

            return new TestResult(testId, file, country ?? "", expectation, actualFiltered, actualParsed, actualSkipped, result, detail, sw.ElapsedMilliseconds, _licenseFilePresent);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TestResult(testId, file, country ?? "", expectation, null, null, null,
                "FAIL", $"Exception: {ex.Message}", sw.ElapsedMilliseconds, _licenseFilePresent);
        }
    }

    private int? GetUniformKeyCount(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return null;

        if (element.GetArrayLength() == 0)
            return 0; // Empty array

        try
        {
            // Get key count from first object
            var firstObj = element[0];
            var count = firstObj.EnumerateObject().Count();

            // Verify all objects have same count
            foreach (var obj in element.EnumerateArray().Skip(1))
            {
                if (obj.EnumerateObject().Count() != count)
                    return -1; // Inconsistent
            }

            return count;
        }
        catch
        {
            return null;
        }
    }

    private string ComputeIdFingerprint(JsonElement filteredArray)
    {
        try
        {
            var ids = new List<string>();
            foreach (var obj in filteredArray.EnumerateArray())
            {
                if (obj.TryGetProperty("id", out var idProp))
                    ids.Add(idProp.GetString() ?? "");
            }

            if (ids.Count == 0)
                return "";

            var combined = string.Join(",", ids).ToLowerInvariant();
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(combined));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
        catch
        {
            return "";
        }
    }

    private string? CheckDeepEquality(string arrayName, List<Dictionary<string, object?>> expected, JsonElement actual)
    {
        try
        {
            var actualList = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(actual.GetRawText()) ?? new();

            if (actualList.Count != expected.Count)
                return $"{arrayName} array length mismatch: expected {expected.Count}, got {actualList.Count}";

            for (int i = 0; i < expected.Count; i++)
            {
                var exp = expected[i];
                var act = actualList[i];

                foreach (var kvp in exp)
                {
                    if (!act.TryGetValue(kvp.Key, out var actVal))
                        return $"{arrayName}[{i}] missing key '{kvp.Key}'";

                    var expStr = kvp.Value?.ToString() ?? "";
                    var actStr = actVal?.ToString() ?? "";
                    if (expStr != actStr)
                        return $"{arrayName}[{i}].{kvp.Key}: expected '{expStr}', got '{actStr}'";
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"{arrayName} deep equality check error: {ex.Message}";
        }
    }

    private int? ParseNullableInt(object? value)
    {
        if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
            return null;
        if (int.TryParse(value.ToString(), out var result))
            return result;
        return null;
    }
}
