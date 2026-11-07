using System.Globalization;

namespace EtlDesigner.Engine.Intake;

/// <summary>
/// What the fields of one row look like for one delimiter. <see cref="Count"/> leaves out trailing
/// empty fields (<c>a,b,,,</c> has 2); <see cref="RawCount"/> counts them.
/// </summary>
internal readonly record struct FieldSummary(int Count, bool HasNumeric, bool FirstEmpty, bool FirstIsLabel, bool OthersAllText, int RawCount);

/// <summary>
/// One logical row of a file for the auto-detector: one line, or several lines when a quoted field
/// spans them. The text is kept only while the row is in the detector's look-ahead window.
/// </summary>
internal sealed class Row
{
    public long FirstLine { get; init; }

    public long LastLine { get; init; }

    public long StartByte { get; init; }

    public long EndByte { get; init; }

    public string Text { get; init; } = string.Empty;

    /// <summary>The line was longer than the indexer's head, so only its start was looked at.</summary>
    public bool Cut { get; init; }

    /// <summary>Only delimiters, spaces and quotes: it may sit inside a region but may not start or end one.</summary>
    public bool IsWeak { get; init; }

    public FieldSummary[] Fields { get; init; } = [];

    public KeyValueForm? Form { get; init; }

    /// <summary>The whole row is one JSON value; <see cref="JsonObject"/> when it is an object.</summary>
    public bool JsonValue { get; init; }

    public bool JsonObject { get; init; }

    public long LineCount => LastLine - FirstLine + 1;
}

/// <summary>Pure functions that describe a row's text: field counts, key-value forms, weak lines.</summary>
internal static class RowShape
{
    public static FieldSummary[] Summaries(string text, IReadOnlyList<char> delimiters)
    {
        var result = new FieldSummary[delimiters.Count];
        for (var i = 0; i < delimiters.Count; i++)
        {
            result[i] = Summary(Split(text, delimiters[i]));
        }

        return result;
    }

    public static FieldSummary Summary(List<string> fields)
    {
        var hasNumeric = false;
        var othersAllText = fields.Count > 1;
        var anyOther = false;
        for (var i = 0; i < fields.Count; i++)
        {
            var numeric = IsNumeric(fields[i]);
            hasNumeric |= numeric;
            if (i > 0 && fields[i].Length > 0)
            {
                anyOther = true;
                othersAllText &= !numeric;
            }
        }

        var count = fields.Count;
        while (count > 1 && fields[count - 1].Length == 0)
        {
            count--;
        }

        var first = fields[0];
        return new FieldSummary(count, hasNumeric, first.Length == 0, first.Length > 0 && !IsNumeric(first), othersAllText && anyOther, fields.Count);
    }

