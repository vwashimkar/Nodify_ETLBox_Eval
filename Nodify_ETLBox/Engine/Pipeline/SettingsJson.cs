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
public static class SettingsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    public static string Serialize(CsvFileSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.Serialize(new CsvFileJson
        {
            Path = settings.Path,
            Separator = settings.Separator,
            HasHeader = settings.HasHeader,
            SkipLines = settings.SkipLines,
            EncodingName = settings.EncodingName,
            PreviewRows = settings.PreviewRows
        }, Options);
    }

    public static CsvFileSettings DeserializeCsvFileSettings(string json)
    {
        var dto = Read<CsvFileJson>(json);
        return new CsvFileSettings(
            dto.Path ?? string.Empty,
            dto.Separator ?? ",",
            dto.HasHeader,
            dto.SkipLines,
            dto.EncodingName ?? "utf-8",
            dto.PreviewRows);
    }

    public static string Serialize(CsvColumnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return JsonSerializer.Serialize(new ColumnSelectionJson
        {
            Mode = selection.Mode,
            Names = selection.Names.ToList()
        }, Options);
    }

    public static CsvColumnSelection DeserializeColumnSelection(string json)
    {
        var dto = Read<ColumnSelectionJson>(json);
        var selection = new CsvColumnSelection { Mode = dto.Mode };
        selection.Names.AddRange(dto.Names ?? []);
        return selection;
    }

    public static string Serialize(ParseValueSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.Serialize(new ParseValueJson
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
        }, Options);
    }

    public static ParseValueSettings DeserializeParseValueSettings(string json)
    {
        var dto = Read<ParseValueJson>(json);
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
}
