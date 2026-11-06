using System.Text.Json;
using System.Text.Json.Serialization;

namespace EtlDesigner.Engine.Project;

/// <summary>
/// One saved project (a .etlproj file). The property names are the file format, written out here
/// so that renaming a C# property cannot change it. Properties this version does not know land in
/// Extra and are written back, so a file from another version loses nothing.
/// </summary>
public sealed record ProjectFile
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; } = ProjectSerializer.CurrentFormatVersion;

    [JsonPropertyName("savedUtc")]
    public DateTime SavedUtc { get; init; }

    [JsonPropertyName("toolVersion")]
    public string ToolVersion { get; init; } = string.Empty;

    [JsonPropertyName("projectName")]
    public string ProjectName { get; init; } = string.Empty;

    [JsonPropertyName("nodes"), JsonRequired]
    public IReadOnlyList<ProjectNode> Nodes { get; init; } = [];

    [JsonPropertyName("connections")]
    public IReadOnlyList<ProjectConnection> Connections { get; init; } = [];

    [JsonPropertyName("view")]
    public ProjectView? View { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// One node. <see cref="Type"/> is a stable id from <see cref="ProjectNodeTypes"/>, never the
/// label. <see cref="Raw"/> is the node exactly as read; when set it is written back as it is,
/// which is how a node of an unknown type survives a save.
/// </summary>
public sealed record ProjectNode
{
    [JsonPropertyName("id"), JsonRequired]
    public Guid Id { get; init; }

    [JsonPropertyName("type"), JsonRequired]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("settings")]
    public JsonElement? Settings { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    [JsonIgnore]
    public JsonElement? Raw { get; init; }
}

/// <summary>One connection, by node id and connector key, not by position in a list.</summary>
public sealed record ProjectConnection
{
    [JsonPropertyName("fromNode"), JsonRequired]
    public Guid FromNode { get; init; }

    [JsonPropertyName("fromConnector"), JsonRequired]
    public string FromConnector { get; init; } = string.Empty;

    [JsonPropertyName("toNode"), JsonRequired]
    public Guid ToNode { get; init; }

    [JsonPropertyName("toConnector"), JsonRequired]
    public string ToConnector { get; init; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>The canvas zoom and offset.</summary>
public sealed record ProjectView
{
    [JsonPropertyName("zoom")]
    public double Zoom { get; init; } = 1;

    [JsonPropertyName("offsetX")]
    public double OffsetX { get; init; }

    [JsonPropertyName("offsetY")]
    public double OffsetY { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>The stable node type ids of the file format. They never change with a label.</summary>
public static class ProjectNodeTypes
{
    public const string CsvFile = "CsvFile";
    public const string ParseValue = "ParseValue";
    public const string DotNetObject = "DotNetObject";
    public const string CsvSource = "CsvSource";
    public const string FilterByCountry = "FilterByCountry";
    public const string ToJson = "ToJson";
    public const string ParseJson = "ParseJson";
    public const string ResultJson = "ResultJson";
}
