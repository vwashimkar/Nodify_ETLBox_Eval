using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EtlDesigner.Engine.Intake;

/// <summary>The limits and thresholds of layout templates. Data, so they can come from configuration later.</summary>
public sealed record TemplateSettings
{
    /// <summary>The folder of the template files; null means <see cref="TemplateLocation.DefaultFolder"/>.</summary>
    public string? Folder { get; init; }

    public int MaxTemplates { get; init; } = 500;

    /// <summary>A template file larger than this is not read, and a template that would be larger is not saved.</summary>
    public int MaxFileKb { get; init; } = 1024;

    /// <summary>A similarity at or above this (and below 1) is a Close match.</summary>
    public double CloseThreshold { get; init; } = 0.8;

    /// <summary>A run of at least this many equal consecutive signatures (or groups of signatures) is one fingerprint entry marked "repeats".</summary>
    public int MinRepeatRun { get; init; } = 2;

    /// <summary>The longest group of signatures that is looked for as a repeating unit.</summary>
    public int MaxRepeatPeriod { get; init; } = 32;

    /// <summary>The most candidates <see cref="IntakeTemplateMatcher.Match"/> returns.</summary>
    public int MaxCandidates { get; init; } = 3;

    /// <summary>Two fingerprints larger than this together (entries x entries) are compared only for equality.</summary>
    public long MaxCompareCells { get; init; } = 4_000_000;

    /// <summary>An anchor (the first line of a part) is cut to this many characters.</summary>
    public int MaxAnchorLength { get; init; } = 80;

    /// <summary>
    /// When fewer than this share of the column names (of table parts with a header on both sides)
    /// are equal, a structural match is downgraded: Exact becomes Close, similarity at most 0.9.
    /// </summary>
    public double ColumnNameMatchMin { get; init; } = 0.5;

    /// <summary>The most column names kept per table part.</summary>
    public int MaxColumnNames { get; init; } = 64;

    /// <summary>A column name is cut to this many characters.</summary>
    public int MaxColumnNameLength { get; init; } = 40;
}

/// <summary>Where template files are kept.</summary>
public static class TemplateLocation
{
    public const string FolderName = "templates";

    /// <summary><c>templates</c> in <paramref name="baseFolder"/>, or in the folder of the running program.</summary>
    public static string DefaultFolder(string? baseFolder = null) => Path.Combine(baseFolder ?? AppContext.BaseDirectory, FolderName);

    /// <summary>The folder used when the default one cannot be written: <c>%AppData%\EtlDesigner\templates</c>.</summary>
    public static string FallbackFolder() => Path.Combine(LineageLocation.FallbackFolder(), FolderName);
}

/// <summary>
/// The structure of one part: its format, its delimiter (tables and key-value), and for csv and tsv
/// tables the number of columns of the first row after the skip lines (null when unknown) and the
/// header guess. Never row counts, values, lines or offsets.
/// </summary>
public sealed record PartSignature(string Format, string? Delimiter = null, int? Columns = null, bool? Header = null)
{
    public override string ToString()
    {
        var text = new StringBuilder(Format);
        if (Delimiter is not null || Columns is not null || Header is not null)
        {
            text.Append('[').Append(Delimiter == "\t" ? "tab" : Delimiter).Append('|').Append(Columns?.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(Header switch { true => "h", false => "-", null => "" }).Append(']');
        }

        return text.ToString();
    }
}

/// <summary>One entry of a fingerprint: one signature, or a group of signatures, that occurs once or repeats.</summary>
public sealed record FingerprintEntry(IReadOnlyList<PartSignature> Group, bool Repeats)
{
    public override string ToString()
        => (Group.Count == 1 ? Group[0].ToString() : "(" + string.Join(" ", Group) + ")") + (Repeats ? "*" : "");
}

/// <summary>
/// The structure of a layout: the ordered part signatures, with runs of equal consecutive
/// signatures, or of equal consecutive groups of signatures, collapsed into one entry marked
/// "repeats". It does not depend on row counts, values, line numbers, file name, folder, size,
/// hash or date. Two fingerprints are equal when their <see cref="Text"/> is equal.
/// </summary>
public sealed record IntakeFingerprint(IReadOnlyList<FingerprintEntry> Entries)
{
    public string Text => string.Join(" ", Entries);

