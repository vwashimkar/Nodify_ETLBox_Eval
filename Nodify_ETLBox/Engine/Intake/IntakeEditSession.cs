using System.Collections.ObjectModel;
using System.IO;

namespace EtlDesigner.Engine.Intake;

/// <summary>
/// The user's edits of one scan's containers (plan section 7): change format, table options,
/// rename, split and merge, with undo, redo and revert. Every edit checks its input first and
/// changes nothing when it gives a failed <see cref="EditResult"/>. After an edit the changed
/// containers are checked again (<see cref="IntakeValidator.ValidateContainers"/>, only their bytes
/// are read); the other checks are kept. The session then asserts that every line the scan put in
/// a container is still in exactly one container (the G2 property check); if not, the edit is
/// rolled back and a plain message is returned. The original file is only read, never written.
/// Each edit, undo, redo and revert is written to the lineage log. Not thread-safe: one UI thread.
/// </summary>
public sealed partial class IntakeEditSession
{
    public const string ChangedFileMessage = "The file has changed since it was read; read it again before editing.";

    private const string LayoutBrokenMessage = "This change would leave lines outside every container, so it was not made.";

    private readonly IntakeSettings _settings;
    private readonly EditSettings _edit;
    private readonly ILineageLog? _log;
    private readonly List<ContainerInfo> _containers;
    private readonly List<ContainerValidation> _validation;
    private readonly IReadOnlyList<ContainerInfo> _scanContainers;
    private readonly IReadOnlyList<ContainerValidation> _scanValidation;
    private readonly List<(long First, long Last)> _scanSpans;
    private readonly LinkedList<Change> _undo = new();
    private readonly Stack<Change> _redo = new();
    private int _state;
    private int _nextState;

    public IntakeEditSession(IntakeItem item, IntakeScanResult scan, ILineageLog? log, string intakeId, IntakeSettings? settings = null, EditSettings? edit = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(scan);
        if (scan.Intake is not { File: not null, Error: null, Cancelled: false } intake)
        {
            throw new ArgumentException("Only a scan that read the whole file can be edited.", nameof(scan));
        }

        Item = item;
        Scan = scan;
        IntakeId = intakeId;
        _log = log;
        _settings = (settings ?? new IntakeSettings()) with { Mode = scan.Mode };
        _edit = edit ?? new EditSettings();
        EncodingName = intake.EncodingUsed ?? intake.File.Encoding;

        _scanContainers = intake.Containers;
        _scanValidation = scan.Validation is { Error: null, Cancelled: false } v && v.Containers.Count == intake.Containers.Count
            ? v.Containers
            : IntakeValidator.ValidateContainers(item, intake.Containers, Enumerable.Range(0, intake.Containers.Count).ToList(), EncodingName, CancellationToken.None, _settings).Containers;
        _containers = [.. _scanContainers];
        _validation = [.. _scanValidation];
        _scanSpans = _scanContainers.Select(Span).Where(s => s is not null).Select(s => s!.Value).ToList();
        Containers = new ReadOnlyCollection<ContainerInfo>(_containers);
        Validation = new ReadOnlyCollection<ContainerValidation>(_validation);
    }

    public IntakeItem Item { get; }

    public IntakeScanResult Scan { get; }

    public string IntakeId { get; }

    public IntakeMode Mode => Scan.Mode;

    /// <summary>The encoding the file was read with; every read of its bytes uses it.</summary>
    public string EncodingName { get; }

    public string FileName => Path.GetFileName(Scan.Intake!.File!.Name);

    public string Sha256 => Scan.Intake!.File!.Sha256;

    /// <summary>The current containers in file order, numbered from 1. A live view: it changes with each edit.</summary>
    public IReadOnlyList<ContainerInfo> Containers { get; }

    /// <summary>The checks of <see cref="Containers"/>, one per container in the same order. A live view.</summary>
    public IReadOnlyList<ContainerValidation> Validation { get; }

    /// <summary>True when the containers differ from the scan's (an undo back to the scan or a revert makes it false).</summary>
    public bool IsModified => _state != 0;

    public bool CanUndo => _undo.Count > 0;

    /// <summary>The steps that can be undone now (at most <see cref="EditSettings.MaxUndo"/>).</summary>
    public int UndoSteps => _undo.Count;

    public bool CanRedo => _redo.Count > 0;

