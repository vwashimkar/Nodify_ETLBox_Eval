using System.Text;

namespace EtlDesigner.Engine.Intake;

/// <summary>
/// Marker mode (plan section 5 and the README of the intake test data). Takes the lines of one scan
/// from <see cref="LineIndexer"/> and builds the containers. A problem in the file never stops the
/// scan: it becomes a status on the container or a file-level warning. Heads are decoded with the
/// encoding of the bytes read so far (UTF-8 for a UTF-16 file, whose heads the indexer converts).
/// </summary>
public sealed class MarkerScanner(MarkerProfile profile, Func<Encoding> headEncoding, AutoDetector? gapDetector = null)
{
    public const string StrayEndMessage = "END marker without BEGIN (kept as plain text)";

    private const string EmptyMessage = "empty segment";

    private const string NotClosedMessage = "segment was not closed before the next BEGIN marker";

    private const string NoEndMessage = "no END marker; the segment runs to the end of the file";

    private readonly byte _firstBeginByte = headEncoding().GetBytes(profile.BeginPrefix[..1])[0];

    private readonly byte _firstEndByte = headEncoding().GetBytes(profile.EndLine[..1])[0];

    private readonly List<ContainerInfo> _containers = [];

    private readonly List<IntakeWarning> _warnings = [];

    private readonly Dictionary<string, int> _formatCounts = new(StringComparer.Ordinal);

    private OpenSegment? _open;

    private ContentRange? _gap;

    public IReadOnlyList<ContainerInfo> Containers => _containers;

    public IReadOnlyList<IntakeWarning> Warnings => _warnings;

    public int ContainerCount => _containers.Count;

    public void OnLine(in IntakeLine line)
    {
        BeginMarker? begin = null;
        var isEnd = false;

        // Almost every line stops here, so nothing is decoded for it.
        if (line.HeadIsWholeLine && !line.Head.IsEmpty && (line.Head[0] == _firstBeginByte || line.Head[0] == _firstEndByte))
        {
            var text = headEncoding().GetString(line.Head).TrimEnd(' ', '\t');
            isEnd = string.Equals(text, profile.EndLine, StringComparison.Ordinal);
            begin = isEnd ? null : ParseBegin(text);
        }

        if (_open is not null)
        {
            if (isEnd)
            {
                CloseOpenSegment(line.Number, null);
            }
            else if (begin is not null)
            {
                CloseOpenSegment(null, NotClosedMessage);
                Open(begin, line.Number);
            }
            else
            {
                _open.Content = Extend(_open.Content, line);
            }

            return;
        }

        if (begin is not null)
        {
            FlushGap();
            gapDetector?.Break();
            Open(begin, line.Number);
            return;
        }

        if (isEnd)
        {
            _warnings.Add(new IntakeWarning(line.Number, StrayEndMessage));
        }

        // MarkersThenAutoDetect: the lines between segments, blank ones too, go to the detector.
        if (gapDetector is not null)
        {
            gapDetector.OnLine(line);
            return;
        }

        // Blank edge lines are dropped from a text block: a block starts and ends on a line with
        // text, and a blank line inside it stays because the block is counted from first to last.
        if (!line.IsBlank)
        {
            _gap = Extend(_gap, line);
        }
    }

    /// <summary>Call once, after the last line of a scan that reached the end of the file.</summary>
    public void Finish()
    {
        if (_open is not null)
        {
            CloseOpenSegment(null, NoEndMessage);
            return;
        }

        FlushGap();
    }

    /// <summary>
    /// Reads a BEGIN line; null when it is not one. The format must be a single name, so
    /// <c>&lt;format&gt;</c> in a description of the markers stays text. A separator after the third
    /// field belongs to the notes [U: chosen].
    /// </summary>
    private BeginMarker? ParseBegin(string text)
    {
        var fieldsLength = text.Length - profile.BeginPrefix.Length - profile.BeginSuffix.Length;
        if (fieldsLength <= 0
            || !text.StartsWith(profile.BeginPrefix, StringComparison.Ordinal)
            || !text.EndsWith(profile.BeginSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        var fields = text.Substring(profile.BeginPrefix.Length, fieldsLength).Split(profile.FieldSeparator, 3);
        var format = fields[0].Trim();
        if (format.Length == 0 || !format.All(IsFormatCharacter))
        {
            return null;
        }

        var name = fields.Length > 1 ? fields[1].Trim() : string.Empty;
        var notes = fields.Length > 2 ? fields[2].Trim() : string.Empty;
        return new BeginMarker(format, name, notes);
    }

    private static bool IsFormatCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';

    private void Open(BeginMarker begin, long lineNumber)
    {
        // The n of <format>-<n> counts the segments of that format so far, named or not [U: chosen].
        var count = _formatCounts.GetValueOrDefault(begin.Format) + 1;
        _formatCounts[begin.Format] = count;

        var name = begin.Name.Length > 0 ? begin.Name : $"{begin.Format}-{count}";
        _open = new OpenSegment(begin with { Name = name }, lineNumber);
    }

    private void CloseOpenSegment(long? endLine, string? closeProblem)
    {
        var segment = _open!;
        _open = null;

        var messages = new List<string>();
        var status = ContainerStatus.Ok;

        var format = segment.Begin.Format;
        if (!profile.KnownFormats.Contains(format))
        {
            messages.Add($"unknown format '{format}'; treated as text");
            status = ContainerStatus.Warning;
            format = "text";
        }

        if (segment.Content is null)
        {
            messages.Add(EmptyMessage);
            status = ContainerStatus.Warning;
        }

        if (closeProblem is not null)
        {
            messages.Add(closeProblem);
            status = closeProblem == NoEndMessage ? ContainerStatus.Error : ContainerStatus.Warning;
        }

        var content = segment.Content;
        _containers.Add(new ContainerInfo(
            _containers.Count + 1,
            ContainerKind.Marker,
            format,
            segment.Begin.Name,
            segment.Begin.Notes,
            segment.BeginLine,
            endLine,
            content?.FirstLine,
            content?.LastLine,
            content is null ? 0 : content.LastLine - content.FirstLine + 1,
            content?.StartByte,
            content?.EndByte,
            status,
            string.Join("; ", messages),
            ContainerConfidence.Sure));
    }

    private void FlushGap()
    {
        if (_gap is null)
        {
            return;
        }

        var gap = _gap;
        _gap = null;

        _containers.Add(new ContainerInfo(
            _containers.Count + 1,
            ContainerKind.GapText,
            "text",
            string.Empty,
            string.Empty,
            null,
            null,
            gap.FirstLine,
            gap.LastLine,
            gap.LastLine - gap.FirstLine + 1,
            gap.StartByte,
            gap.EndByte,
            ContainerStatus.Ok,
            string.Empty,
            ContainerConfidence.Sure));
    }

    private static ContentRange Extend(ContentRange? range, in IntakeLine line)
        => range is null
            ? new ContentRange(line.Number, line.Number, line.StartByte, line.EndByte)
            : range with { LastLine = line.Number, EndByte = line.EndByte };

    private sealed record BeginMarker(string Format, string Name, string Notes);

    private sealed record ContentRange(long FirstLine, long LastLine, long StartByte, long EndByte);

    private sealed class OpenSegment(BeginMarker begin, long beginLine)
    {
        public BeginMarker Begin { get; } = begin;

        public long BeginLine { get; } = beginLine;

        public ContentRange? Content { get; set; }
    }
}
