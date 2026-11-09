using System.Globalization;
using System.IO;

namespace EtlDesigner.Engine.Intake;

/// <summary>The limits of <see cref="IntakeEditSession"/>. Data, so they can come from configuration later.</summary>
public sealed record EditSettings
{
    /// <summary>The most steps that can be undone; older steps are forgotten.</summary>
    public int MaxUndo { get; init; } = 200;

    /// <summary>A container larger than this is not read for a split (or for the format check after a merge).</summary>
    public int MaxEditMb { get; init; } = 64;
}

/// <summary>The outcome of one edit. <see cref="Message"/> is one plain sentence; on failure nothing has changed.</summary>
public sealed record EditResult(bool Ok, string Message)
{
    public static EditResult Done { get; } = new(true, string.Empty);

    public static EditResult Fail(string message) => new(false, message);
}

/// <summary>The names and limits an edit (and a saved plan) must keep to.</summary>
public static class IntakeEditRules
{
    public const int MaxNameLength = 80;

    /// <summary>The formats a user can choose.</summary>
    public static IReadOnlyList<string> Formats { get; } = ["text", "json", "json-lines", "xml", "csv", "tsv", "keyvalue", "yaml", "fixedwidth"];

    /// <summary>The delimiter names a user can choose for a table, with the value the model keeps (the character, or "space" for fixedwidth).</summary>
    public static IReadOnlyList<string> DelimiterNames { get; } = ["comma", "semicolon", "pipe", "tab", "space"];

    public static bool IsTable(string format) => format is "csv" or "tsv" or "fixedwidth";

    public static string? FormatName(string? format)
        => format is null ? null : Formats.FirstOrDefault(f => string.Equals(f, format.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string UnknownFormatMessage(string? format)
        => $"'{Cut(format ?? string.Empty)}' is not a format the tool knows; choose one of {string.Join(", ", Formats)}.";

    /// <summary>The model's value for a delimiter name in a table of <paramref name="format"/>, or null when it is not allowed there.</summary>
    public static string? DelimiterValue(string format, string name)
    {
        var value = name.Trim().ToLowerInvariant() switch
        {
            "comma" => ",",
            "semicolon" => ";",
            "pipe" => "|",
            "tab" => "\t",
            "space" => format == "fixedwidth" ? "space" : " ",
            _ => null
        };
        return format == "fixedwidth" && value != "space" ? null : value;
    }

    /// <summary>True when <paramref name="value"/> is a delimiter the model can hold for <paramref name="format"/> (null is always allowed).</summary>
    public static bool IsDelimiterValue(string format, string? value) => value is null || format switch
    {
        "csv" or "tsv" => value is "," or ";" or "|" or "\t" or " ",
        "fixedwidth" => value == "space",
        "keyvalue" => value.Length == 1,
        _ => false
    };

    /// <summary>Null when the name is fine, else why not.</summary>
    public static string? NameProblem(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return "The name is empty; type at least one character.";
        }

        if (trimmed.Length > MaxNameLength)
        {
            return $"The name has {trimmed.Length} characters; use at most {MaxNameLength}.";
        }

        return trimmed.Any(char.IsControl) ? "The name contains a control character (such as a tab or a line break); remove it." : null;
    }

    public static string SkipLinesMessage(long lineCount)
        => lineCount <= 1
            ? "This container has only one line, so it can have no skip lines."
            : $"Skip lines must be between 0 and {(lineCount - 1).ToString(CultureInfo.InvariantCulture)} for this container.";

    /// <summary>A text the user typed, cut to <see cref="MaxNameLength"/> characters for a message or the lineage log.</summary>
    public static string Cut(string text) => text.Length <= MaxNameLength ? text : text[..MaxNameLength];
}

/// <summary>One line of a container: its byte range in the file (end before the line ending) and whether it is blank.</summary>
internal readonly record struct LineSpan(long Start, long End, bool Blank);

/// <summary>Reads one container's bytes and splits them into lines, for the edits. Never reads more than the container.</summary>
internal static class ContainerBytes
{
    /// <summary>The bytes from <paramref name="start"/> to <paramref name="end"/>, or null with a plain message.</summary>
    public static byte[]? Read(IntakeItem item, long start, long end, out string? error)
    {
        error = null;
        try
        {
            using var stream = item.OpenRead();
            IntakeReader.MoveTo(stream, start);
            var bytes = new byte[end - start];
            var read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (read != bytes.Length)
            {
                error = IntakeEditSession.ChangedFileMessage;
                return null;
            }

            return bytes;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            error = LineIndexer.NotFoundMessage;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = LineIndexer.UnreadableMessage;
        }

        return null;
    }

    /// <summary>
    /// The lines of <paramref name="bytes"/>, which start at file offset <paramref name="baseOffset"/>.
    /// <paramref name="unit"/> is 2 for UTF-16 (code units in the given byte order), else 1. CR, LF and CRLF end a line.
    /// </summary>
    public static List<LineSpan> Lines(byte[] bytes, long baseOffset, int unit, bool bigEndian)
    {
        var lines = new List<LineSpan>();
        var lineStart = 0;
        var blank = true;
        for (var i = 0; i + unit <= bytes.Length; i += unit)
        {
            int c = unit == 1 ? bytes[i] : bigEndian ? (bytes[i] << 8) | bytes[i + 1] : bytes[i] | (bytes[i + 1] << 8);
            if (c is '\r' or '\n')
            {
                lines.Add(new LineSpan(baseOffset + lineStart, baseOffset + i, blank));
                if (c == '\r' && i + 2 * unit <= bytes.Length)
                {
                    int next = unit == 1 ? bytes[i + 1] : bigEndian ? (bytes[i + 2] << 8) | bytes[i + 3] : bytes[i + 2] | (bytes[i + 3] << 8);
                    if (next == '\n')
                    {
                        i += unit;
                    }
                }

                lineStart = i + unit;
                blank = true;
                continue;
            }

            blank = blank && c is ' ' or '\t' or 0xFEFF;
        }

        lines.Add(new LineSpan(baseOffset + lineStart, baseOffset + bytes.Length, blank));
        return lines;
    }
}