    public override string ToString() => Text;

    /// <summary>
    /// Collapses repeats: at each position the repeating unit (of at most <see cref="TemplateSettings.MaxRepeatPeriod"/>
    /// signatures) that covers the most signatures, at least <see cref="TemplateSettings.MinRepeatRun"/> times,
    /// becomes one entry; the shortest unit wins a tie.
    /// </summary>
    public static IntakeFingerprint Of(IReadOnlyList<PartSignature> layout, TemplateSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        settings ??= new TemplateSettings();
        var minRun = Math.Max(2, settings.MinRepeatRun);
        var entries = new List<FingerprintEntry>();
        var i = 0;
        while (i < layout.Count)
        {
            var (bestPeriod, bestCount) = (1, 1);
            for (var p = 1; p <= settings.MaxRepeatPeriod && i + p * minRun <= layout.Count; p++)
            {
                var count = 1;
                while (i + (count + 1) * p <= layout.Count && SameRun(layout, i, i + count * p, p))
                {
                    count++;
                }

                if (count >= minRun && p * count > bestPeriod * bestCount)
                {
                    (bestPeriod, bestCount) = (p, count);
                }
            }

            entries.Add(new FingerprintEntry(layout.Skip(i).Take(bestPeriod).ToList(), bestCount > 1));
            i += bestPeriod * bestCount;
        }

        return new IntakeFingerprint(entries);
    }

    /// <summary>The fingerprint of containers of <paramref name="item"/>; reads only the first lines of csv and tsv parts (for the column counts).</summary>
    public static IntakeFingerprint Of(IntakeItem item, IReadOnlyList<ContainerInfo> layout, string encodingName, TemplateSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(layout);
        var tables = layout.Select(c => c.Format is "csv" or "tsv" ? c : c with { ContentStartByte = null }).ToList();
        var facts = PartFacts.Read(item, tables, encodingName);
        return Of(layout.Select((c, i) => PartFacts.Signature(c, facts[i].Columns)).ToList(), settings);
    }

    /// <summary>
    /// 0.0 to 1.0: twice the best in-order matching score divided by the number of entries of both
    /// (a Dice coefficient over an ordered alignment). Two entries score 1 when equal; 0.5 when
    /// they are single parts of the same format and delimiter whose columns or header differ; 0.25
    /// less when only one of them repeats; else 0. Equal fingerprints give 1, empty ones 1 when both
    /// are empty. Fingerprints too large to align (<see cref="TemplateSettings.MaxCompareCells"/>) give 1 or 0.
    /// </summary>
    public static double Similarity(IntakeFingerprint a, IntakeFingerprint b, TemplateSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        settings ??= new TemplateSettings();
        if (a.Text == b.Text)
        {
            return 1.0;
        }

        int n = a.Entries.Count, m = b.Entries.Count;
        if (n == 0 || m == 0 || (long)n * m > settings.MaxCompareCells)
        {
            return 0.0;
        }

        var previous = new double[m + 1];
        var current = new double[m + 1];
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var score = Score(a.Entries[i - 1], b.Entries[j - 1]);
                current[j] = Math.Max(Math.Max(previous[j], current[j - 1]), score > 0 ? previous[j - 1] + score : 0);
            }

            (previous, current) = (current, previous);
        }

