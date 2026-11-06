using System.Text.Json;
using System.Text.Json.Nodes;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;

namespace EtlDesigner.Engine.Project;

/// <summary>
/// The "settings" object of a node, per node type, built on the <see cref="SettingsJson"/>
/// serializers. Every writer takes the settings as they were loaded, so that properties this
/// version does not know are written back.
/// </summary>
public static class ProjectNodeSettings
{
    private const string RelativePath = "relativePath";
    private const string Columns = "columns";

    /// <summary>Optional properties the serializers leave out when empty. They are known, so never kept from the old file.</summary>
    private static readonly HashSet<string> KnownOptional = new() { RelativePath, "subPartColumn" };

    public static JsonElement WriteCsvFile(CsvFileSettings settings, CsvColumnSelection columns, string? relativePath, JsonElement? loaded = null)
    {
        var json = JsonNode.Parse(SettingsJson.Serialize(settings))!.AsObject();
        if (relativePath is not null)
        {
            json.Insert(1, RelativePath, relativePath);
        }

        json[Columns] = JsonNode.Parse(SettingsJson.Serialize(columns));
        return Finish(json, loaded);
    }

    public static (CsvFileSettings Settings, CsvColumnSelection Columns, string? RelativePath) ReadCsvFile(JsonElement settings)
    {
        var columns = settings.TryGetProperty(Columns, out var element)
            ? SettingsJson.DeserializeColumnSelection(element.GetRawText())
            : new CsvColumnSelection();

        return (SettingsJson.DeserializeCsvFileSettings(settings.GetRawText()), columns, ReadRelativePath(settings));
    }

    public static JsonElement WriteParseValue(ParseValueSettings settings, JsonElement? loaded = null)
        => Finish(JsonNode.Parse(SettingsJson.Serialize(settings))!.AsObject(), loaded);

    public static ParseValueSettings ReadParseValue(JsonElement settings)
        => SettingsJson.DeserializeParseValueSettings(settings.GetRawText());

    public static JsonElement WriteOutput(OutputSettings settings, JsonElement? loaded = null)
        => Finish(JsonNode.Parse(SettingsJson.Serialize(settings))!.AsObject(), loaded);

    public static OutputSettings ReadOutput(JsonElement settings)
        => SettingsJson.DeserializeOutputSettings(settings.GetRawText());

    /// <summary>A legacy node: a plain key/value object with the keys of its setting rows.</summary>
    public static JsonElement WriteLegacy(IEnumerable<KeyValuePair<string, string>> values, string? relativePath, JsonElement? loaded = null)
    {
        var json = new JsonObject();
        foreach (var (key, value) in values)
        {
            json[key] = value;
        }

        if (relativePath is not null)
        {
            json[RelativePath] = relativePath;
        }

        return Finish(json, loaded);
    }

    public static IReadOnlyDictionary<string, string> ReadLegacy(JsonElement settings)
        => settings.EnumerateObject()
            .Where(p => p.Name != RelativePath)
            .ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText());

    public static string? ReadRelativePath(JsonElement settings)
        => settings.TryGetProperty(RelativePath, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static JsonElement Finish(JsonObject json, JsonElement? loaded)
    {
        if (loaded is { ValueKind: JsonValueKind.Object } original)
        {
            KeepUnknown(json, JsonNode.Parse(original.GetRawText())!.AsObject());
        }

        return JsonSerializer.SerializeToElement(json);
    }

    private static void KeepUnknown(JsonObject current, JsonObject loaded)
    {
        foreach (var (key, value) in loaded)
        {
            if (!current.TryGetPropertyValue(key, out var existing))
            {
                if (!KnownOptional.Contains(key))
                {
                    current[key] = value?.DeepClone();
                }
            }
            else if (existing is JsonObject currentChild && value is JsonObject loadedChild)
            {
                KeepUnknown(currentChild, loadedChild);
            }
            else if (existing is JsonArray currentItems && value is JsonArray loadedItems && currentItems.Count == loadedItems.Count)
            {
                for (var i = 0; i < currentItems.Count; i++)
                {
                    if (currentItems[i] is JsonObject currentItem && loadedItems[i] is JsonObject loadedItem)
                    {
                        KeepUnknown(currentItem, loadedItem);
                    }
                }
            }
        }
    }
}