    // ---- Operations ----------------------------------------------------------------------------

    public EditResult ChangeFormat(int index, string newFormat)
    {
        if (Problem(index) is { } problem)
        {
            return EditResult.Fail(problem);
        }

        if (IntakeEditRules.FormatName(newFormat) is not { } format)
        {
            return EditResult.Fail(IntakeEditRules.UnknownFormatMessage(newFormat));
        }

        var old = _containers[index];
        var changed = WithFormat(old, format);
        return Commit("edit.format_changed", index, 1, [changed], new() { ["from"] = old.Format, ["to"] = format });
    }

    /// <summary>
    /// Table options of a csv, tsv or fixedwidth container; a null argument keeps that option.
    /// <paramref name="delimiter"/> is a name of <see cref="IntakeEditRules.DelimiterNames"/>,
    /// <paramref name="header"/> is Yes or No.
    /// </summary>
    public EditResult SetTableOptions(int index, string? delimiter = null, string? header = null, int? skipLines = null)
    {
        if (Problem(index) is { } problem)
        {
            return EditResult.Fail(problem);
        }

        var old = _containers[index];
        if (!IntakeEditRules.IsTable(old.Format))
        {
            return EditResult.Fail("Table options apply only to CSV, TSV and fixed-width tables; change the format first.");
        }

        var changed = old;
        if (delimiter is not null)
        {
            if (IntakeEditRules.DelimiterValue(old.Format, delimiter) is not { } value)
            {
                return EditResult.Fail(old.Format == "fixedwidth"
                    ? "A fixed-width table is split by spaces, so its delimiter can only be space."
                    : $"'{IntakeEditRules.Cut(delimiter)}' is not a delimiter the tool knows; choose one of {string.Join(", ", IntakeEditRules.DelimiterNames)}.");
            }

            changed = changed with { Delimiter = value };
        }

        if (header is not null)
        {
            HeaderGuess? guess = header.Trim().ToLowerInvariant() switch { "yes" => HeaderGuess.Yes, "no" => HeaderGuess.No, _ => null };
            if (guess is null)
            {
                return EditResult.Fail("The header setting must be Yes or No.");
            }

            changed = changed with { HeaderGuess = guess };
        }

        if (skipLines is { } skip)
        {
            if (skip < 0 || skip > old.ContentLineCount - 1)
            {
                return EditResult.Fail(IntakeEditRules.SkipLinesMessage(old.ContentLineCount));
            }

            changed = changed with { SkipLines = skip };
        }

        if (changed == old)
        {
            return EditResult.Done;
        }

        changed = changed with { ConfirmedByUser = true, Reasons = With(old.Reasons, "Options set by you.") };
        return Commit("edit.options_changed", index, 1, [changed], new()
        {
            ["delimiter"] = changed.Delimiter == "\t" ? "tab" : changed.Delimiter,
            ["header"] = changed.HeaderGuess?.ToString(),
            ["skipLines"] = changed.SkipLines
        });
    }

    public EditResult Rename(int index, string name)
    {
        if (Problem(index) is { } problem)
        {
            return EditResult.Fail(problem);
        }

        if (IntakeEditRules.NameProblem(name) is { } nameProblem)
        {
            return EditResult.Fail(nameProblem);
        }

        var old = _containers[index];
        var trimmed = name.Trim();
        if (trimmed == old.Name)
        {
            return EditResult.Done;
        }

        // The name is not used by the checks, so the container's check is kept.
        return Commit("edit.renamed", index, 1, [old with { Name = trimmed, ConfirmedByUser = true }],
            new() { ["from"] = IntakeEditRules.Cut(old.Name), ["to"] = trimmed }, revalidate: false);
    }

    /// <summary>
    /// Splits container <paramref name="index"/> before FILE line <paramref name="atLine"/>. The line
    /// must be inside the container and not its first line, and be non-blank or followed by a
    /// non-blank line of the container; blank lines before the split stay with the first part.
    /// </summary>
    public EditResult Split(int index, long atLine)
    {
        if (Problem(index) is { } problem)
        {
            return EditResult.Fail(problem);
        }

        if (SplitParts(_containers[index], atLine, out var error) is not { } parts)
        {
            return EditResult.Fail(error!);
        }

        return Commit("edit.split", index, 1, [parts.Top, parts.Bottom], new() { ["atLine"] = parts.At });
    }