        return Math.Round(2 * previous[m] / (n + m), 4);
    }

    private static double Score(FingerprintEntry x, FingerprintEntry y)
    {
        double score;
        if (x.Group.Count == 1 && y.Group.Count == 1)
        {
            PartSignature s = x.Group[0], t = y.Group[0];
            score = s.Format != t.Format || s.Delimiter != t.Delimiter ? 0 : s == t ? 1 : 0.5;
        }
        else
        {
            score = x.Group.SequenceEqual(y.Group) ? 1 : 0;
        }

        return score == 0 ? 0 : x.Repeats == y.Repeats ? score : score - 0.25;
    }

    private static bool SameRun(IReadOnlyList<PartSignature> layout, int a, int b, int length)
    {
        for (var k = 0; k < length; k++)
        {
            if (layout[a + k] != layout[b + k])
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// One part of an <see cref="IntakeTemplate"/>: a structural description of one part of a corrected
/// layout. <see cref="From"/> lists the signatures of the automatic parts it was made from (two or
/// more for a merge). <see cref="StartsInside"/> is true when it began inside an automatic part (a
/// split); it is then placed at the first line equal to <see cref="Anchor"/>. No line numbers, byte
/// offsets, file name, hash, size or row counts.
/// </summary>
public sealed record TemplatePart(
    string Format,
    string Name,
    string? Delimiter = null,
    HeaderGuess? Header = null,
    int SkipLines = 0,
    KeyValueForm? KeyValueForm = null,
    int? Columns = null,
    string? Anchor = null,
    IReadOnlyList<PartSignature>? From = null,
    bool StartsInside = false,
    IReadOnlyList<string>? ColumnNames = null);

/// <summary>
/// A corrected layout kept per instrument (plan section 6), recognised on a new file by its
/// <see cref="Fingerprint"/> (the structure of the automatic layout it was made from) and applied
/// by <see cref="IntakeEditSession.ApplyTemplate"/>.
/// </summary>
public sealed record IntakeTemplate(
    int FormatVersion,
    Guid Id,
    string Name,
    DateTime CreatedUtc,
    IntakeFingerprint Fingerprint,
    IReadOnlyList<TemplatePart> Parts,
    string? Notes = null)
{
    public const int CurrentFormatVersion = 1;
}

/// <summary>An <see cref="IntakeTemplate"/> as JSON: camelCase, enums as strings, defaults left out, indented so a person can read it.</summary>
public static class IntakeTemplateJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter() }
    };

    public const string UnreadableMessage = "The saved layout could not be read.";

    public static string Serialize(IntakeTemplate template) => JsonSerializer.Serialize(template, Options);

    /// <summary>The template, or null with the reason: <see cref="UnreadableMessage"/>, or a newer version.</summary>
    public static IntakeTemplate? Deserialize(string json, out string? error)
    {
        error = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("formatVersion", out var version) || !version.TryGetInt32(out var number) || number < 1)
            {
                error = UnreadableMessage;
                return null;
            }

            if (number > IntakeTemplate.CurrentFormatVersion)
            {
                error = "The saved layout was made by a newer version of the tool.";
                return null;
            }

            var template = document.RootElement.Deserialize<IntakeTemplate>(Options);
            if (template is null || template.Id == Guid.Empty || IntakeEditRules.NameProblem(template.Name) is not null
                || template.Fingerprint?.Entries is null || template.Parts is null
                || template.Parts.Any(p => p?.Format is null || p.Name is null)
                || template.Fingerprint.Entries.Any(e => e?.Group is null || e.Group.Count == 0 || e.Group.Any(s => s?.Format is null)))
            {
                error = UnreadableMessage;
                return null;
            }

            return template;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            error = UnreadableMessage;
            return null;
        }
    }
}

public enum TemplateMatch
{
    None,
    Close,
    Exact
}

/// <summary>
/// A template and how well its fingerprint fits a file's automatic layout. <see cref="NamesDiffer"/>
/// is true when the structure fits but the column names do not; <see cref="Reason"/> then says so in one sentence.
/// </summary>
public sealed record TemplateCandidate(IntakeTemplate Template, double Similarity, TemplateMatch Match, bool NamesDiffer = false, string? Reason = null);

