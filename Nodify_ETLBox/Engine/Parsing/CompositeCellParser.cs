namespace EtlDesigner.Engine.Parsing;

/// <summary>
/// One component of a cell. <see cref="SubParts"/> has one entry per Each level, in level
/// order; a missing sub-part is an empty string.
/// </summary>
public sealed record ParsedComponent(string Name, IReadOnlyList<string> SubParts);

/// <summary>Result of parsing one cell. When <see cref="Error"/> is set, Key is null and there are no components.</summary>
public sealed record ParsedCell(string? Key, IReadOnlyList<ParsedComponent> Components, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>
/// Splits a composite cell (spec section 5). A pure function: no ETLBox, no WPF, no state.
/// </summary>
public static class CompositeCellParser
{
    public const string EmptyValue = "empty value";
    public const string NoKeyDelimiter = "no key delimiter";
    public const string EmptyKey = "empty key";

    /// <summary>
    /// A cell with a key but no components ("S1:"). Not named in section 5 or in the expected
    /// files; chosen here and marked unverified [U] in the Task B report.
    /// </summary>
    public const string NoComponents = "no components";

    public static ParsedCell Parse(string? cell, IReadOnlyList<DelimiterLevel> levels, bool trim, bool dropEmpty)
    {
        ValidateLevels(levels);

        var text = cell ?? string.Empty;
        if ((trim ? text.Trim() : text).Length == 0)
        {
            return Fail(EmptyValue);
        }

        var keyDelimiter = levels[0].Delimiter;
        var keyEnd = text.IndexOf(keyDelimiter, StringComparison.Ordinal);
        if (keyEnd < 0)
        {
            return Fail(NoKeyDelimiter);
        }

        var key = Clean(text[..keyEnd], trim);
        if (key.Length == 0)
        {
            return Fail(EmptyKey);
        }

        var rest = text[(keyEnd + keyDelimiter.Length)..];
        var eachLevels = levels.Skip(2).ToList();
        var components = new List<ParsedComponent>();

        foreach (var rawItem in rest.Split(levels[1].Delimiter, StringSplitOptions.None))
        {
            var item = Clean(rawItem, trim);
            if (dropEmpty && item.Length == 0)
            {
                continue;
            }

            components.Add(SplitSubParts(item, eachLevels, trim));
        }

        return components.Count == 0
            ? Fail(NoComponents)
            : new ParsedCell(key, components, null);
    }

    /// <summary>
    /// Each Each level splits what is left at its first delimiter: the left part stays, the right
    /// part moves on to the next level. With one level that is "name/sub". A component without
    /// the delimiter gets an empty sub-part. Chaining for more than one Each level is [U].
    /// </summary>
    private static ParsedComponent SplitSubParts(string item, IReadOnlyList<DelimiterLevel> eachLevels, bool trim)
    {
        if (eachLevels.Count == 0)
        {
            return new ParsedComponent(item, Array.Empty<string>());
        }

        var parts = new List<string>(eachLevels.Count + 1);
        var current = item;
        foreach (var level in eachLevels)
        {
            var at = current.IndexOf(level.Delimiter, StringComparison.Ordinal);
            if (at < 0)
            {
                parts.Add(current);
                current = string.Empty;
                continue;
            }

            parts.Add(Clean(current[..at], trim));
            current = Clean(current[(at + level.Delimiter.Length)..], trim);
        }

        parts.Add(current);
        return new ParsedComponent(parts[0], parts.Skip(1).ToList());
    }

    private static void ValidateLevels(IReadOnlyList<DelimiterLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);

        if (levels.Count < 2 || levels[0].Mode != SplitMode.Once || levels[1].Mode != SplitMode.All)
        {
            throw new ArgumentException("Level 1 must split once (key) and level 2 must split all (components).", nameof(levels));
        }

        if (levels.Skip(2).Any(l => l.Mode != SplitMode.Each || string.IsNullOrWhiteSpace(l.SubPartColumn)))
        {
            throw new ArgumentException("Levels after the second must split each component and name their output column.", nameof(levels));
        }

        if (levels.Any(l => string.IsNullOrEmpty(l.Delimiter)))
        {
            throw new ArgumentException("Every delimiter level needs a delimiter.", nameof(levels));
        }
    }

    private static string Clean(string value, bool trim) => trim ? value.Trim() : value;

    private static ParsedCell Fail(string reason) => new(null, Array.Empty<ParsedComponent>(), reason);
}
