using System.Text.Json;
using System.Text.Json.Serialization;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;

namespace EtlDesigner.Engine.Pipeline;

/// <summary>
/// Node settings as JSON (System.Text.Json). The settings will be saved in project files and
/// baked into the generated DLL, so the JSON is written from explicit shapes below, not from the
/// records directly: renaming a record property must not change the file format. Enums are
/// written as their names. Property names are camelCase.
/// </summary>
public static partial class SettingsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    public static string Serialize(CsvFileSettings settings)
        => JsonSerializer.Serialize(ToJson(settings), Options);

    public static CsvFileSettings DeserializeCsvFileSettings(string json)
        => FromJson(Read<CsvFileJson>(json));

    public static string Serialize(CsvColumnSelection selection)
        => JsonSerializer.Serialize(ToJson(selection), Options);

    public static CsvColumnSelection DeserializeColumnSelection(string json)
        => FromJson(Read<ColumnSelectionJson>(json));

    public static string Serialize(ParseValueSettings settings)
        => JsonSerializer.Serialize(ToJson(settings), Options);

    public static ParseValueSettings DeserializeParseValueSettings(string json)
        => FromJson(Read<ParseValueJson>(json));

    public static string Serialize(OutputSettings settings)
        => JsonSerializer.Serialize(ToJson(settings), Options);

    public static OutputSettings DeserializeOutputSettings(string json)
        => FromJson(Read<OutputJson>(json));

    /// <summary>The whole pipeline: one object with a version and one property per node.</summary>
    public static string Serialize(PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        JsonElement? part = null;
        WritePart(definition, ref part);
        JsonElement? guided = null;
        WriteGuided(definition, ref guided);
        return JsonSerializer.Serialize(new PipelineJson
        {
            Version = PipelineJson.CurrentVersion,
            CsvFile = ToJson(definition.CsvFile),
            Columns = ToJson(definition.Columns),
            // A guided pipeline runs no ParseValue, so it has none in its JSON.
            ParseValue = guided is null ? ToJson(definition.ParseValue) : null,
            Output = ToJson(definition.Output),
            CsvPart = part,
            Guided = guided
        }, Options);
    }

    public static PipelineDefinition DeserializePipelineDefinition(string json)
    {
        var dto = Read<PipelineJson>(json);
        var csvFile = FromJson(dto.CsvFile ?? throw new JsonException("The pipeline has no csvFile settings."));
        var columns = FromJson(dto.Columns ?? new ColumnSelectionJson());
        var output = FromJson(dto.Output ?? new OutputJson());
        PipelineDefinition? definition = null;
        if (dto.Guided is { } guided)
        {
            ReadGuided(guided, csvFile, columns, output, ref definition);
        }

        definition ??= new PipelineDefinition(csvFile, columns,
            FromJson(dto.ParseValue ?? throw new JsonException("The pipeline has no parseValue settings.")), output);
        if (dto.CsvPart is { } part)
        {
            ReadPart(part, ref definition);
        }

        return definition;
    }

    /// <summary>The CSV part selector as JSON; left null for a CsvFile pipeline (PipelineDefinition.Part.cs).</summary>
    static partial void WritePart(PipelineDefinition definition, ref JsonElement? part);

    /// <summary>Puts the CSV part selector into <paramref name="definition"/> (PipelineDefinition.Part.cs).</summary>
    static partial void ReadPart(JsonElement part, ref PipelineDefinition definition);

    /// <summary>The guided steps as JSON; left null for any other pipeline (PipelineDefinition.Part.cs).</summary>
    static partial void WriteGuided(PipelineDefinition definition, ref JsonElement? guided);

    /// <summary>Makes the guided definition of <paramref name="guided"/> (PipelineDefinition.Part.cs).</summary>
    static partial void ReadGuided(JsonElement guided, CsvFileSettings csvFile, CsvColumnSelection columns, OutputSettings output,
        ref PipelineDefinition? definition);

    private static CsvFileJson ToJson(CsvFileSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new CsvFileJson
        {
            Path = settings.Path,
            Separator = settings.Separator,
            HasHeader = settings.HasHeader,
            SkipLines = settings.SkipLines,
            EncodingName = settings.EncodingName,
            PreviewRows = settings.PreviewRows
        };
    }

    private static CsvFileSettings FromJson(CsvFileJson dto)
        => new(
            dto.Path ?? string.Empty,
            dto.Separator ?? ",",
            dto.HasHeader,
            dto.SkipLines,
            dto.EncodingName ?? "utf-8",
            dto.PreviewRows);

    private static ColumnSelectionJson ToJson(CsvColumnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return new ColumnSelectionJson { Mode = selection.Mode, Names = selection.Names.ToList() };
    }

    private static CsvColumnSelection FromJson(ColumnSelectionJson dto)
    {
        var selection = new CsvColumnSelection { Mode = dto.Mode };
        selection.Names.AddRange(dto.Names ?? []);
        return selection;
    }

    private static ParseValueJson ToJson(ParseValueSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ParseValueJson
        {
            SourceColumn = settings.SourceColumn,
            DelimiterLevels = settings.DelimiterLevels
                .Select(l => new DelimiterLevelJson { Delimiter = l.Delimiter, Mode = l.Mode, SubPartColumn = l.SubPartColumn })
                .ToList(),
            Trim = settings.Trim,
            DropEmpty = settings.DropEmpty,
            Slots = settings.Slots.Select(s => new SlotJson { Name = s.Name, Columns = s.Columns.ToList() }).ToList(),
            ExcessPolicy = settings.ExcessPolicy,
            DuplicatePolicy = settings.DuplicatePolicy,
            CarryColumns = new CarryColumnsJson
            {
                Mode = settings.CarryColumns.AllOtherColumns ? CarryMode.All
                    : settings.CarryColumns.Names.Count == 0 ? CarryMode.None
                    : CarryMode.List,
                Names = settings.CarryColumns.AllOtherColumns ? [] : settings.CarryColumns.Names.ToList()
            },
            OutputShape = settings.OutputShape
        };
    }

    private static ParseValueSettings FromJson(ParseValueJson dto)
    {
        var carry = dto.CarryColumns?.Mode switch
        {
            CarryMode.All => CarryColumns.All,
            CarryMode.List => CarryColumns.Of((dto.CarryColumns.Names ?? []).ToArray()),
            _ => CarryColumns.None
        };

        return new ParseValueSettings
        {
            SourceColumn = dto.SourceColumn ?? string.Empty,
            DelimiterLevels = dto.DelimiterLevels is null
                ? ParseValueSettings.DefaultLevels
                : dto.DelimiterLevels.Select(l => new DelimiterLevel(l.Delimiter ?? string.Empty, l.Mode, l.SubPartColumn)).ToList(),
            Trim = dto.Trim,
            DropEmpty = dto.DropEmpty,
            Slots = (dto.Slots ?? []).Select(s => new Slot(s.Name ?? string.Empty, s.Columns ?? [])).ToList(),
            ExcessPolicy = dto.ExcessPolicy,
            DuplicatePolicy = dto.DuplicatePolicy,
            CarryColumns = carry,
            OutputShape = dto.OutputShape
        };
    }

    private static OutputJson ToJson(OutputSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new OutputJson { Kind = settings.Kind };
    }

    private static OutputSettings FromJson(OutputJson dto) => new(dto.Kind);

    private static T Read<T>(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new JsonException($"The settings text is empty ({typeof(T).Name}).");
    }

    // The file format. Change these only on purpose: saved projects and generated DLLs read them.

    private sealed class CsvFileJson
    {
        public string? Path { get; set; }
        public string? Separator { get; set; }
        public bool HasHeader { get; set; } = true;
        public int SkipLines { get; set; }
        public string? EncodingName { get; set; }
        public int PreviewRows { get; set; } = 5;
    }

    private sealed class ColumnSelectionJson
    {
        public CsvColumnSelectionMode Mode { get; set; }
        public List<string>? Names { get; set; }
    }

    private enum CarryMode
    {
        None,
        All,
        List
    }

    private sealed class CarryColumnsJson
    {
        public CarryMode Mode { get; set; }
        public List<string>? Names { get; set; }
    }

    private sealed class DelimiterLevelJson
    {
        public string? Delimiter { get; set; }
        public SplitMode Mode { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SubPartColumn { get; set; }
    }

    private sealed class SlotJson
    {
        public string? Name { get; set; }
        public List<string>? Columns { get; set; }
    }

    private sealed class ParseValueJson
    {
        public string? SourceColumn { get; set; }
        public List<DelimiterLevelJson>? DelimiterLevels { get; set; }
        public bool Trim { get; set; } = true;
        public bool DropEmpty { get; set; } = true;
        public List<SlotJson>? Slots { get; set; }
        public ExcessPolicy ExcessPolicy { get; set; }
        public DuplicatePolicy DuplicatePolicy { get; set; }
        public CarryColumnsJson? CarryColumns { get; set; }
        public OutputShape OutputShape { get; set; }
    }

    private sealed class OutputJson
    {
        public ObjectKind Kind { get; set; } = ObjectKind.ListOfStringDictionary;
    }

    private sealed class PipelineJson
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; }
        public CsvFileJson? CsvFile { get; set; }
        public ColumnSelectionJson? Columns { get; set; }
        /// <summary>Left out for a guided pipeline (task G7a); every other pipeline has it, so their JSON is unchanged.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ParseValueJson? ParseValue { get; set; }

        public OutputJson? Output { get; set; }

        /// <summary>Only a CSV part pipeline has it; left out of the JSON otherwise, so a CsvFile pipeline's JSON is unchanged.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? CsvPart { get; set; }

        /// <summary>Only a guided pipeline has it (task G7a); left out otherwise.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? Guided { get; set; }
    }
}
