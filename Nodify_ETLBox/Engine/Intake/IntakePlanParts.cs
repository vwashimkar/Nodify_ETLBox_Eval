namespace EtlDesigner.Engine.Intake;

/// <summary>
/// One part of a version 2 <see cref="IntakePlan"/>: only what cannot be rebuilt from the scan.
/// <see cref="FirstLine"/> and <see cref="LastLine"/> are the part's whole span in the file, marker
/// lines included (as <see cref="IntakeEditSession"/> checks coverage); both are null for a part
/// with no lines. Format, options, name and <see cref="ConfirmedByUser"/> are the user's. Everything
/// else (kind, marker lines, content lines, byte offsets, confidence, sub-type, notes, status,
/// message, reasons) is rebuilt by <see cref="PlanRebuilder"/>; <see cref="Fix"/> holds the few
/// fields the rebuild would get wrong, and is absent for almost every part.
/// </summary>
public sealed record PlanPart(
    long? FirstLine,
    long? LastLine,
    string Format,
    string Name,
    string? Delimiter = null,
    HeaderGuess? Header = null,
    int SkipLines = 0,
    KeyValueForm? KeyValueForm = null,
    bool ConfirmedByUser = false,
    PlanFix? Fix = null);

/// <summary>
/// The fields of a <see cref="PlanPart"/> that differ from what the rebuild gives; null means "as
/// rebuilt". A line of 0 means "no line", an empty <see cref="SubType"/> or <see cref="SubTypeVersion"/>
/// means "none", and an empty <see cref="Reasons"/> list means no reasons.
/// </summary>
public sealed record PlanFix(
    ContainerKind? Kind = null,
    long? MarkerBeginLine = null,
    long? MarkerEndLine = null,
    long? ContentFirstLine = null,
    long? ContentLastLine = null,
    ContainerConfidence? Confidence = null,
    string? SubType = null,
    string? SubTypeVersion = null,
    string? Notes = null,
    ContainerStatus? Status = null,
    string? Message = null,
    IReadOnlyList<string>? Reasons = null)
{
    internal bool HasLines => MarkerBeginLine is not null || MarkerEndLine is not null || ContentFirstLine is not null || ContentLastLine is not null;

    internal bool IsEmpty => this == new PlanFix();
}

/// <summary>
/// Rebuilds the containers of a version 2 plan on the scan of the same file. A part with the span
/// of exactly one scan container is that container. Any other part (a split piece or a merge) is
/// built by the rules of <see cref="IntakeEditSession.Split"/> and <see cref="IntakeEditSession.Merge"/>:
/// kind, notes, status and message of the first scan container it covers; a marker line when the
/// span starts (ends) on that container's marker line; content lines from the span (or, inside
/// markers, from the covered containers); format and options by the detector on the part's own
/// bytes. Byte offsets come from the scan's containers; a line inside a container (a split line) is
/// found by one bounded read from the nearest known offset, never by reading the whole file.
/// Parts must be given in file order. The file is only read.
/// </summary>
internal sealed class PlanRebuilder
{
    private readonly IntakeItem _item;
    private readonly IntakeSettings _detect;
    private readonly int _maxEditMb;
    private readonly long _fileSize;
    private readonly long _firstByte;
    private readonly int _unit;
    private readonly bool _bigEndian;
    private readonly IReadOnlyList<ContainerInfo> _scan;
    private readonly List<(long First, long Last, int Index)> _spans = [];
    private readonly List<int> _empties = [];
    private readonly Dictionary<long, long> _starts = [];
    private readonly Dictionary<long, long> _ends = [];
    private readonly List<(long Line, int Order, long Byte)> _points = [];
    private int _spanCursor;
    private int _emptyCursor;

    public PlanRebuilder(IntakeItem item, IntakeScanResult scan, IntakeSettings settings, string encodingName, EditSettings edit)
    {
        var intake = scan.Intake!;
        _item = item;
        _scan = intake.Containers;
        _fileSize = intake.File!.Size;
        _detect = settings with { Mode = IntakeMode.AutoDetect, Encoding = encodingName };
        _maxEditMb = edit.MaxEditMb;
        _unit = encodingName is IntakeEncodings.Utf16Le or IntakeEncodings.Utf16Be ? 2 : 1;
        _bigEndian = encodingName == IntakeEncodings.Utf16Be;
        _firstByte = intake.File.HasBom ? (_unit == 2 ? 2 : 3) : 0;
        for (var i = 0; i < _scan.Count; i++)
        {
            var c = _scan[i];
            if (IntakeEditSession.Span(c) is { } span)
            {
                _spans.Add((span.First, span.Last, i));
            }
            else
            {
                _empties.Add(i);
            }

            if (c is { ContentFirstLine: { } first, ContentLastLine: { } last, ContentStartByte: { } start, ContentEndByte: { } end })
            {
                _starts[first] = start;
                _ends[last] = end;
                _points.Add((first, 0, start));
                _points.Add((last, 1, end));
            }
        }

        _points.Sort();
    }

