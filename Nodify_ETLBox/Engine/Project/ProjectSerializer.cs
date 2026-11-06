using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace EtlDesigner.Engine.Project;

/// <summary>A file that cannot be opened. The message says why and is meant for the user.</summary>
public sealed class ProjectLoadException : Exception
{
    public ProjectLoadException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>Brings the JSON of an older format version up to the current one.</summary>
public delegate JsonObject ProjectMigrator(JsonObject project, int fromVersion);

/// <summary>Project files as JSON: UTF-8, indented, camelCase, enums as strings.</summary>
public static class ProjectSerializer
{
    public const int CurrentFormatVersion = 1;

    public const string FileExtension = ".etlproj";

    public const string NewerVersionMessage = "This project was saved by a newer version of the tool. Update the tool to open it.";

    private const string CheckTheFile = "Check the file, or restore the .bak copy next to it.";

    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Lab column names (µg, °C) and paths stay readable in the file.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ToolVersion { get; } = typeof(ProjectSerializer).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    public static string Serialize(ProjectFile project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var root = JsonSerializer.SerializeToNode(project, Options)!.AsObject();
        var nodes = root["nodes"]!.AsArray();
        for (var i = 0; i < project.Nodes.Count; i++)
        {
            if (project.Nodes[i].Raw is { } raw)
            {
                nodes[i] = JsonSerializer.SerializeToNode(raw, Options);
            }
        }

        return root.ToJsonString(Options);
    }

    /// <summary>
    /// Reads a project. Throws <see cref="ProjectLoadException"/> with a message for the user when
    /// the text is empty, not valid JSON, not a project, or from a newer format version.
    /// </summary>
    public static ProjectFile Deserialize(string text, ProjectMigrator? migrate = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ProjectLoadException($"The project file is empty. {CheckTheFile}");
        }

        try
        {
            var json = Upgrade(text, migrate ?? ProjectMigrations.Migrate);
            var project = JsonSerializer.Deserialize<ProjectFile>(json, Options)
                ?? throw new ProjectLoadException($"The file does not contain a project. {CheckTheFile}");

            var duplicate = project.Nodes.GroupBy(n => n.Id).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
            {
                throw new ProjectLoadException($"The node id {duplicate.Key} appears twice in the project file. {CheckTheFile}");
            }

            return project with { Nodes = WithRawJson(project.Nodes, json) };
        }
        catch (JsonException ex)
        {
            throw new ProjectLoadException(DescribeJsonError(ex), ex);
        }
    }

    /// <summary>The text of the current format version, after the version checks and Migrate.</summary>
    private static string Upgrade(string text, ProjectMigrator migrate)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ProjectLoadException($"The file does not contain a project. {CheckTheFile}");
        }

        if (!root.TryGetProperty("formatVersion", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out var version)
            || version < 0)
        {
            throw new ProjectLoadException($"The file has no valid formatVersion, so it is not a project file. {CheckTheFile}");
        }

        if (version > CurrentFormatVersion)
        {
            throw new ProjectLoadException(NewerVersionMessage);
        }

        if (version == CurrentFormatVersion)
        {
            return text;
        }

        var migrated = migrate(JsonNode.Parse(text)!.AsObject(), version);
        migrated["formatVersion"] = CurrentFormatVersion;
        return migrated.ToJsonString(Options);
    }

    private static List<ProjectNode> WithRawJson(IReadOnlyList<ProjectNode> nodes, string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("nodes").EnumerateArray()
            .Select((element, i) => nodes[i] with { Raw = element.Clone() })
            .ToList();
    }

    /// <summary>The reason without the technical path, with the line number when there is one.</summary>
    private static string DescribeJsonError(JsonException ex)
    {
        var reason = ex.Message;
        foreach (var marker in new[] { " Path:", " LineNumber:" })
        {
            var index = reason.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                reason = reason[..index];
            }
        }

        var line = ex.LineNumber is { } number ? $" (line {number + 1})" : string.Empty;
        return $"The project file could not be read: {reason.TrimEnd().TrimEnd('.')}{line}. {CheckTheFile}";
    }
}

/// <summary>
/// One step per old format version goes here, each bringing the JSON one version up. There are
/// none yet: version 1 is the first.
/// </summary>
public static class ProjectMigrations
{
    public static JsonObject Migrate(JsonObject project, int fromVersion) => project;
}