    /// <summary>
    /// Splits on a delimiter. A field that starts with a quote runs to the closing quote; two quotes
    /// inside are one. The fields come back unquoted and trimmed. A quote that is never closed runs
    /// to the end of the text.
    /// </summary>
    public static List<string> Split(string text, char delimiter)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var i = 0;
        while (true)
        {
            field.Clear();
            if (i < text.Length && text[i] == '"')
            {
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                    }
                    else if (text[i] == '"')
                    {
                        i++;
                        break;
                    }
                    else
                    {
                        field.Append(text[i++]);
                    }
                }
            }

            while (i < text.Length && text[i] != delimiter)
            {
                field.Append(text[i++]);
            }

            fields.Add(field.ToString().Trim());
            if (i >= text.Length)
            {
                return fields;
            }

            i++;
        }
    }

    /// <summary>
    /// True when a quoted field is still open at the end of the text. A quote opens a field only at
    /// the start of the text or right after one of the delimiters, so it does not depend on which
    /// delimiter the table later gets [U: chosen].
    /// </summary>
    public static bool EndsInsideQuotes(string text, IReadOnlyList<char> delimiters)
    {
        var inQuotes = false;
        var fieldStart = true;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }

                continue;
            }

            if (c == '"' && fieldStart)
            {
                inQuotes = true;
                fieldStart = false;
                continue;
            }

            fieldStart = delimiters.Contains(c);
        }

        return inQuotes;
    }

    public static bool IsWeak(string text, IReadOnlyList<char> delimiters)
    {
        var any = false;
        foreach (var c in text)
        {
            if (c is ' ' or '"')
            {
                continue;
            }

            if (!delimiters.Contains(c))
            {
                return false;
            }

            any = true;
        }

        return any || text.Contains('"');
    }

    /// <summary>The whitespace-separated tokens of a line, for whitespace-aligned tables.</summary>
    public static string[] Tokens(string text) => text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A header of a whitespace-aligned table: only text, or consecutive integers (column numbers).</summary>
    public static bool IsTokenHeader(string[] tokens) => tokens.All(t => !IsNumeric(t)) || IsConsecutiveIntegers(tokens);

    public static bool IsConsecutiveIntegers(string[] tokens)
    {
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!int.TryParse(tokens[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || (i > 0 && n != int.Parse(tokens[i - 1], CultureInfo.InvariantCulture) + 1))
            {
                return false;
            }
        }

        return tokens.Length > 0;
    }

    public static bool IsNumeric(string field)
        => field.Length > 0 && double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>The key-value form of a line, or null. Several pairs win over one; an indented line is never key-value (that is nesting).</summary>
    public static KeyValueForm? FormOf(string text, FieldSummary[] fields, IReadOnlyList<char> delimiters, int maxKeyLength)
    {
        if (text.Length == 0 || char.IsWhiteSpace(text[0]))
        {
            return null;
        }

        if (text[0] == '"')
        {
            var comma = IndexOf(delimiters, ',');
            return comma >= 0 && fields[comma].Count >= 2 && fields[comma].FirstIsLabel ? KeyValueForm.Quoted : null;
        }

        var pairs = Split(text, ',').Count(part => IsColonPair(part, maxKeyLength));
        if (pairs >= 2)
        {
            return KeyValueForm.Pairs;
        }

        if (IsColonPair(text, maxKeyLength))
        {
            return KeyValueForm.Colon;
        }

        if (IsEqualsPair(text, maxKeyLength))
        {
            return KeyValueForm.EqualsSign;
        }

        var tab = IndexOf(delimiters, '\t');
        return tab >= 0 && fields[tab].Count >= 2 && fields[tab].FirstIsLabel ? KeyValueForm.Tab : null;
    }

    /// <summary>
    /// A section separator: one of <paramref name="characters"/> repeated, at least
    /// <paramref name="minLength"/> times in all, optionally with a name in the middle
    /// (<c>====Data====</c>). The name is null when there is none. It starts in the first column: an
    /// indented line is an example inside text, not a separator [U: chosen].
    /// </summary>
    public static bool IsSeparator(string text, string characters, int minLength, int maxNameLength, out string? name)
    {
        name = null;
        var trimmed = text.TrimEnd();
        if (trimmed.Length < minLength || !characters.Contains(trimmed[0]))
        {
            return false;
        }

        var c = trimmed[0];
        var lead = 0;
        while (lead < trimmed.Length && trimmed[lead] == c)
        {
            lead++;
        }

        if (lead == trimmed.Length)
        {
            return true;
        }

        var trail = 0;
        while (trail < trimmed.Length - lead && trimmed[trimmed.Length - 1 - trail] == c)
        {
            trail++;
        }

        // A name needs the character on its left at least twice, so a line like "-5" or "# Note" is not a separator [U: chosen].
        var middle = trimmed[lead..^trail].Trim();
        if (lead < 2 || lead + trail < minLength || middle.Length == 0 || middle.Length >= maxNameLength)
        {
            return false;
        }

        name = middle;
        return true;
    }

    /// <summary>Colon lines and lines of several colon pairs belong in one key-value block [U: chosen]; other forms only with themselves.</summary>
    public static bool SameKeyValueFamily(KeyValueForm? form, KeyValueForm block)
        => form == block || (form is KeyValueForm.Colon or KeyValueForm.Pairs && block is KeyValueForm.Colon or KeyValueForm.Pairs);

    private static int IndexOf(IReadOnlyList<char> delimiters, char c)
    {
        for (var i = 0; i < delimiters.Count; i++)
        {
            if (delimiters[i] == c)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary><c>Name: value</c>: a short key that starts with a letter, then a colon and a space or the end.</summary>
    private static bool IsColonPair(string text, int maxKeyLength)
    {
        var colon = text.IndexOf(':');
        if (colon <= 0 || (colon + 1 < text.Length && text[colon + 1] is not (' ' or '\t')))
        {
            return false;
        }

        return IsKey(text[..colon].Trim(), maxKeyLength);
    }

    /// <summary><c>Name = value</c>, also with a leading <c>* </c>.</summary>
    private static bool IsEqualsPair(string text, int maxKeyLength)
    {
        var body = text.StartsWith("* ", StringComparison.Ordinal) ? text[2..] : text;
        var equals = body.IndexOf('=');
        return equals > 0 && IsKey(body[..equals].Trim(), maxKeyLength);
    }

    private static bool IsKey(string key, int maxKeyLength)
    {
        if (key.Length == 0 || key.Length > maxKeyLength || !char.IsLetter(key[0]))
        {
            return false;
        }

        foreach (var c in key)
        {
            if (c is ',' or ';' or '|' or '\t' or '"' or '{' or '}' or '<' or '>' or '=' or '[' or ']')
            {
                return false;
            }
        }

        return true;
    }
}
