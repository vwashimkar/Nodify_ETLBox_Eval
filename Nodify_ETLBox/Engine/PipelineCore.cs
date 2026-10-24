using System.Dynamic;
using System.IO;
using System.Text.Json;
using ETLBox;
using ETLBox.Csv;
using ETLBox.DataFlow;
using ETLBox.Json;

namespace EtlDesigner.Engine;

public record PipelineCoreResult(
    string FilteredJson,
    string ParsedJson,
    int SkippedMalformedCount,
    string? ErrorMessage,
    PipelineExecutionStats Stats,
    PipelineFailureNode FailureNode);

public static class PipelineCore
{
    public static PipelineCoreResult Run(PipelineSettings settings, string csvPath, string country)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(csvPath))
                return CreateError("CSV file path is required.", PipelineFailureNode.CsvSource);

            if (!File.Exists(csvPath))
                return CreateError($"CSV file not found: {csvPath}", PipelineFailureNode.CsvSource);

            if (string.IsNullOrEmpty(settings.Delimiter))
                return CreateError("Delimiter is required.", PipelineFailureNode.CsvSource);

            if (string.IsNullOrEmpty(settings.Separator))
                return CreateError("Separator is required.", PipelineFailureNode.CsvSource);

            if (string.IsNullOrWhiteSpace(settings.LocationColumn))
                return CreateError("Location column is required.", PipelineFailureNode.CsvSource);

            if (settings.ParseFields == null || settings.ParseFields.Count == 0)
                return CreateError("At least one parse field is required.", PipelineFailureNode.ParseJson);

            var actualDelimiter = NormalizeDelimiter(settings.Delimiter);
            if (actualDelimiter.Length != 1)
                return CreateError("Delimiter must be a single character.", PipelineFailureNode.CsvSource);

            var requiredColumns = settings.ParseFields
                .Concat(new[] { settings.LocationColumn })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string headerLine;
            try
            {
                using var reader = new StreamReader(csvPath);
                headerLine = reader.ReadLine() ?? string.Empty;
            }
            catch (Exception ex)
            {
                return CreateError($"Error reading CSV header: {ex.Message}", PipelineFailureNode.CsvSource);
            }

            if (string.IsNullOrWhiteSpace(headerLine))
                return CreateError("CSV file has no header", PipelineFailureNode.CsvSource);

            var headers = headerLine
                .Split(actualDelimiter[0])
                .Select(h => h.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var requiredColumn in requiredColumns)
            {
                if (!headers.Contains(requiredColumn))
                    return CreateError($"Missing required column: {requiredColumn}", PipelineFailureNode.CsvSource);
            }

            int rowsRead = 0;
            int rowsMatched = 0;
            int rowsNotMatched = 0;
            int skippedMalformedCount = 0;

            var csvSource = new CsvSource<ExpandoObject>(csvPath);
            ConfigureCsvSourceDelimiter(csvSource, actualDelimiter);

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

                if (string.IsNullOrWhiteSpace(country))
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

                if (string.Equals(rowCountry, country, StringComparison.OrdinalIgnoreCase))
                {
                    rowsMatched++;
                    return row;
                }

                rowsNotMatched++;
                return null;
            });

            var tempFilteredJson = Path.Combine(Path.GetTempPath(), $"pipeline_filtered_{Guid.NewGuid()}.json");
            var jsonDestination = new JsonDestination<ExpandoObject>(tempFilteredJson);

            try
            {
                csvSource.LinkTo(countingTap);
                countingTap.LinkTo(filterCountry);
                filterCountry.LinkTo(jsonDestination);

                var network1 = new Network(csvSource, countingTap, filterCountry, jsonDestination);
                network1.Execute();
            }
            catch (Exception ex)
            {
                TryDelete(tempFilteredJson);
                return CreateError($"Pipeline error: {ex.Message}", PipelineFailureNode.CsvSource,
                    rowsRead, rowsMatched, rowsNotMatched, skippedMalformedCount, 0);
            }

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
                var emptyStats = new PipelineExecutionStats(rowsRead, rowsMatched, rowsNotMatched, skippedMalformedCount, 0);
                return new PipelineCoreResult(filteredJson, "[]", skippedMalformedCount, null, emptyStats, PipelineFailureNode.Unknown);
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

            try
            {
                jsonSource.LinkTo(projectFields);
                projectFields.LinkTo(memoryDestination);

                var network2 = new Network(jsonSource, projectFields, memoryDestination);
                network2.Execute();
            }
            catch (Exception ex)
            {
                TryDelete(tempFilteredJson);
                return CreateError($"Pipeline error: {ex.Message}", PipelineFailureNode.ParseJson,
                    rowsRead, rowsMatched, rowsNotMatched, skippedMalformedCount, 0);
            }

            string parsedJson;
            try
            {
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
            }
            catch (Exception ex)
            {
                TryDelete(tempFilteredJson);
                return CreateError($"Pipeline error: {ex.Message}", PipelineFailureNode.ToJson,
                    rowsRead, rowsMatched, rowsNotMatched, skippedMalformedCount, memoryDestination.Data?.Count ?? 0);
            }

            TryDelete(tempFilteredJson);

            var stats = new PipelineExecutionStats(rowsRead, rowsMatched, rowsNotMatched, skippedMalformedCount, memoryDestination.Data?.Count ?? 0);
            return new PipelineCoreResult(filteredJson, parsedJson, skippedMalformedCount, null, stats, PipelineFailureNode.Unknown);
        }
        catch (Exception ex)
        {
            return CreateError($"Pipeline error: {ex.Message}", PipelineFailureNode.Unknown);
        }
    }

    public static CountryListResult ReadDistinctCountries(PipelineSettings settings, string csvPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(csvPath))
                return new CountryListResult(Array.Empty<string>(), 0, 0, "CSV file path is required.");

            if (!File.Exists(csvPath))
                return new CountryListResult(Array.Empty<string>(), 0, 0, $"CSV file not found: {csvPath}");

            if (string.IsNullOrWhiteSpace(settings.LocationColumn))
                return new CountryListResult(Array.Empty<string>(), 0, 0, "Location column is required.");

            if (string.IsNullOrEmpty(settings.Separator))
                return new CountryListResult(Array.Empty<string>(), 0, 0, "Separator is required.");

            var actualDelimiter = NormalizeDelimiter(settings.Delimiter);
            if (actualDelimiter.Length != 1)
                return new CountryListResult(Array.Empty<string>(), 0, 0, "Delimiter must be a single character.");

            string headerLine;
            try
            {
                using var reader = new StreamReader(csvPath);
                headerLine = reader.ReadLine() ?? string.Empty;
            }
            catch (Exception ex)
            {
                return new CountryListResult(Array.Empty<string>(), 0, 0, $"Error reading CSV header: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(headerLine))
                return new CountryListResult(Array.Empty<string>(), 0, 0, "CSV file has no header");

            var headers = headerLine
                .Split(actualDelimiter[0])
                .Select(h => h.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!headers.Contains(settings.LocationColumn))
                return new CountryListResult(Array.Empty<string>(), 0, 0, $"Missing required column: {settings.LocationColumn}");

            int rowsRead = 0;
            int rowsSkipped = 0;
            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var csvSource = new CsvSource<ExpandoObject>(csvPath);
            ConfigureCsvSourceDelimiter(csvSource, actualDelimiter);

            var collectCountries = new RowTransformation(row =>
            {
                rowsRead++;
                var dict = (IDictionary<string, object?>)row;

                if (!dict.TryGetValue(settings.LocationColumn, out var locationObj))
                {
                    rowsSkipped++;
                    return null;
                }

                var location = locationObj?.ToString() ?? string.Empty;
                var idx = location.IndexOf(settings.Separator, StringComparison.Ordinal);
                if (idx < 0)
                {
                    rowsSkipped++;
                    return null;
                }

                var c = location[..idx].Trim();
                if (string.IsNullOrEmpty(c))
                {
                    rowsSkipped++;
                    return null;
                }

                if (seen.Add(c))
                    ordered.Add(c);

                return null;
            });

            var sink = new VoidDestination();
            csvSource.LinkTo(collectCountries);
            collectCountries.LinkTo(sink);

            var network = new Network(csvSource, collectCountries, sink);
            network.Execute();

            var sorted = ordered.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            return new CountryListResult(sorted, rowsRead, rowsSkipped, null);
        }
        catch (Exception ex)
        {
            return new CountryListResult(Array.Empty<string>(), 0, 0, ex.Message);
        }
    }

    public static string NormalizeDelimiter(string delimiter)
        => delimiter == "\\t" ? "\t" : delimiter;

    public static void ConfigureCsvSourceDelimiter(CsvSource<ExpandoObject> csvSource, string delimiter)
    {
        if (delimiter == ",")
            return;

        csvSource.Configuration.Delimiter = delimiter;
    }

    private static PipelineCoreResult CreateError(
        string message,
        PipelineFailureNode failureNode,
        int rowsRead = 0,
        int rowsMatched = 0,
        int rowsNotMatched = 0,
        int rowsSkipped = 0,
        int parsedCount = 0)
    {
        var stats = new PipelineExecutionStats(rowsRead, rowsMatched, rowsNotMatched, rowsSkipped, parsedCount);
        return new PipelineCoreResult("[]", "[]", rowsSkipped, message, stats, failureNode);
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
}