    /// <summary>Merges container <paramref name="index"/> with the next one. The lines between them go inside; the first one's name, format and options are kept.</summary>
    public EditResult Merge(int index)
    {
        if (Problem(index) is { } problem)
        {
            return EditResult.Fail(problem);
        }

        if (index == _containers.Count - 1)
        {
            return EditResult.Fail("The last container has no next container to merge with.");
        }

        if (MergedPart(_containers[index], _containers[index + 1], out var error) is not { } merged)
        {
            return EditResult.Fail(error!);
        }

        return Commit("edit.merged", index, 2, [merged], []);
    }

    public EditResult Undo()
    {
        if (_undo.Last is not { } node)
        {
            return EditResult.Fail("There is nothing to undo.");
        }

        var change = node.Value;
        _undo.RemoveLast();
        Replace(change.Index, change.NewC.Length, change.OldC, change.OldV);
        _state = change.OldState;
        _redo.Push(change);
        Log("edit.undone", Details(change.Event, change.Index, change.NewC, change.OldC, change.Details));
        return EditResult.Done;
    }

    public EditResult Redo()
    {
        if (!_redo.TryPop(out var change))
        {
            return EditResult.Fail("There is nothing to redo.");
        }

        Replace(change.Index, change.OldC.Length, change.NewC, change.NewV);
        _state = change.NewState;
        PushUndo(change);
        Log("edit.redone", Details(change.Event, change.Index, change.OldC, change.NewC, change.Details));
        return EditResult.Done;
    }

    /// <summary>Goes back to the scan's containers and checks. One step that can be undone.</summary>
    public EditResult Revert()
    {
        if (!IsModified)
        {
            return EditResult.Done;
        }

        var change = new Change("edit.reverted", 0, [.. _containers], [.. _validation], [.. _scanContainers], [.. _scanValidation], _state, 0, []);
        Replace(0, change.OldC.Length, change.NewC, change.NewV);
        _state = 0;
        PushUndo(change);
        _redo.Clear();
        Log("edit.reverted", new() { ["containersBefore"] = change.OldC.Length, ["containersAfter"] = change.NewC.Length });
        return EditResult.Done;
    }

    // ---- Plans ---------------------------------------------------------------------------------

    /// <summary>
    /// A session that starts from a saved plan instead of the scan's containers, or null with the
    /// plain reason (also written to the lineage log as <c>plan.rejected</c>). All containers are
    /// checked again. <see cref="IsModified"/> is true; <see cref="Revert"/> goes back to the scan.
    /// </summary>
    public static IntakeEditSession? FromPlan(IntakeItem item, IntakeScanResult scan, IntakePlan plan, ILineageLog? log, string intakeId, out string message,
        IntakeSettings? settings = null, EditSettings? edit = null)
    {
        var session = new IntakeEditSession(item, scan, log, intakeId, settings, edit);
        if (!IntakePlan.TryApply(item, scan, plan, out var containers, out message, session._settings, session._edit))
        {
            session.Log("plan.rejected", new() { ["reason"] = message });
            return null;
        }

        var validation = IntakeValidator.ValidateContainers(item, containers, Enumerable.Range(0, containers.Count).ToList(), session.EncodingName,
            CancellationToken.None, session._settings);
        if (validation.Error is not null)
        {
            message = validation.Error;
            session.Log("plan.rejected", new() { ["reason"] = message });
            return null;
        }

        session.Replace(0, session._containers.Count, [.. containers], [.. validation.Containers]);
        session._state = ++session._nextState;
        session.Log("plan.applied", new() { ["containers"] = containers.Count, ["confirmedByUser"] = containers.Count(c => c.ConfirmedByUser) });
        return session;
    }

    internal void Log(string eventName, Dictionary<string, object?> details)
    {
        _log?.Write(new LineageEvent(eventName, IntakeId, FileName, Sha256, details));
    }

    // ---- Internals -----------------------------------------------------------------------------

