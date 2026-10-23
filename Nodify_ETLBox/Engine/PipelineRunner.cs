using System.Dynamic;
using System.IO;
using System.Text.Json;
using ETLBox;
using ETLBox.Csv;
using ETLBox.DataFlow;
using ETLBox.Json;
using EtlDesigner.Spike;

namespace EtlDesigner.Engine;

public static class PipelineRunner
{
    public static PipelineResult Run(PipelineSettings settings)
        => RunWithStats(settings).Result;

    public static PipelineRunResult RunWithStats(PipelineSettings settings)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settings.CsvFilePath))
            {
                return CreateError("CSV file path is required.");
            }

            if (!File.Exists(settings.CsvFilePath))
            {
                return CreateError($"CSV file not found: {settings.CsvFilePath}");
            }

            if (string.IsNullOrEmpty(settings.Delimiter))
            {
                return CreateError("Delimiter is required.");
            }

            if (string.IsNullOrEmpty(settings.Separator))
            {
                return CreateError("Separator is required.");
            }

            if (string.IsNullOrWhiteSpace(settings.LocationColumn))
            {
                return CreateError("Location column is required.");
            }

            if (settings.ParseFields == null || settings.ParseFields.Count == 0)
            {
                return CreateError("At least one parse field is required.");
            }

            var actualDelimiter = settings.Delimiter == "\\t" ? "\t" : settings.Delimiter;
            if (actualDelimiter.Length != 1)
            {
                return CreateError("Delimiter must be a single character.");
            }

            var requiredColumns = settings.ParseFields
                .Concat(new[] { settings.LocationColumn })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string headerLine;
            try
            {
                using var reader = new StreamReader(settings.CsvFilePath);
                headerLine = reader.ReadLine() ?? string.Empty;
            }
            catch (Exception ex)
            {
                return CreateError($"Error reading CSV header: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(headerLine))
            {
                return CreateError("CSV file has no header");
            }

            var headers = headerLine
                .Split(actualDelimiter[0])
                .Select(h => h.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var requiredColumn in requiredColumns)
            {
                if (!headers.Contains(requiredColumn))
                {
                    return CreateError($"Missing required column: {requiredColumn}");
                }
            }

            int rowsRead = 0;
            int rowsMatched = 0;
            int rowsNotMatched = 0;
            int skippedMalformedCount = 0;

            var csvSource = new CsvSource<ExpandoObject>(settings.CsvFilePath);
            TryConfigureDelimiter(csvSource, actualDelimiter);

            var countingTap = new RowTransformation(row =>
            {
                rowsRead++;
                return row;
            });

            var filterCountry = new RowTransformation(row =>
            {
                var dict = (IDictionary<string, object?>)row;

                if (!dict.TryGetValue(settings.LocationColumn, out var locationObj))
                {
                    skippedMalformedCount++;
                    return null;
                }

                var location = locationObj?.ToString() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(settings.Country))
                {
                    rowsMatched++;
                    return row;
                }

                var separatorIndex = location.IndexOf(settings.Separator, StringComparison.Ordinal);
                if (separatorIndex < 0)
                {
                    skippedMalformedCount++;
                    return null;
                }

                var rowCountry = location[..separatorIndex].Trim();
                if (string.IsNullOrEmpty(rowCountry))
                {
                    skippedMalformedCount++;
                    return null;
                }

                if (string.Equals(rowCountry, settings.Country, StringComparison.OrdinalIgnoreCase))
                {
                    rowsMatched++;
                    return row;
                }

                rowsNotMatched++;
                return null;
            });

            var tempFilteredJson = Path.Combine(Path.GetTempPath(), $"pipeline_filtered_{Guid.NewGuid()}.json");
            var jsonDestination = new JsonDestination<ExpandoObject>(tempFilteredJson);

            csvSource.LinkTo(countingTap);
            countingTap.LinkTo(filterCountry);
            filterCountry.LinkTo(jsonDestination);

            var network1 = new Network(csvSource, countingTap, filterCountry, jsonDestination);
            network1.Execute();

            string filteredJsonRaw;
            try
            {
                filteredJsonRaw = File.ReadAllText(tempFilteredJson);
            }
            catch (FileNotFoundException)
            {
                filteredJsonRaw = "[]";
            }

            string filteredJson;
            if (string.IsNullOrWhiteSpace(filteredJsonRaw))
            {
                filteredJsonRaw = "[]";
                filteredJson = "[]";
            }
            else
            {
                filteredJson = TryIndentJson(filteredJsonRaw);
            }

            if (filteredJsonRaw.Trim() == "[]")
            {
                TryDelete(tempFilteredJson);
                var emptyResult = new PipelineResult(filteredJson, "[]", skippedMalformedCount, null);
                var emptyStats = new PipelineExecutionStats(rowsRead, rowsMatched, rowsNotMatched, skippedMalformedCount, 0);
                return new PipelineRunResult(emptyResult, emptyStats);
            }

            var jsonSource = new JsonSource<ExpandoObject>(tempFilteredJson);

            var projectFields = new RowTransformation(row =>
            {
                var dict = (IDictionary<string, object?>)row;
                var expanded = new ExpandoObject();
                var output = (IDictionary<string, object?>)expanded;

                foreach (var field in settings.ParseFields)
                {
                    output[field] = dict.ContainsKey(field) ? dict[field] : string.Empty;
                }

                return expanded;
            });

            var memoryDestination = new MemoryDestination<ExpandoObject>();

            jsonSource.LinkTo(projectFields);
            projectFields.LinkTo(memoryDestination);

            var network2 = new Network(jsonSource, projectFields, memoryDestination);
            network2.Execute();

            string parsedJson;
            if (memoryDestination.Data == null || memoryDestination.Data.Count == 0)
            {
                parsedJson = "[]";
            }
            else
            {
                var dictRecords = memoryDestination.Data.Select(exp =>
                {
                    var dict = (IDictionary<string, object?>)exp;
                    var record = new Dictionary<string, object?>();
                    foreach (var field in settings.ParseFields)
                    {
                        record[field] = dict.ContainsKey(field) ? dict[field] : string.Empty;
                    }

                    return record;
                }).ToList();

                parsedJson = JsonSerializer.Serialize(dictRecords, new JsonSerializerOptions { WriteIndented = true });
            }

            TryDelete(tempFilteredJson);

            var result = new PipelineResult(filteredJson, parsedJson, skippedMalformedCount, null);
            var stats = new PipelineExecutionStats(rowsRead, rowsMatched, rowsNotMatched, skippedMalformedCount, memoryDestination.Data?.Count ?? 0);
            return new PipelineRunResult(result, stats);
        }
        catch (Exception ex)
        {
            return CreateError($"Pipeline error: {ex.Message}");
        }
    }

    private static PipelineRunResult CreateError(string message)
    {
        return new PipelineRunResult(
            new PipelineResult("[]", "[]", 0, message),
            new PipelineExecutionStats(0, 0, 0, 0, 0));
    }

    private static string TryIndentJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch
        {
        }
    }

    private static void TryConfigureDelimiter(CsvSource<ExpandoObject> csvSource, string delimiter)
    {
        if (delimiter == ",")
        {
            return;
        }

        try
        {
            var settingsProp = typeof(CsvSource<ExpandoObject>).GetProperty("Settings");
            if (settingsProp?.GetValue(csvSource) is object settings)
            {
                var delimProp = settings.GetType().GetProperty("Delimiter");
                if (delimProp?.CanWrite == true)
                {
                    delimProp.SetValue(settings, delimiter);
                    return;
                }
            }

            var directProp = typeof(CsvSource<ExpandoObject>).GetProperty("Delimiter");
            if (directProp?.CanWrite == true)
            {
                directProp.SetValue(csvSource, delimiter);
            }
        }
        catch
        {
            // intentionally no-op; caller relies on header validation and pipeline errors
        }
    }
}
