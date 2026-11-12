using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EtlDesigner.Engine.Parsing;

namespace EtlDesigner.Engine.Pipeline;

/// <summary>
/// Splits one column at one separator into named parts (task G7a, the guided builder). Wide: the parts replace
/// the column at its place, every other kept column stays. Values are trimmed. A value with more pieces than
/// part names is rejected; one with fewer gets empty parts and is flagged; an empty value gives empty parts.
/// </summary>
public sealed record ColumnSplitSettings(string Column, string Separator, IReadOnlyList<string> PartNames)
{
    /// <summary>Part1, Part2, ... for <paramref name="count"/> parts.</summary>
    public static IReadOnlyList<string> DefaultPartNames(int count)
        => Enumerable.Range(1, Math.Max(count, 0)).Select(i => "Part" + i.ToString(CultureInfo.InvariantCulture)).ToList();
}

/// <summary>
/// The steps of a guided pipeline (CSV part -> DotNetObject, task G7a): no ParseValue, and an optional
/// <see cref="Split"/>. A definition with it ignores its ParseValue settings (<see cref="UnusedParseValue"/>).
/// </summary>
public sealed record GuidedSteps(ColumnSplitSettings? Split)
{
    /// <summary>The ParseValue settings a guided definition carries because the record needs some; never run and never written.</summary>
    public static ParseValueSettings UnusedParseValue { get; } = new() { SourceColumn = "", Slots = Array.Empty<Slot>() };
}

public static class ColumnSplit
{
    public const int MaxParts = 50;

    /// <summary>The reject reason of a value with more pieces than part names.</summary>
    public static string TooManyReason(string column, int pieces, int names)
        => $"column '{column}' has {pieces} parts, but only {names} part names are set";

    /// <summary>The flag reason of a value with fewer pieces than part names.</summary>
    public static string TooFewReason(string column, int pieces, int names)
        => $"column '{column}' has {pieces} part{(pieces == 1 ? "" : "s")}, fewer than the {names} part names; the rest are empty";

    /// <summary>
    /// Plain sentences for settings that cannot run; empty when they can. <paramref name="keptColumns"/>, when known,
    /// enables the checks that the column exists and that no part name repeats a kept column.
    /// </summary>
    public static IReadOnlyList<string> Validate(ColumnSplitSettings split, IReadOnlyList<string>? keptColumns = null)
    {
        ArgumentNullException.ThrowIfNull(split);
        var messages = new List<string>();
        if (string.IsNullOrWhiteSpace(split.Column))
        {
            messages.Add("Choose the column to split.");
        }
        else if (keptColumns is not null && !keptColumns.Contains(split.Column, StringComparer.Ordinal))
        {
            messages.Add($"The column '{split.Column}' is not among the kept columns; keep it or choose another column.");
        }

        if (string.IsNullOrEmpty(split.Separator))
        {
            messages.Add("Choose the separator to split at.");
        }

        var names = split.PartNames ?? Array.Empty<string>();
        if (names.Count < 2)
        {
            messages.Add("Give at least two part names.");
        }
        else if (names.Count > MaxParts)
        {
            messages.Add($"Give at most {MaxParts} part names.");
        }

        if (names.Any(string.IsNullOrWhiteSpace))
        {
            messages.Add("Every part needs a name.");
        }
        else if (names.Select(n => n.Trim()).Distinct(StringComparer.Ordinal).Count() < names.Count)
        {
            messages.Add("Every part needs a different name.");
        }
        else if (keptColumns is not null && names.Select(n => n.Trim()).FirstOrDefault(n => keptColumns.Contains(n, StringComparer.Ordinal) && n != split.Column) is { } clash)
        {
            messages.Add($"The part name '{clash}' is already a column; choose another name.");
        }

        return messages;
    }

    /// <summary>The output columns: <paramref name="keptColumns"/> with the split column replaced by the part names.</summary>
    public static IReadOnlyList<string> OutputColumns(IReadOnlyList<string> keptColumns, ColumnSplitSettings split)
    {
        var columns = new List<string>(keptColumns.Count + split.PartNames.Count);
        foreach (var column in keptColumns)
        {
            if (column == split.Column)
            {
                columns.AddRange(split.PartNames.Select(n => n.Trim()));
            }
            else
            {
                columns.Add(column);
            }
        }

        return columns;
    }

    /// <summary>The pieces of one value, trimmed.</summary>
    public static IReadOnlyList<string> Pieces(string? value, string separator)
        => string.IsNullOrEmpty(value) ? Array.Empty<string>() : value.Split(separator).Select(p => p.Trim()).ToList();

    /// <summary>
    /// The step of a guided pipeline for one record: the kept values, with the split applied when there is one.
    /// </summary>
    /// <exception cref="ArgumentException">The settings cannot run on these columns (the first sentence of <see cref="Validate"/>).</exception>
    public static (Func<ParseValueRecord, RecordOutcome> Process, IReadOnlyList<string> Columns) Prepare(IReadOnlyList<string> keptColumns, GuidedSteps steps)
    {
        ArgumentNullException.ThrowIfNull(keptColumns);
        ArgumentNullException.ThrowIfNull(steps);
        var noFlags = Array.Empty<RecordIssue>();
        if (steps.Split is not { } split)
        {
            return (record => new RecordOutcome([keptColumns.Select(c => record.Values.GetValueOrDefault(c) ?? string.Empty).ToList()], null, noFlags),
                keptColumns);
        }

        if (Validate(split, keptColumns) is { Count: > 0 } problems)
        {
            throw new ArgumentException(problems[0]);
        }

        var names = split.PartNames.Count;
        return (record =>
        {
            var pieces = Pieces(record.Values.GetValueOrDefault(split.Column), split.Separator);
            if (pieces.Count > names)
            {
                return new RecordOutcome([], new RecordIssue(record.RecordNumber, TooManyReason(split.Column, pieces.Count, names)), noFlags);
            }

            var row = new List<string>(keptColumns.Count + names);
            foreach (var column in keptColumns)
            {
                if (column == split.Column)
                {
                    row.AddRange(Enumerable.Range(0, names).Select(i => i < pieces.Count ? pieces[i] : string.Empty));
                }
                else
                {
                    row.Add(record.Values.GetValueOrDefault(column) ?? string.Empty);
                }
            }

            IReadOnlyList<RecordIssue> flags = pieces.Count is > 0 && pieces.Count < names
                ? [new RecordIssue(record.RecordNumber, TooFewReason(split.Column, pieces.Count, names))]
                : noFlags;
            return new RecordOutcome([row], null, flags);
        }, OutputColumns(keptColumns, split));
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>A split as JSON for comparing two splits by value; null for none.</summary>
    public static string? SerializeOrNull(ColumnSplitSettings? split) => split is null ? null : Serialize(new GuidedSteps(split));

    /// <summary>The guided steps as JSON: <c>{}</c> without a split, else <c>{"split":{"column":..,"separator":..,"partNames":[..]}}</c>.</summary>
    public static string Serialize(GuidedSteps steps) => JsonSerializer.Serialize(steps, Options);

    /// <exception cref="JsonException">The text is not guided steps.</exception>
    public static GuidedSteps Deserialize(string json)
    {
        var steps = JsonSerializer.Deserialize<GuidedSteps>(json, Options) ?? throw new JsonException("The guided steps are empty.");
        if (steps.Split is { } split && (split.Column is null || split.Separator is null || split.PartNames is null))
        {
            throw new JsonException("The column split is incomplete.");
        }

        return steps;
    }
}