    /// <summary>The container with another format, by the rules of <see cref="ChangeFormat"/>.</summary>
    internal static ContainerInfo WithFormat(ContainerInfo old, string format)
    {
        var changed = old with
        {
            Format = format,
            SubType = null,
            SubTypeVersion = null,
            Reasons = ["Set by you."],
            Confidence = ContainerConfidence.Sure,
            ConfirmedByUser = true
        };
        changed = format switch
        {
            "csv" => changed with { Delimiter = old.Delimiter is "," or ";" or "|" or " " ? old.Delimiter : ",", KeyValueForm = null },
            "tsv" => changed with { Delimiter = "\t", KeyValueForm = null },
            "fixedwidth" => changed with { Delimiter = "space", KeyValueForm = null },
            "keyvalue" => changed with { Delimiter = old.Format == "keyvalue" ? old.Delimiter : null, HeaderGuess = null },
            _ => changed with { Delimiter = null, HeaderGuess = null, SkipLines = 0, KeyValueForm = null }
        };

        return changed;
    }

    /// <summary>The two parts of a split of <paramref name="old"/> before FILE line <paramref name="atLine"/> (see <see cref="Split"/>), or null with the reason.</summary>
    private (ContainerInfo Top, ContainerInfo Bottom, long At)? SplitParts(ContainerInfo old, long atLine, out string? error)
    {
        error = null;
        if (old is not { ContentFirstLine: { } first, ContentLastLine: { } last, ContentStartByte: { } start, ContentEndByte: { } end })
        {
            error = "This container has no lines, so it cannot be split.";
            return null;
        }

        if (atLine == first)
        {
            error = "A container cannot be split at its first line; choose a line further down.";
            return null;
        }

        if (atLine < first || atLine > last)
        {
            error = $"Line {atLine} is not inside this container, which has lines {first} to {last}.";
            return null;
        }

        if (end - start > _edit.MaxEditMb * 1024L * 1024L)
        {
            error = $"This container is larger than the edit limit of {_edit.MaxEditMb} MB, so it cannot be split.";
            return null;
        }

        var bytes = ContainerBytes.Read(Item, start, end, out error);
        if (bytes is null)
        {
            return null;
        }

        var unit = IsUtf16 ? 2 : 1;
        var lines = ContainerBytes.Lines(bytes, start, unit, EncodingName == IntakeEncodings.Utf16Be);
        if (lines.Count != last - first + 1)
        {
            error = ChangedFileMessage;
            return null;
        }

        var at = atLine;
        if (lines[(int)(at - first)].Blank)
        {
            if (at == last || lines[(int)(at + 1 - first)].Blank)
            {
                error = $"Line {atLine} is blank and is not followed by a line with text, so there is nothing to split off there.";
                return null;
            }

            // The blank line stays with the first part.
            at++;
        }

        var cut = (int)(at - first);
        var top = old with
        {
            MarkerEndLine = null,
            ContentLastLine = at - 1,
            ContentLineCount = at - first,
            ContentEndByte = lines[cut - 1].End,
            ConfirmedByUser = true
        };
        var bottom = old with
        {
            Name = SecondName(old.Name),
            MarkerBeginLine = null,
            ContentFirstLine = at,
            ContentLineCount = last - at + 1,
            ContentStartByte = lines[cut].Start,
            ConfirmedByUser = true
        };

        top = Redetect(top, bytes, 0, (int)(top.ContentEndByte!.Value - start), "Split by you.");
        bottom = Redetect(bottom, bytes, (int)(bottom.ContentStartByte!.Value - start), (int)(end - bottom.ContentStartByte.Value), "Split by you.");
        return (top, bottom, at);
    }

    /// <summary>Container <paramref name="a"/> merged with the next one <paramref name="b"/> (see <see cref="Merge"/>), or null with the reason.</summary>
    private ContainerInfo? MergedPart(ContainerInfo a, ContainerInfo b, out string? error)
    {
        error = null;
        var first = a.ContentFirstLine ?? b.ContentFirstLine;
        var last = b.ContentLastLine ?? a.ContentLastLine;
        var start = a.ContentStartByte ?? b.ContentStartByte;
        var end = b.ContentEndByte ?? a.ContentEndByte;
        if (first is null || last is null || start is null || end is null)
        {
            error = "Both containers are empty, so there is nothing to merge.";
                return null;
        }

        var merged = a with
        {
            MarkerEndLine = b.MarkerEndLine,
            ContentFirstLine = first,
            ContentLastLine = last,
            ContentLineCount = last.Value - first.Value + 1,
            ContentStartByte = start,
            ContentEndByte = end,
            ConfirmedByUser = true
        };

        if (end.Value - start.Value <= _edit.MaxEditMb * 1024L * 1024L)
        {
            var bytes = ContainerBytes.Read(Item, start.Value, end.Value, out error);
            if (bytes is null)
            {
                return null;
            }

            merged = Redetect(merged, bytes, 0, bytes.Length, "Merged by you.");
        }
        else
        {
            merged = merged with { Confidence = ContainerConfidence.Guessed, Reasons = With(a.Reasons, "Merged by you.") };
        }

        return merged;
    }

