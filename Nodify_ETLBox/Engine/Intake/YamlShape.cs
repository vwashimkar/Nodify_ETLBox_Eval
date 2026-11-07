using System.Text.RegularExpressions;

namespace EtlDesigner.Engine.Intake;

/// <summary>
/// Whether lines have real YAML structure (plan section 4, rule 6). Plain <c>Name: value</c> lines,
/// prose and logs do not: they are key-value or text. The parser is asked only after this says yes.
/// </summary>
internal static partial class YamlShape
{
    /// <summary>A <c>key:</c> or <c>key: value</c> line (after its indentation): a key that does not start with a space, '#' or '-'.</summary>
    [GeneratedRegex(@"^[^\s#\-\[\{""'][^:#]*:(\s|$)")]
    private static partial Regex KeyLine();

    /// <summary>An anchor or alias as a value (<c>key: &amp;name</c>, <c>key: *name</c>) or as a list item.</summary>
    [GeneratedRegex(@"(:\s+|^-\s+)[&*][A-Za-z0-9_.-]+(\s|$)")]
    private static partial Regex AnchorOrAlias();

    /// <summary>A literal or folded block scalar after a key: <c>key: |</c>, <c>key: &gt;-</c>.</summary>
    [GeneratedRegex(@":\s+[|>][+-]?\d?\s*$")]
    private static partial Regex BlockScalar();

    /// <summary>
    /// The first structure found, in words, or null. Structure is one of: a <c>---</c> document
    /// marker or <c>%YAML</c> header; an anchor or alias; a block scalar; a <c>key:</c> line with a
    /// more indented <c>key:</c> line or list item below it (a wrapped sentence under it is not
    /// nesting) [U]; a <c>- </c> list item directly under a <c>key:</c> line, or one that holds a
    /// <c>key: value</c> pair [U].
    /// </summary>
    public static string? HasStructure(IReadOnlyList<string> lines)
    {
        string? previous = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var text = line.TrimStart();
            if (text.Length == 0 || text.StartsWith('#'))
            {
                continue;
            }

            if (text == "---" || text.StartsWith("--- ", StringComparison.Ordinal) || text.StartsWith("%YAML", StringComparison.Ordinal))
            {
                return "a --- document marker or a %YAML header";
            }

            if (AnchorOrAlias().IsMatch(text))
            {
                return "an anchor or an alias";
            }

            if (KeyLine().IsMatch(text) && BlockScalar().IsMatch(text))
            {
                return "a | or > block scalar";
            }

            if (text.StartsWith("- ", StringComparison.Ordinal) && (previous?.TrimEnd().EndsWith(':') == true || KeyLine().IsMatch(text[2..].TrimStart())))
            {
                return "a list of - items";
            }

            if (KeyLine().IsMatch(text) && text.TrimEnd().EndsWith(':') && NextContent(lines, i) is { } next)
            {
                var nextText = next.TrimStart();
                if (Indent(next) > Indent(line) && (KeyLine().IsMatch(nextText) || nextText.StartsWith("- ", StringComparison.Ordinal)))
                {
                    return "nesting by indentation under a key: line";
                }
            }

            previous = text;
        }

        return null;
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    private static string? NextContent(IReadOnlyList<string> lines, int from)
    {
        for (var j = from + 1; j < lines.Count; j++)
        {
            var text = lines[j].TrimStart();
            if (text.Length > 0 && !text.StartsWith('#'))
            {
                return lines[j];
            }
        }

        return null;
    }
}
