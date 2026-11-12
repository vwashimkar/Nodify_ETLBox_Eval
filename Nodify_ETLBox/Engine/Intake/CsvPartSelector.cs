using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EtlDesigner.Engine.Intake;

/// <summary>Limits of the CSV part selector and locator (data, not code constants).</summary>
public sealed record CsvPartSettings
{
    /// <summary>A list of column names in a message is cut to this many characters.</summary>
    public int MaxNamesInMessage { get; init; } = 120;

    /// <summary>The header of a part is looked for in at most this many lines after its skip lines.</summary>
    public int MaxSkipLines { get; init; } = 1000;
}

/// <summary>What the pipeline expects of its CSV part: the final check on any file.</summary>
public sealed record CsvPartExpected(string Delimiter, bool HasHeader, int SkipLines, int ColumnCount, IReadOnlyList<string> ColumnNames);

/// <summary>
/// How a pipeline finds its CSV part in a new file (G6 design section 4): the scan's mode and
/// encoding setting, the user's layout as a self-contained template, the index of the chosen part in
/// it, what that part must look like, and, for the self-check, where it was in the example file.
/// </summary>
public sealed record CsvPartSelector(
    int FormatVersion,
    IntakeMode Mode,
    string Encoding,
    IntakeTemplate Template,
    int PartIndex,
    CsvPartExpected Expected,
    string ExampleSha256,
    long ExampleFirstLine,
    long ExampleLastLine)
{
    public const int CurrentFormatVersion = 1;

    /// <summary>The chosen part's name for messages ("part N" when it has none).</summary>
    [JsonIgnore]
    public string PartName => Template.Parts[PartIndex].Name is { Length: > 0 } name ? IntakeEditRules.Cut(name) : $"part {PartIndex + 1}";

    /// <summary>
    /// The selector of container <paramref name="containerIndex"/> of the user's CURRENT layout. It
    /// must be a csv or tsv table with a one-character delimiter; otherwise null and one plain sentence.
    /// <paramref name="encoding"/> is the File intake node's encoding setting (default auto), so a new
    /// file is read the way the example was set up, not with the example's guessed encoding.
    /// </summary>
    public static CsvPartSelector? FromSession(IntakeEditSession session, int containerIndex, out string message,
        string encoding = IntakeEncodings.Auto, CsvPartSettings? settings = null, TemplateSettings? templateSettings = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        settings ??= new CsvPartSettings();
        if (containerIndex < 0 || containerIndex >= session.Containers.Count)
        {
            message = "Choose a part of the file first.";
            return null;
        }

        var c = session.Containers[containerIndex];
        var number = containerIndex + 1;
        if (c.Format is not ("csv" or "tsv"))
        {
            message = $"Part {number} is not a CSV or TSV table, so a pipeline cannot read it; choose a table or change its format first.";
            return null;
        }

        if (c.Delimiter is not { Length: 1 } || c.ContentStartByte is null || c.ContentEndByte is null || c.ContentFirstLine is null || c.ContentLastLine is null)
        {
            message = $"Part {number} has no known delimiter or no lines; set its delimiter in Intake Studio first.";
            return null;
        }

        var head = CsvPartHead.Read(session.Item, c, session.EncodingName, settings);
        if (head.Error is not null)
        {
            message = head.Error;
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(session.FileName);
        name = IntakeEditRules.NameProblem(name) is null ? IntakeEditRules.Cut(name.Trim()) : "Pipeline layout";
        var template = session.ToTemplate(name, templateSettings);
        message = "";
        return new CsvPartSelector(CurrentFormatVersion, session.Mode, encoding, template, containerIndex,
            new CsvPartExpected(c.Delimiter, c.HeaderGuess == HeaderGuess.Yes, c.SkipLines, head.Names.Count, head.Names),
            session.Sha256, c.ContentFirstLine.Value, c.ContentLastLine.Value);
    }
}

/// <summary>
/// The columns of a csv or tsv part as the pipeline will see them: the fields of the first line after
/// the skip lines (quote-aware, trimmed, outer quotes removed), or Col1..ColN without a header.
/// Reads only the first lines of the part.
/// </summary>
internal static class CsvPartHead
{
    public static (IReadOnlyList<string> Names, string? Error) Read(IntakeItem item, ContainerInfo part, string encodingName, CsvPartSettings settings)
    {
        if (part.SkipLines > settings.MaxSkipLines)
        {
            return ([], $"The part has more than {settings.MaxSkipLines.ToString("N0", CultureInfo.InvariantCulture)} lines to skip; reduce its skip lines in Intake Studio.");
        }

        var lines = IntakeReader.ReadLines(item, part.ContentStartByte!.Value, part.ContentEndByte!.Value, part.SkipLines + 1, encodingName);
        if (lines.Error is not null)
        {
            return ([], lines.Error);
        }

        if (lines.Lines.Count <= part.SkipLines)
        {
            return ([], "The part has no line after its skip lines, so it has no columns.");
        }

        var fields = PartFacts.Fields(lines.Lines[part.SkipLines], part.Delimiter![0]);
        return part.HeaderGuess == HeaderGuess.Yes
            ? (fields.Select(f => f.Trim().Trim('"').Trim()).ToList(), null)
            : (Enumerable.Range(1, fields.Count).Select(i => $"Col{i}").ToList(), null);
    }
}

/// <summary>Reads and writes a <see cref="CsvPartSelector"/> as JSON (formatVersion 1).</summary>
public static class CsvPartSelectorJson
{
    public const string UnreadableMessage = "The pipeline's choice of CSV part could not be read; choose the part again in Intake Studio.";

    public const string NewerMessage = "The pipeline's choice of CSV part was made by a newer version of the tool.";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(CsvPartSelector selector) => JsonSerializer.Serialize(selector, Options);

    /// <summary>The selector, or null with <see cref="UnreadableMessage"/> or <see cref="NewerMessage"/>.</summary>
    public static CsvPartSelector? Deserialize(string json, out string? error)
    {
        error = UnreadableMessage;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("formatVersion", out var version) || !version.TryGetInt32(out var number) || number < 1)
            {
                return null;
            }

            if (number > CsvPartSelector.CurrentFormatVersion)
            {
                error = NewerMessage;
                return null;
            }

            // The template is checked by its own reader.
            if (!root.TryGetProperty("template", out var templateJson) || IntakeTemplateJson.Deserialize(templateJson.GetRawText(), out _) is null)
            {
                return null;
            }

            var selector = root.Deserialize<CsvPartSelector>(Options);
            if (selector is null || selector.Template is null || selector.Expected is not { Delimiter.Length: 1, ColumnNames: not null }
                || selector.PartIndex < 0 || selector.PartIndex >= selector.Template.Parts.Count
                || string.IsNullOrEmpty(selector.ExampleSha256) || IntakeEncodings.Normalize(selector.Encoding) is null)
            {
                return null;
            }

            error = null;
            return selector;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