    /// <summary>The settings of this session (its mode is the scan's).</summary>
    internal IntakeSettings Settings => _settings;

    internal EditSettings EditLimits => _edit;

    private bool IsUtf16 => EncodingName is IntakeEncodings.Utf16Le or IntakeEncodings.Utf16Be;

    private string? Problem(int index)
        => index < 0 || index >= _containers.Count ? $"There is no container number {index + 1}; the file has {IntakeText.Count(_containers.Count, "container")}." : null;

    private static string SecondName(string name)
    {
        var second = name + " (2)";
        return second.Length <= IntakeEditRules.MaxNameLength ? second : name[..(IntakeEditRules.MaxNameLength - 4)] + " (2)";
    }

    internal static IReadOnlyList<string> With(IReadOnlyList<string>? reasons, string reason) => [.. reasons ?? [], reason];

    /// <summary>
    /// Runs the detector (its public API, no rule changes) on the container's own bytes. When it
    /// finds exactly one container there, that one's format and options are taken; otherwise the
    /// container keeps its format and options and becomes Guessed. The range stays as it is.
    /// </summary>
    private ContainerInfo Redetect(ContainerInfo container, byte[] bytes, int offset, int length, string reason)
        => Redetect(Item, _settings with { Mode = IntakeMode.AutoDetect, Encoding = EncodingName }, container, bytes, offset, length, reason);

    /// <summary>The detector on a slice of bytes, as above; <paramref name="detect"/> is AutoDetect with the file's encoding. Also used to rebuild a saved plan.</summary>
    internal static ContainerInfo Redetect(IntakeItem item, IntakeSettings detect, ContainerInfo container, byte[] bytes, int offset, int length, string reason)
    {
        var slice = new IntakeItem(Guid.NewGuid(), "edit", item.Name, length, DateTime.UtcNow, () => new MemoryStream(bytes, offset, length, writable: false));
        var result = IntakeReader.Read(slice, detect, null, CancellationToken.None);
        if (result.Error is not null || result.Cancelled || result.Containers.Count != 1)
        {
            return container with { Confidence = ContainerConfidence.Guessed, Reasons = With(container.Reasons, reason) };
        }

        var found = result.Containers[0];
        return container with
        {
            Format = found.Format,
            SubType = found.SubType,
            SubTypeVersion = found.SubTypeVersion,
            Delimiter = found.Delimiter,
            HeaderGuess = found.HeaderGuess,
            SkipLines = (int)Math.Min(found.SkipLines, Math.Max(0, container.ContentLineCount - 1)),
            KeyValueForm = found.KeyValueForm,
            Confidence = found.Confidence,
            Reasons = With(found.Reasons, reason)
        };
    }

    private EditResult Commit(string eventName, int index, int removeCount, ContainerInfo[] added, Dictionary<string, object?> details, bool revalidate = true)
    {
        var oldC = _containers.GetRange(index, removeCount).ToArray();
        var oldV = _validation.GetRange(index, removeCount).ToArray();
        for (var i = 0; i < added.Length; i++)
        {
            added[i] = added[i] with { Order = index + 1 + i };
        }

        ContainerValidation[] newV;
        if (revalidate)
        {
            var validation = IntakeValidator.ValidateContainers(Item, added, Enumerable.Range(0, added.Length).ToList(), EncodingName, CancellationToken.None, _settings);
            if (validation.Error is not null)
            {
                return EditResult.Fail(validation.Error);
            }

            newV = [.. validation.Containers];
        }
        else
        {
            newV = oldV;
        }

        Replace(index, oldC.Length, added, newV);
        if (!LayoutHolds())
        {
            Replace(index, added.Length, oldC, oldV);
            return EditResult.Fail(LayoutBrokenMessage);
        }

        var change = new Change(eventName, index, oldC, oldV, added, newV, _state, ++_nextState, details);
        _state = change.NewState;
        PushUndo(change);
        _redo.Clear();
        Log(eventName, Details(null, index, oldC, added, details));
        return EditResult.Done;
    }