    /// <summary>The part's container without its stored fields applied (see <see cref="Finish"/>), or null with the reason.</summary>
    public ContainerInfo? Build(PlanPart part, out string? error)
    {
        error = null;
        var fix = part.Fix;
        if (part.FirstLine is null || part.LastLine is null)
        {
            if (part.FirstLine is not null || part.LastLine is not null || _emptyCursor >= _empties.Count)
            {
                error = IntakePlan.DoesNotFitMessage;
                return null;
            }

            return _scan[_empties[_emptyCursor++]];
        }

        long f = part.FirstLine.Value, l = part.LastLine.Value;
        if (f < 1 || l < f)
        {
            error = IntakePlan.DoesNotFitMessage;
            return null;
        }

        while (_spanCursor < _spans.Count && _spans[_spanCursor].Last < f)
        {
            _spanCursor++;
        }

        var end = _spanCursor;
        while (end < _spans.Count && _spans[end].First <= l)
        {
            end++;
        }

        if (end == _spanCursor)
        {
            error = IntakePlan.DoesNotFitMessage;
            return null;
        }

        var covered = Enumerable.Range(_spanCursor, end - _spanCursor).Select(k => _scan[_spans[k].Index]).ToList();
        var first = covered[0];
        if (covered.Count == 1 && _spans[_spanCursor].First == f && _spans[_spanCursor].Last == l && fix?.HasLines != true)
        {
            return first;
        }

        var last = covered[^1];
        long? markerBegin = first.MarkerBeginLine == f ? f : null;
        long? markerEnd = last.MarkerEndLine == l ? l : null;
        var contentFirst = markerBegin is null ? f : covered.Select(c => c.ContentFirstLine).FirstOrDefault(x => x is not null);
        var contentLast = markerEnd is null ? l : covered.Select(c => c.ContentLastLine).LastOrDefault(x => x is not null);
        if (fix is not null)
        {
            markerBegin = Line(fix.MarkerBeginLine, markerBegin);
            markerEnd = Line(fix.MarkerEndLine, markerEnd);
            contentFirst = Line(fix.ContentFirstLine, contentFirst);
            contentLast = Line(fix.ContentLastLine, contentLast);
        }

        if ((contentFirst is null) != (contentLast is null) || contentFirst > contentLast || contentFirst < f || contentLast > l)
        {
            error = IntakePlan.DoesNotFitMessage;
            return null;
        }

        var built = first with
        {
            MarkerBeginLine = markerBegin,
            MarkerEndLine = markerEnd,
            ContentFirstLine = contentFirst,
            ContentLastLine = contentLast,
            ContentLineCount = contentFirst is { } cf ? contentLast!.Value - cf + 1 : 0,
            ContentStartByte = null,
            ContentEndByte = null
        };
        if (contentFirst is null)
        {
            return built;
        }

        if (StartOf(contentFirst.Value, out error) is not { } startByte || EndOf(contentLast!.Value, out error) is not { } endByte || endByte < startByte)
        {
            error ??= IntakePlan.DoesNotFitMessage;
            return null;
        }

        built = built with { ContentStartByte = startByte, ContentEndByte = endByte };
        var reason = covered.Count > 1 ? "Merged by you." : "Split by you.";
        if (endByte - startByte > _maxEditMb * 1024L * 1024L)
        {
            return built with { Confidence = ContainerConfidence.Guessed, Reasons = IntakeEditSession.With(built.Reasons, reason) };
        }

        var bytes = ContainerBytes.Read(_item, startByte, endByte, out error);
        return bytes is null ? null : IntakeEditSession.Redetect(_item, _detect, built, bytes, 0, bytes.Length, reason);
    }

    /// <summary>The stored fields of <paramref name="part"/> on the rebuilt container, then its fix.</summary>
    public static ContainerInfo Finish(ContainerInfo built, PlanPart part)
    {
        var c = built with
        {
            Format = part.Format,
            Name = part.Name,
            Delimiter = part.Delimiter,
            HeaderGuess = part.Header,
            SkipLines = part.SkipLines,
            KeyValueForm = part.KeyValueForm,
            ConfirmedByUser = part.ConfirmedByUser
        };
        if (part.Fix is not { } fix)
        {
            return c;
        }

        return c with
        {
            Kind = fix.Kind ?? c.Kind,
            Confidence = fix.Confidence ?? c.Confidence,
            SubType = fix.SubType is null ? c.SubType : fix.SubType.Length == 0 ? null : fix.SubType,
            SubTypeVersion = fix.SubTypeVersion is null ? c.SubTypeVersion : fix.SubTypeVersion.Length == 0 ? null : fix.SubTypeVersion,
            Notes = fix.Notes ?? c.Notes,
            Status = fix.Status ?? c.Status,
            Message = fix.Message ?? c.Message,
            Reasons = fix.Reasons is null ? c.Reasons : fix.Reasons.Count == 0 ? null : fix.Reasons
        };
    }

