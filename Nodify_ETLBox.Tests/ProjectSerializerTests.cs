using System.Text.Json;
using System.Text.Json.Nodes;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.Engine.Project;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>The file format: no view models here, only records and JSON text.</summary>
public class ProjectSerializerTests
{
    private static readonly DateTime Saved = new(2026, 11, 6, 9, 30, 0, DateTimeKind.Utc);

    internal static readonly ParseValueSettings IrregularParseValue = new()
    {
        SourceColumn = "sample",
        DelimiterLevels = [new DelimiterLevel(":", SplitMode.Once), new DelimiterLevel("|", SplitMode.All), new DelimiterLevel("=", SplitMode.Each, "dose")],
        Trim = false,
        DropEmpty = false,
        Slots = [new Slot("S1", ["r01"]), new Slot("S2", ["r02", "r03"]), new Slot("S3", ["r05", "r07", "r09", "r11"])],
        ExcessPolicy = ExcessPolicy.PairWhatFits,
        DuplicatePolicy = DuplicatePolicy.Reject,
        CarryColumns = CarryColumns.Of("id", "batch")
    };

    private static ProjectNode Node(string type, JsonElement settings, double x = 10.5) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        Name = type,
        X = x,
        Y = 40,
        Settings = settings
    };

    private static JsonElement Legacy(params (string Key, string Value)[] values)
        => ProjectNodeSettings.WriteLegacy(values.Select(v => KeyValuePair.Create(v.Key, v.Value)), null);

    private static ProjectFile Project(params ProjectNode[] nodes) => new()
    {
        SavedUtc = Saved,
        ToolVersion = "1.0.0.0",
        ProjectName = "lab µg/l",
        Nodes = nodes,
        View = new ProjectView { Zoom = 0.75, OffsetX = -20, OffsetY = 12.5 }
    };

    public static TheoryData<string> NodeTypes() => new()
    {
        ProjectNodeTypes.CsvFile, ProjectNodeTypes.ParseValue, ProjectNodeTypes.DotNetObject, ProjectNodeTypes.CsvSource,
        ProjectNodeTypes.FilterByCountry, ProjectNodeTypes.ToJson, ProjectNodeTypes.ParseJson, ProjectNodeTypes.ResultJson
    };

    private static JsonElement SettingsFor(string type)
    {
        var selection = new CsvColumnSelection { Mode = CsvColumnSelectionMode.KeepOnly };
        selection.Names.AddRange(["id", "sample", "r01"]);

        return type switch
        {
            ProjectNodeTypes.CsvFile => ProjectNodeSettings.WriteCsvFile(new CsvFileSettings(@"C:\lab\in.csv", ";", false, 3, "windows-1252", 20), selection, "../in.csv"),
            ProjectNodeTypes.ParseValue => ProjectNodeSettings.WriteParseValue(IrregularParseValue),
            ProjectNodeTypes.DotNetObject => ProjectNodeSettings.WriteOutput(OutputSettings.Default),
            ProjectNodeTypes.CsvSource => Legacy(("FilePath", "employee_list.csv"), ("Delimiter", ",")),
            ProjectNodeTypes.FilterByCountry => Legacy(("LocationColumn", "location"), ("Separator", "|"), ("Country", "United States")),
            ProjectNodeTypes.ParseJson => Legacy(("Fields", "first_name,last_name,email")),
            _ => Legacy()
        };
    }

    [Theory]
    [MemberData(nameof(NodeTypes))]
    public void EveryNodeType_RoundTrips_ByteForByte(string type)
    {
        var text = ProjectSerializer.Serialize(Project(Node(type, SettingsFor(type))));

        var loaded = ProjectSerializer.Deserialize(text);

        Assert.Equal(type, loaded.Nodes.Single().Type);
        Assert.Equal(10.5, loaded.Nodes.Single().X);
        Assert.Equal(text, ProjectSerializer.Serialize(loaded));
    }

    [Fact]
    public void CsvFileSettings_ReadBack_WithSelectionAndRelativePath()
    {
        var (settings, columns, relative) = ProjectNodeSettings.ReadCsvFile(SettingsFor(ProjectNodeTypes.CsvFile));

        Assert.Equal(new CsvFileSettings(@"C:\lab\in.csv", ";", false, 3, "windows-1252", 20), settings);
        Assert.Equal(CsvColumnSelectionMode.KeepOnly, columns.Mode);
        Assert.Equal(["id", "sample", "r01"], columns.Names);
        Assert.Equal("../in.csv", relative);
    }

    [Fact]
    public void ParseValueSettings_ReadBack_WithIrregularSlotsAndEachLevel()
    {
        var read = ProjectNodeSettings.ReadParseValue(SettingsFor(ProjectNodeTypes.ParseValue));

        Assert.Equal(IrregularParseValue.DelimiterLevels, read.DelimiterLevels);
        Assert.Equal(new[] { 1, 2, 4 }, read.Slots.Select(s => s.Columns.Count));
        Assert.Equal(["r05", "r07", "r09", "r11"], read.Slots[2].Columns);
        Assert.Equal(ExcessPolicy.PairWhatFits, read.ExcessPolicy);
        Assert.Equal(["id", "batch"], read.CarryColumns.Names);
    }

    [Fact]
    public void File_HasTheEnvelope_IndentedCamelCase_EnumsAsStrings()
    {
        var text = ProjectSerializer.Serialize(Project(Node(ProjectNodeTypes.ParseValue, SettingsFor(ProjectNodeTypes.ParseValue))));
        var root = JsonNode.Parse(text)!.AsObject();

        Assert.Equal(["formatVersion", "savedUtc", "toolVersion", "projectName", "nodes", "connections", "view"], root.Select(p => p.Key));
        Assert.Equal(["id", "type", "name", "x", "y", "settings"], root["nodes"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal("PairWhatFits", (string?)root["nodes"]![0]!["settings"]!["excessPolicy"]);
        Assert.Contains("\n  \"nodes\"", text);
        Assert.Contains("lab µg/l", text);
    }

    [Fact]
    public void UnknownNodeType_IsKept_AndWrittenBackExactly()
    {
        var id = Guid.NewGuid();
        var text = $$"""
            {
              "formatVersion": 1,
              "savedUtc": "2026-11-06T09:30:00Z",
              "toolVersion": "1.0.0.0",
              "projectName": "p",
              "nodes": [
                {
                  "id": "{{id}}",
                  "type": "RestSource",
                  "name": "Weather",
                  "x": 120.0,
                  "y": 4.50,
                  "settings": {
                    "url": "https://example.test/a?b=1",
                    "pages": [
                      1,
                      2.0
                    ]
                  },
                  "colour": "blue"
                }
              ],
              "connections": []
            }
            """.ReplaceLineEndings("\n");

        var loaded = ProjectSerializer.Deserialize(text);

        Assert.Equal("RestSource", loaded.Nodes[0].Type);
        Assert.Equal(text, ProjectSerializer.Serialize(loaded));
    }

    [Fact]
    public void UnknownExtraProperties_AreKept_OnEveryKnownObject()
    {
        var project = Project(Node(ProjectNodeTypes.ToJson, Legacy()));
        var json = JsonNode.Parse(ProjectSerializer.Serialize(project))!.AsObject();
        json["reviewedBy"] = "QA";
        json["view"]!["grid"] = true;
        json["connections"] = JsonNode.Parse($$"""[{ "fromNode": "{{Guid.NewGuid()}}", "fromConnector": "Rows", "toNode": "{{Guid.NewGuid()}}", "toConnector": "Rows", "label": "main" }]""");

        var written = JsonNode.Parse(ProjectSerializer.Serialize(ProjectSerializer.Deserialize(json.ToJsonString())))!;

        Assert.Equal("QA", (string?)written["reviewedBy"]);
        Assert.True((bool)written["view"]!["grid"]!);
        Assert.Equal("main", (string?)written["connections"]![0]!["label"]);
    }

    [Fact]
    public void UnknownSettingsProperties_AreKept_WhenTheSettingsAreWrittenAgain()
    {
        var loaded = JsonNode.Parse(SettingsFor(ProjectNodeTypes.ParseValue).GetRawText())!.AsObject();
        loaded["futureOption"] = 7;
        loaded["slots"]![1]!["unit"] = "mg";
        var settings = JsonSerializer.SerializeToElement(loaded);

        var written = JsonNode.Parse(ProjectNodeSettings.WriteParseValue(IrregularParseValue with { Trim = true }, settings).GetRawText())!;

        Assert.Equal(7, (int)written["futureOption"]!);
        Assert.Equal("mg", (string?)written["slots"]![1]!["unit"]);
        Assert.True((bool)written["trim"]!);
    }

    [Fact]
    public void ClearedOptionalValues_DoNotComeBackFromTheLoadedSettings()
    {
        var loaded = SettingsFor(ProjectNodeTypes.CsvFile);
        var noRelative = ProjectNodeSettings.WriteCsvFile(new CsvFileSettings("x.csv", ",", true, 0, "utf-8"), new CsvColumnSelection(), null, loaded);
        Assert.Null(ProjectNodeSettings.ReadRelativePath(noRelative));

        var levelCleared = IrregularParseValue with { DelimiterLevels = [new(":", SplitMode.Once), new("|", SplitMode.All), new("=", SplitMode.Each)] };
        var written = ProjectNodeSettings.WriteParseValue(levelCleared, SettingsFor(ProjectNodeTypes.ParseValue));
        Assert.Null(ProjectNodeSettings.ReadParseValue(written).DelimiterLevels[2].SubPartColumn);
    }

    [Fact]
    public void NewerVersion_IsRefused_WithTheMessage()
    {
        var text = ProjectSerializer.Serialize(Project()).Replace("\"formatVersion\": 1", "\"formatVersion\": 2");

        var ex = Assert.Throws<ProjectLoadException>(() => ProjectSerializer.Deserialize(text));

        Assert.Equal(ProjectSerializer.NewerVersionMessage, ex.Message);
    }

    [Fact]
    public void OlderVersion_GoesThroughMigrate_AndCurrentDoesNot()
    {
        var current = ProjectSerializer.Serialize(Project());
        var older = current.Replace("\"formatVersion\": 1", "\"formatVersion\": 0");
        var calls = new List<int>();
        JsonObject Spy(JsonObject project, int fromVersion)
        {
            calls.Add(fromVersion);
            project["projectName"] = "migrated";
            return project;
        }

        var loaded = ProjectSerializer.Deserialize(older, Spy);
        Assert.Equal([0], calls);
        Assert.Equal("migrated", loaded.ProjectName);
        Assert.Equal(ProjectSerializer.CurrentFormatVersion, loaded.FormatVersion);

        ProjectSerializer.Deserialize(current, Spy);
        Assert.Equal([0], calls);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   \n ", "empty")]
    [InlineData("[1, 2]", "does not contain a project")]
    [InlineData("{ \"projectName\": \"x\" }", "formatVersion")]
    [InlineData("{ \"formatVersion\": \"one\" }", "formatVersion")]
    public void BadContent_GivesAClearMessage(string text, string expected)
    {
        var ex = Assert.Throws<ProjectLoadException>(() => ProjectSerializer.Deserialize(text));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void InvalidJson_GivesTheLineNumber()
    {
        var ex = Assert.Throws<ProjectLoadException>(() => ProjectSerializer.Deserialize("{\n  \"formatVersion\": 1,\n  oops\n}"));

        Assert.Contains("(line 3)", ex.Message);
        Assert.DoesNotContain("BytePosition", ex.Message);
    }

    [Fact]
    public void ANodeThatIsNotValid_GivesTheLineNumber()
    {
        var text = "{\n  \"formatVersion\": 1,\n  \"nodes\": [\n    { \"id\": \"not-a-guid\", \"type\": \"ToJson\" }\n  ]\n}";

        var ex = Assert.Throws<ProjectLoadException>(() => ProjectSerializer.Deserialize(text));

        Assert.Contains("(line 4)", ex.Message);
    }

    [Fact]
    public void SameNodeIdTwice_IsRefused()
    {
        var node = Node(ProjectNodeTypes.ToJson, Legacy());

        var ex = Assert.Throws<ProjectLoadException>(() => ProjectSerializer.Deserialize(ProjectSerializer.Serialize(Project(node, node))));

        Assert.Contains("appears twice", ex.Message);
    }
}