    private void PushUndo(Change change)
    {
        _undo.AddLast(change);
        while (_undo.Count > Math.Max(0, _edit.MaxUndo))
        {
            _undo.RemoveFirst();
        }
    }

    /// <summary>Replaces <paramref name="count"/> containers at <paramref name="index"/> and numbers the containers from there again.</summary>
    private void Replace(int index, int count, ContainerInfo[] containers, ContainerValidation[] validation)
    {
        _containers.RemoveRange(index, count);
        _containers.InsertRange(index, containers);
        _validation.RemoveRange(index, count);
        _validation.InsertRange(index, validation);
        var to = count == containers.Length ? index + containers.Length : _containers.Count;
        for (var i = index; i < to; i++)
        {
            if (_containers[i].Order != i + 1)
            {
                _containers[i] = _containers[i] with { Order = i + 1 };
            }

            if (_validation[i].Order != i + 1)
            {
                _validation[i] = _validation[i] with { Order = i + 1 };
            }
        }
    }

    /// <summary>The lines a container covers in the file, its marker lines included; null for a container with no lines.</summary>
    internal static (long First, long Last)? Span(ContainerInfo c)
    {
        var first = c.MarkerBeginLine ?? c.ContentFirstLine;
        var last = c.MarkerEndLine ?? c.ContentLastLine;
        return first is null || last is null ? null : (first.Value, last.Value);
    }

    /// <summary>
    /// The G2 property check: the containers are in order and do not overlap, and every line the
    /// scan put in a container is in one of them. The scan puts every non-blank line in a container,
    /// so no non-blank line is lost.
    /// </summary>
    private bool LayoutHolds() => Covers(_containers, _scanSpans);

    /// <summary>
    /// True when <paramref name="containers"/> are in file order, do not overlap, end by line
    /// <paramref name="lineCount"/> (when given) and together cover every range of <paramref name="required"/>
    /// (in file order).
    /// </summary>
    internal static bool Covers(IReadOnlyList<ContainerInfo> containers, IReadOnlyList<(long First, long Last)> required, long? lineCount = null)
    {
        var spans = new List<(long First, long Last)>(containers.Count);
        long previous = 0;
        foreach (var c in containers)
        {
            if (Span(c) is not { } span)
            {
                continue;
            }

            if (span.First > span.Last || span.First <= previous || span.Last > (lineCount ?? long.MaxValue))
            {
                return false;
            }

            previous = span.Last;
            spans.Add(span);
        }

        var j = 0;
        foreach (var (first, last) in required)
        {
            var line = first;
            while (line <= last)
            {
                while (j < spans.Count && spans[j].Last < line)
                {
                    j++;
                }

                if (j == spans.Count || spans[j].First > line)
                {
                    return false;
                }

                line = spans[j].Last + 1;
            }
        }

        return true;
    }

    private Dictionary<string, object?> Details(string? undid, int index, ContainerInfo[] before, ContainerInfo[] after, Dictionary<string, object?> extra)
    {
        var details = new Dictionary<string, object?>();
        if (undid is not null)
        {
            details["step"] = undid;
        }

        details["before"] = Ranges(before, index);
        details["after"] = Ranges(after, index);
        foreach (var (key, value) in extra)
        {
            details[key] = value;
        }

        return details;
    }

    private static List<Dictionary<string, object?>> Ranges(ContainerInfo[] containers, int index)
        => containers.Select((c, i) => new Dictionary<string, object?>
        {
            ["container"] = index + 1 + i,
            ["firstLine"] = Span(c)?.First,
            ["lastLine"] = Span(c)?.Last
        }).ToList();

    /// <summary>One undoable step: at <see cref="Index"/>, <see cref="OldC"/> was replaced by <see cref="NewC"/>.</summary>
    private sealed record Change(
        string Event,
        int Index,
        ContainerInfo[] OldC,
        ContainerValidation[] OldV,
        ContainerInfo[] NewC,
        ContainerValidation[] NewV,
        int OldState,
        int NewState,
        Dictionary<string, object?> Details);
}
