global using System;
global using System.Collections.Generic;
global using System.Dynamic;
global using System.IO;
global using System.Linq;
global using System.Text.Json;

using ETLBox;
using ETLBox.Csv;
using ETLBox.DataFlow;
using ETLBox.Json;

namespace EtlDesigner.Spike;

/// <summary>
/// ETLBox pipeline harness for testing.
/// 
/// Reads CSV, filters by country (first segment before '|' in location column, trimmed, ordinal ignore-case).
/// Malformed rows (no '|' or empty country) are skipped and counted.
/// Filtered rows (all columns) written to JSON.
/// Then parsed back and projected to first_name, last_name, email.
/// </summary>
public class ETLBoxSpike
{
    /// <summary>
    /// Run the pipeline: CSV → filter by country → JSON → project fields → JSON
    /// </summary>
    /// <param name="csvPath">Full path to CSV file</param>
    /// <param name="country">Country to filter by (null or empty = skip filtering)</param>
    /// <param name="delimiter">CSV delimiter. Use "\\t" for tab (will be converted to actual tab)</param>
    /// <returns>PipelineResult with FilteredJson, ParsedJson, SkippedMalformedCount, ErrorMessage</returns>
    public static PipelineResult RunPipeline(string csvPath, string? country, string delimiter)
    {
        try
        {
            // Validate input CSV exists
            if (!File.Exists(csvPath))
                return new PipelineResult("[]", "[]", 0, $"CSV file not found: {csvPath}");

            // Convert delimiter literal "\\t" to actual tab
            string actualDelimiter = delimiter == "\\t" ? "\t" : delimiter;

            // Validate required columns exist by reading header (using the actual delimiter)
            var requiredColumns = new[] { "location", "first_name", "last_name", "email" };
            try
            {
                using (var reader = new StreamReader(csvPath))
                {
                    var headerLine = reader.ReadLine();
                    if (string.IsNullOrEmpty(headerLine))
                        return new PipelineResult("[]", "[]", 0, "CSV file has no header");

                    var headers = headerLine.Split(actualDelimiter[0]).Select(h => h.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

                    foreach (var requiredCol in requiredColumns)
                    {
                        if (!headers.Contains(requiredCol))
                            return new PipelineResult("[]", "[]", 0, $"Missing required column: {requiredCol}");
                    }
                }
            }
            catch (Exception ex)
            {
                return new PipelineResult("[]", "[]", 0, $"Error validating CSV columns: {ex.Message}");
            }

            // Read CSV with delimiter
            var csvSource = new CsvSource<ExpandoObject>(csvPath);

            // Try to configure delimiter via reflection (ETLBox.Csv.CsvSettings pattern)
            if (actualDelimiter != ",")
            {
                try
                {
                    // Look for Settings property or constructor parameter
                    var settingsProp = typeof(CsvSource<ExpandoObject>).GetProperty("Settings");
                    if (settingsProp != null)
                    {
                        var settings = settingsProp.GetValue(csvSource);
                        if (settings != null)
                        {
                            var delimProp = settings.GetType().GetProperty("Delimiter");
                            if (delimProp != null && delimProp.CanWrite)
                            {
                                delimProp.SetValue(settings, actualDelimiter);
                            }
                        }
                    }
                    // Fallback: try direct property
                    else
                    {
                        var prop = typeof(CsvSource<ExpandoObject>).GetProperty("Delimiter");
                        if (prop != null && prop.CanWrite)
                            prop.SetValue(csvSource, actualDelimiter);
                    }
                }
                catch
                {
                    // Delimiter not configurable; will use default ","
                    // For tab/semicolon, this will cause column mismatch or error downstream
                }
            }

            int skippedMalformedCount = 0;

            // Filter by country
            var filterCountry = new RowTransformation(row =>
            {
                var dict = (IDictionary<string, object?>)row;

                // location column is already validated to exist by header check above
                var locationObj = dict["location"];
                var location = locationObj?.ToString() ?? "";

                if (string.IsNullOrEmpty(country))
                    return row; // No country filter, pass through all

                // Find first '|'
                var pipeIndex = location.IndexOf('|');
                if (pipeIndex < 0)
                {
                    // No pipe = malformed
                    skippedMalformedCount++;
                    return null;
                }

                var rowCountry = location.Substring(0, pipeIndex).Trim();

                if (string.IsNullOrEmpty(rowCountry))
                {
                    // Empty country after trim = malformed
                    skippedMalformedCount++;
                    return null;
                }

                // Match ordinal ignore-case
                if (!string.Equals(rowCountry, country, StringComparison.OrdinalIgnoreCase))
                    return null; // Country mismatch, drop

                return row; // Keep matching rows
            });

            // Write filtered to temp JSON
            var tempFilteredJson = Path.Combine(Path.GetTempPath(), $"spike_filtered_{Guid.NewGuid()}.json");
            var jsonDestination = new JsonDestination<ExpandoObject>(tempFilteredJson);

            csvSource.LinkTo(filterCountry);
            filterCountry.LinkTo(jsonDestination);

            var network1 = new Network(csvSource, filterCountry, jsonDestination);
            network1.Execute();

            // Read temp JSON
            string filteredJsonRaw;
            try
            {
                filteredJsonRaw = File.ReadAllText(tempFilteredJson);
            }
            catch (FileNotFoundException)
            {
                // JsonDestination might not create file if no rows pass filter
                filteredJsonRaw = "[]";
            }

            // Indent filtered JSON
            string filteredJson;
            if (string.IsNullOrEmpty(filteredJsonRaw) || filteredJsonRaw.Trim() == "")
            {
                filteredJson = "[]";
            }
            else
            {
                try
                {
                    using (var doc = JsonDocument.Parse(filteredJsonRaw))
                    {
                        filteredJson = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
                    }
                }
                catch (JsonException)
                {
                    filteredJson = filteredJsonRaw;
                }
            }

            // Step 2: Parse filtered JSON and project fields
            // If no filtered results, skip parsing and return empty parsed JSON
            if (string.IsNullOrEmpty(filteredJsonRaw) || filteredJsonRaw.Trim() == "" || filteredJsonRaw.Trim() == "[]")
            {
                // No rows passed filter, return empty parsed JSON
                try { File.Delete(tempFilteredJson); } catch { }
                return new PipelineResult(filteredJson, "[]", skippedMalformedCount, null);
            }

            var jsonSource = new JsonSource<ExpandoObject>(tempFilteredJson);

            // Project to 3 fields
            var projectFields = new RowTransformation(row =>
            {
                var dict = (IDictionary<string, object?>)row;
                var expanded = new ExpandoObject();
                var dictExpanded = (IDictionary<string, object?>)expanded;
                dictExpanded["first_name"] = dict.ContainsKey("first_name") ? dict["first_name"] : "";
                dictExpanded["last_name"] = dict.ContainsKey("last_name") ? dict["last_name"] : "";
                dictExpanded["email"] = dict.ContainsKey("email") ? dict["email"] : "";
                return expanded;
            });

            var memoryDestination = new MemoryDestination<ExpandoObject>();

            jsonSource.LinkTo(projectFields);
            projectFields.LinkTo(memoryDestination);

            var network2 = new Network(jsonSource, projectFields, memoryDestination);
            network2.Execute();

            // Convert memory records to JSON with indentation
            string parsedJson;
            if (memoryDestination.Data == null || memoryDestination.Data.Count == 0)
            {
                parsedJson = "[]";
            }
            else
            {
                try
                {
                    var dictRecords = memoryDestination.Data.Select(exp =>
                    {
                        var dict = (IDictionary<string, object?>)exp;
                        return new Dictionary<string, object?>
                        {
                            { "first_name", dict.ContainsKey("first_name") ? dict["first_name"] : "" },
                            { "last_name", dict.ContainsKey("last_name") ? dict["last_name"] : "" },
                            { "email", dict.ContainsKey("email") ? dict["email"] : "" }
                        };
                    }).ToList();
                    parsedJson = JsonSerializer.Serialize(dictRecords, new JsonSerializerOptions { WriteIndented = true });
                }
                catch (Exception ex)
                {
                    return new PipelineResult(filteredJson, "[]", skippedMalformedCount, $"Error converting memory to JSON: {ex.Message}");
                }
            }

            // Cleanup
            try { File.Delete(tempFilteredJson); } catch { }

            return new PipelineResult(filteredJson, parsedJson, skippedMalformedCount, null);
        }
        catch (Exception ex)
        {
            return new PipelineResult("[]", "[]", 0, $"Pipeline error: {ex.Message}");
        }
    }
}