/// <summary>Finds the templates whose structure fits the automatic layout of a new file.</summary>
public static class IntakeTemplateMatcher
{
    /// <summary>
    /// At most <see cref="TemplateSettings.MaxCandidates"/> candidates, best first (ties by name):
    /// Exact when the fingerprints are equal, Close at or above <see cref="TemplateSettings.CloseThreshold"/>,
    /// else None. <paramref name="scanLayout"/> must be the fingerprint of the detector's result, never of an edited layout.
    /// <paramref name="fileColumnNames"/> are the header names of the file's csv and tsv parts that have
    /// a header, in file order; when given, an Exact or Close candidate whose names differ (see
    /// <see cref="ColumnNamesAgree"/>) becomes Close with a similarity of at most 0.9.
    /// </summary>
    public static IReadOnlyList<TemplateCandidate> Match(IntakeFingerprint scanLayout, IEnumerable<IntakeTemplate> templates, TemplateSettings? settings = null,
        IReadOnlyList<IReadOnlyList<string>>? fileColumnNames = null)
    {
        ArgumentNullException.ThrowIfNull(scanLayout);
        ArgumentNullException.ThrowIfNull(templates);
        settings ??= new TemplateSettings();
        return templates
            .Select(t => (Template: t, Similarity: IntakeFingerprint.Similarity(scanLayout, t.Fingerprint, settings)))
            .Select(x => Candidate(x.Template, x.Similarity,
                x.Template.Fingerprint.Text == scanLayout.Text ? TemplateMatch.Exact : x.Similarity >= settings.CloseThreshold ? TemplateMatch.Close : TemplateMatch.None,
                fileColumnNames, settings))
            .OrderByDescending(c => c.Similarity).ThenBy(c => c.Template.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(0, settings.MaxCandidates))
            .ToList();
    }

    public const string NamesDifferReason = "The column names are different.";

    private static TemplateCandidate Candidate(IntakeTemplate template, double similarity, TemplateMatch match,
        IReadOnlyList<IReadOnlyList<string>>? fileColumnNames, TemplateSettings settings)
    {
        if (match == TemplateMatch.None || fileColumnNames is null || ColumnNamesAgree(template, fileColumnNames, settings))
        {
            return new TemplateCandidate(template, similarity, match);
        }

        return new TemplateCandidate(template, Math.Min(similarity, 0.9), TemplateMatch.Close, true, NamesDifferReason);
    }

