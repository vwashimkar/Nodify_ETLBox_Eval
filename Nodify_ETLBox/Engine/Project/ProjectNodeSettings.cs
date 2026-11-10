using System.Text.Json;
using System.Text.Json.Nodes;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Intake;
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
    private static readonly HashSet<string> KnownOptional = new() { RelativePath, "subPartColumn", LayoutPlan };

    /// <summary>File intake: the saved layout (an <c>IntakePlan</c> as JSON). Absent when the layout is automatic.</summary>
    public const string LayoutPlan = "layoutPlan";

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

    /// <summary>
    /// A File intake node: path, relativePath, mode, encoding and maxFileSizeMb, and the saved layout
    /// (<paramref name="layoutPlanJson"/>) as one JSON string when there is one (projects before G5c1
    /// hold it as a JSON object; both are read). The layout is written as it is and never merged with the loaded one.
    /// </summary>
    public static JsonElement WriteFileIntake(FileIntakeNodeSettings settings, string? relativePath, JsonElement? loaded = null, string? layoutPlanJson = null)
    {
        var json = new JsonObject { ["path"] = settings.Path };
        if (relativePath is not null)
        {
            json[RelativePath] = relativePath;
        }

        json["mode"] = settings.Mode;
        json["encoding"] = settings.Encoding;
        json["maxFileSizeMb"] = settings.MaxFileSizeMb;
        var finished = Finish(json, loaded);
        if (layoutPlanJson is null)
        {
            return finished;
        }

        var withPlan = JsonNode.Parse(finished.GetRawText())!.AsObject();
        // One JSON string, so the project writer's indentation does not grow the layout (G5c1).
        withPlan[LayoutPlan] = JsonValue.Create(layoutPlanJson);
        return JsonSerializer.SerializeToElement(withPlan);
    }

    /// <summary>
    /// The saved layout of a File intake node as JSON text, or null when there is none. It is not
    /// checked here: a layout that cannot be read never stops a project from loading (Intake Studio
    /// says so when it scans).
    /// </summary>
    public static string? ReadLayoutPlan(JsonElement settings)
        => settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(LayoutPlan, out var plan) && plan.ValueKind != JsonValueKind.Null
            ? plan.ValueKind == JsonValueKind.String ? plan.GetString() : plan.GetRawText()
            : null;

    /// <summary>
    /// A missing property takes its default. A mode or encoding this version does not know, or a
    /// size limit that is not a whole number above 0, throws a <see cref="JsonException"/>, so the
    /// node is kept in the file as an unknown node instead of losing the value.
    /// </summary>
    public static (FileIntakeNodeSettings Settings, string? RelativePath) ReadFileIntake(JsonElement settings)
    {
        var defaults = FileIntakeNodeSettings.Default;
        string Text(string name, string fallback)
            => !settings.TryGetProperty(name, out var e) ? fallback
                : e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new JsonException($"'{name}' must be text");

        var mode = Text("mode", defaults.Mode);
        if (!FileIntakeNodeSettings.Modes.Contains(mode))
        {
            throw new JsonException($"the mode '{mode}' is not known");
        }

        var encoding = IntakeEncodings.Normalize(Text("encoding", defaults.Encoding))
            ?? throw new JsonException("the encoding is not known");

        var size = defaults.MaxFileSizeMb;
        if (settings.TryGetProperty("maxFileSizeMb", out var sizeElement)
            && (sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt32(out size) || size < 1))
        {
            throw new JsonException("'maxFileSizeMb' must be a whole number above 0");
        }

        return (new FileIntakeNodeSettings { Path = Text("path", ""), Mode = mode, Encoding = encoding, MaxFileSizeMb = size },
            ReadRelativePath(settings));
    }

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