    /// <summary>
    /// The part that stores <paramref name="actual"/>, with a fix for every field the rebuild gets
    /// wrong; null when the rebuild cannot give it (the caller then keeps a version 1 plan).
    /// </summary>
    public PlanPart? Describe(ContainerInfo actual)
    {
        var span = IntakeEditSession.Span(actual);
        var part = new PlanPart(span?.First, span?.Last, actual.Format, actual.Name, actual.Delimiter, actual.HeaderGuess, actual.SkipLines, actual.KeyValueForm,
            actual.ConfirmedByUser);
        var (spanCursor, emptyCursor) = (_spanCursor, _emptyCursor);
        var built = Build(part, out _);
        if (built is not null && (built.MarkerBeginLine != actual.MarkerBeginLine || built.MarkerEndLine != actual.MarkerEndLine
            || built.ContentFirstLine != actual.ContentFirstLine || built.ContentLastLine != actual.ContentLastLine))
        {
            (_spanCursor, _emptyCursor) = (spanCursor, emptyCursor);
            part = part with
            {
                Fix = new PlanFix(
                    MarkerBeginLine: built.MarkerBeginLine != actual.MarkerBeginLine ? actual.MarkerBeginLine ?? 0 : null,
                    MarkerEndLine: built.MarkerEndLine != actual.MarkerEndLine ? actual.MarkerEndLine ?? 0 : null,
                    ContentFirstLine: built.ContentFirstLine != actual.ContentFirstLine ? actual.ContentFirstLine ?? 0 : null,
                    ContentLastLine: built.ContentLastLine != actual.ContentLastLine ? actual.ContentLastLine ?? 0 : null)
            };
            built = Build(part, out _);
        }

        if (built is null)
        {
            return null;
        }

        var c = Finish(built, part);
        var fix = (part.Fix ?? new PlanFix()) with
        {
            Kind = c.Kind != actual.Kind ? actual.Kind : null,
            Confidence = c.Confidence != actual.Confidence ? actual.Confidence : null,
            SubType = c.SubType != actual.SubType ? actual.SubType ?? string.Empty : null,
            SubTypeVersion = c.SubTypeVersion != actual.SubTypeVersion ? actual.SubTypeVersion ?? string.Empty : null,
            Notes = c.Notes != actual.Notes ? actual.Notes : null,
            Status = c.Status != actual.Status ? actual.Status : null,
            Message = c.Message != actual.Message ? actual.Message : null,
            Reasons = SameReasons(c.Reasons, actual.Reasons) ? null : actual.Reasons ?? []
        };
        part = part with { Fix = fix.IsEmpty ? null : fix };
        return Same(Finish(built, part), actual) ? part : null;
    }

    /// <summary>Every field equal (reasons by content), the order aside.</summary>
    internal static bool Same(ContainerInfo a, ContainerInfo b)
        => a with { Order = 0, Reasons = null } == b with { Order = 0, Reasons = null } && SameReasons(a.Reasons, b.Reasons)
           && (a.Reasons is null) == (b.Reasons is null);

    private static bool SameReasons(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
        => (a ?? []).SequenceEqual(b ?? []) && (a is null) == (b is null);

    private static long? Line(long? fixValue, long? rebuilt) => fixValue is null ? rebuilt : fixValue == 0 ? null : fixValue;

    private long? StartOf(long line, out string? error)
    {
        error = null;
        return _starts.TryGetValue(line, out var start) ? start : Find(line, out error)?.Start;
    }

    private long? EndOf(long line, out string? error)
    {
        error = null;
        return _ends.TryGetValue(line, out var end) ? end : Find(line, out error)?.End;
    }

    /// <summary>The bytes of one line, read from the nearest known offset before it up to the next known offset.</summary>
    private LineSpan? Find(long line, out string? error)
    {
        error = null;
        var key = (line, 0, long.MaxValue);
        var i = _points.BinarySearch(key);
        i = i >= 0 ? i : ~i - 1;
        var (fromLine, fromByte) = i >= 0 ? (_points[i].Line, _points[i].Byte) : (1L, _firstByte);
        var toByte = i + 1 < _points.Count ? _points[i + 1].Byte : _fileSize;
        if (toByte < fromByte || toByte - fromByte > _maxEditMb * 1024L * 1024L)
        {
            error = IntakePlan.DoesNotFitMessage;
            return null;
        }

        var bytes = ContainerBytes.Read(_item, fromByte, toByte, out error);
        if (bytes is null)
        {
            return null;
        }

        var lines = ContainerBytes.Lines(bytes, fromByte, _unit, _bigEndian);
        var index = line - fromLine;
        if (index < 0 || index >= lines.Count)
        {
            error = IntakePlan.DoesNotFitMessage;
            return null;
        }

        return lines[(int)index];
    }
}