    /// <summary>
    /// The k-th table part with names of the template is compared with the k-th of the file, name
    /// by name in column order (trimmed, case ignored). True when there is nothing to compare, or
    /// when the equal names are at least <see cref="TemplateSettings.ColumnNameMatchMin"/> of all
    /// compared positions (the longer list of each pair counts).
    /// </summary>
    public static bool ColumnNamesAgree(IntakeTemplate template, IReadOnlyList<IReadOnlyList<string>> fileColumnNames, TemplateSettings? settings = null)
    {
        settings ??= new TemplateSettings();
        var mine = template.Parts.Where(p => p.ColumnNames is { Count: > 0 }).Select(p => p.ColumnNames!).ToList();
        var theirs = fileColumnNames.Where(n => n is { Count: > 0 }).ToList();
        int equal = 0, total = 0;
        foreach (var (a, b) in mine.Zip(theirs))
        {
            total += Math.Max(a.Count, b.Count);
            equal += a.Zip(b).Count(p => string.Equals(p.First.Trim(), p.Second.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return total == 0 || (double)equal / total >= settings.ColumnNameMatchMin;
    }
}

/// <summary>
/// The facts of each part a template needs from the file: its first line (for the anchor) and, for
/// csv and tsv tables, the number of fields of the first line after the skip lines. Reads at most
/// <see cref="MaxHeadBytes"/> of each part through one read-only stream.
/// </summary>
internal static class PartFacts
{
    public const int MaxHeadBytes = 64 * 1024;

    /// <summary>Also the header names of csv and tsv parts whose header guess is Yes (cut by the settings' limits).</summary>
    public static (string? FirstLine, int? Columns, IReadOnlyList<string>? Names)[] Read(IntakeItem item, IReadOnlyList<ContainerInfo> parts, string encodingName,
        TemplateSettings? settings = null)
    {
        settings ??= new TemplateSettings();
        var facts = new (string?, int?, IReadOnlyList<string>?)[parts.Count];
        var encoding = IntakeReader.ResolveEncoding(encodingName, out _) ?? Encoding.UTF8;
        try
        {
            using var stream = item.OpenRead();
            var buffer = new byte[MaxHeadBytes];
            for (var i = 0; i < parts.Count; i++)
            {
                if (parts[i] is not { ContentStartByte: { } start, ContentEndByte: { } end } part)
                {
                    continue;
                }

                IntakeReader.MoveTo(stream, start);
                var length = stream.ReadAtLeast(buffer.AsSpan(0, (int)Math.Min(MaxHeadBytes, end - start)), (int)Math.Min(MaxHeadBytes, end - start), false);
                var lines = encoding.GetString(buffer, 0, length).TrimStart('﻿').Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
                var header = part.Format is "csv" or "tsv" && part.Delimiter is { Length: 1 } d && part.SkipLines < lines.Length - (end - start > length ? 1 : 0)
                    ? Fields(lines[part.SkipLines], d[0])
                    : null;
                var names = header is not null && part.HeaderGuess == HeaderGuess.Yes
                    ? header.Take(settings.MaxColumnNames).Select(n => n.Trim().Trim('"').Trim())
                        .Select(n => n.Length <= settings.MaxColumnNameLength ? n : n[..settings.MaxColumnNameLength]).ToList()
                    : null;
                facts[i] = (lines[0], header?.Count, names);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Facts that cannot be read stay unknown; a template never fails because of them.
        }

        return facts;
    }

    public static PartSignature Signature(ContainerInfo c, int? columns)
        => IntakeEditRules.IsTable(c.Format) || c.Format == "keyvalue"
            ? new PartSignature(c.Format, c.Delimiter, c.Format is "csv" or "tsv" ? columns : null,
                c.Format is "csv" or "tsv" ? c.HeaderGuess switch { HeaderGuess.Yes => true, HeaderGuess.No => false, _ => null } : null)
            : new PartSignature(c.Format);

    /// <summary>The fields of a line, quote-aware; trailing empty fields do not count (as in the detector).</summary>
    public static List<string> Fields(string line, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (ch == delimiter && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
                continue;
            }

            field.Append(ch);
        }

        fields.Add(field.ToString());
        while (fields.Count > 1 && string.IsNullOrWhiteSpace(fields[^1]))
        {
            fields.RemoveAt(fields.Count - 1);
        }

        return fields;
    }

    /// <summary>
    /// The anchor rule: the trimmed first line, cut to <paramref name="maxLength"/>, when it is not
    /// blank, has at least one letter, and is not a data row of the csv or tsv table before it (a
    /// line with that table's number of fields and at least one number among them).
    /// </summary>
    public static string? Anchor(string? firstLine, ContainerInfo? previous, int? previousColumns, int maxLength)
    {
        var text = firstLine?.Trim();
        if (string.IsNullOrEmpty(text) || !text.Any(char.IsLetter))
        {
            return null;
        }

        if (previous is { Format: "csv" or "tsv", Delimiter: { Length: 1 } d } && previousColumns is { } n)
        {
            var fields = Fields(text, d[0]);
            if (fields.Count == n && fields.Any(f => double.TryParse(f.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                return null;
            }
        }

        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
