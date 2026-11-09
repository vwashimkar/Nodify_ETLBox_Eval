using System.Collections.ObjectModel;
using System.Globalization;
using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.Mvvm;
using EtlDesigner.UI.Services;

namespace EtlDesigner.UI.ViewModels;

/// <summary>One line of the Preview: its FILE line number and the text shown (number, two spaces, the line).</summary>
public sealed record IntakePreviewRow(long LineNumber, string Text);

/// <summary>
/// The review tools of Intake Studio (G5b): an <see cref="IntakeEditSession"/> per finished scan,
/// the edit fields of the selected part, the edit commands and their enabled rules, the saved
/// layout (plan) and the unsaved-layout logic. Every rule lives here; the window only binds.
/// </summary>
public sealed partial class IntakeStudioViewModel
{
    public const string SetByYouText = "Set by you";
    public const string RescanQuestion = "Scanning again will discard your changes. Scan anyway?";
    public const string DiscardQuestion = "Changing this will discard your changes to the layout. Change anyway?";
    public const string DiscardReasonScan = "scan";
    public const string DiscardReasonMode = "mode";
    public const string DiscardReasonEncoding = "encoding";
    public const string DiscardReasonFile = "file";
    public const string UnreadablePlanText = "The saved layout could not be read.";
    public const string AutomaticResultText = "Showing the automatic result.";
    public const string PreviewShortText = "The preview shows the first 200 lines; type a line number to split further down.";

    /// <summary>A change of more rows than this replaces the list in one go instead of row by row.</summary>
    public const int RowByRowLimit = 200;

    private readonly ILineageLog? _lineage;
    private readonly Action<string?>? _savePlan;
    private readonly Func<string, bool>? _confirm;
    private readonly Func<string, bool> _copyText;
    private string? _savedPlanJson;
    private IntakeEditSession? _session;
    private IntakeScanResult? _scanResult;
    private ContainerInfo[] _savedLayout = [];
    private ContainerInfo[] _shownContainers = [];
    private ContainerValidation[] _shownValidation = [];
    private bool _layoutDirty;
    private int _errors;
    private int _warnings;
    private string? _layoutText;
    private string? _editStatus;
    private string? _lineageNote;
    private string? _selectedFormat;
    private string? _selectedDelimiter;
    private string? _selectedHeader;
    private string _skipLinesText = string.Empty;
    private string _newName = string.Empty;
    private string _splitLineText = string.Empty;
    private IntakePreviewRow? _selectedPreviewLine;
    private bool _syncingPreview;

    /// <summary>The session of the last scan and the message to show with it (a refused saved layout).</summary>
    private sealed record LayoutStart(IntakeEditSession Session, IntakeScanResult Scan, string? Message);

    public IReadOnlyList<SettingChoice> FormatChoices { get; private set; } = [];

    public IReadOnlyList<SettingChoice> DelimiterChoices { get; } =
        IntakeEditRules.DelimiterNames.Select(n => new SettingChoice(n, char.ToUpperInvariant(n[0]) + n[1..], true)).ToList();

    public IReadOnlyList<SettingChoice> HeaderChoices { get; } = [new("Yes", "Yes", true), new("No", "No", true)];

    public RelayCommand ChangeFormatCommand { get; private set; } = null!;

    public RelayCommand ApplyOptionsCommand { get; private set; } = null!;

    public RelayCommand RenameCommand { get; private set; } = null!;

    public RelayCommand SplitCommand { get; private set; } = null!;

    public RelayCommand MergeCommand { get; private set; } = null!;

    public RelayCommand UndoCommand { get; private set; } = null!;

    public RelayCommand RedoCommand { get; private set; } = null!;

    public RelayCommand RevertCommand { get; private set; } = null!;

    /// <summary>The edit session of the last finished scan (null before a scan and after a setting changed).</summary>
    public IntakeEditSession? Session => _session;

    /// <summary>The plan JSON the node holds now (after a save in this dialog, the new one).</summary>
    public string? SavedPlanJson => _savedPlanJson;

    /// <summary>True when a scan has finished and no scan runs: the edit area is usable.</summary>
    public bool CanEdit => _session is not null && !IsScanning;

    /// <summary>A part is selected and can be edited.</summary>
    public bool CanEditSelected => CanEdit && SelectedIndex >= 0;

    /// <summary>The selected part is a CSV, TSV or fixed-width table: delimiter, header and skip lines apply.</summary>
    public bool IsTableSelected => CanEditSelected && IntakeEditRules.IsTable(SelectedContainer!.Info.Format);

    /// <summary>"Layout: automatic" or "Layout: edited by you (3 changes)". Null before a scan.</summary>
    public string? LayoutText
    {
        get => _layoutText;
        private set => SetProperty(ref _layoutText, value);
    }

    /// <summary>The outcome of the last edit, or why a saved layout was not used. One plain sentence.</summary>
    public string? EditStatus
    {
        get => _editStatus;
        private set => SetProperty(ref _editStatus, value);
    }

    /// <summary>Where the lineage file is written when that is not the program folder (shown once).</summary>
    public string? LineageNote
    {
        get => _lineageNote;
        private set => SetProperty(ref _lineageNote, value);
    }

    public string? SelectedFormat
    {
        get => _selectedFormat;
        set
        {
            if (SetProperty(ref _selectedFormat, value))
            {
                ChangeFormatCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? SelectedDelimiter
    {
        get => _selectedDelimiter;
        set => SetProperty(ref _selectedDelimiter, value);
    }

    /// <summary>"Yes", "No", or null when the header is not known.</summary>
    public string? SelectedHeader
    {
        get => _selectedHeader;
        set => SetProperty(ref _selectedHeader, value);
    }

    /// <summary>The skip lines as typed; checked when the options are applied.</summary>
    public string SkipLinesText
    {
        get => _skipLinesText;
        set => SetProperty(ref _skipLinesText, value ?? string.Empty);
    }

    public string NewName
    {
        get => _newName;
        set => SetProperty(ref _newName, value ?? string.Empty);
    }

    /// <summary>The FILE line to split before, as typed or filled from the clicked Preview line.</summary>
    public string SplitLineText
    {
        get => _splitLineText;
        set
        {
            if (SetProperty(ref _splitLineText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(SplitLine));
                OnPropertyChanged(nameof(SplitToolTip));
                SplitCommand.RaiseCanExecuteChanged();
                SyncPreviewSelection();
            }
        }
    }

    /// <summary>The split line as a number, or null when the box is empty or not a whole number.</summary>
    public long? SplitLine
    {
        get => long.TryParse(SplitLineText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var line) ? line : null;
        set => SplitLineText = value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>Why Split is not possible now, or what it will do.</summary>
    public string SplitToolTip => SplitProblem() ?? $"Split part {SelectedIndex + 1} before line {SplitLine!.Value.ToString(CultureInfo.InvariantCulture)}.";

    /// <summary>The lines of the Preview; a click selects one and fills <see cref="SplitLineText"/>.</summary>
    public ObservableCollection<IntakePreviewRow> PreviewRows { get; } = new();

    public IntakePreviewRow? SelectedPreviewLine
    {
        get => _selectedPreviewLine;
        set
        {
            if (!SetProperty(ref _selectedPreviewLine, value))
            {
                return;
            }

            CopyPreviewLineCommand.RaiseCanExecuteChanged();
            if (value is not null && !_syncingPreview)
            {
                SplitLine = value.LineNumber;
            }
        }
    }

    /// <summary>The text of the selected Preview line as it is in the file, without its line number; null when no line is selected.</summary>
    public string? SelectedPreviewLineText
        => SelectedPreviewLine is { } row ? PreviewLines.FirstOrDefault(l => l.LineNumber == row.LineNumber)?.Text : null;

    /// <summary>Ctrl+C and "Copy line" in the Preview: copies <see cref="SelectedPreviewLineText"/>.</summary>
    public RelayCommand CopyPreviewLineCommand { get; private set; } = null!;

    private void CopyPreviewLine()
    {
        if (SelectedPreviewLineText is not { } text)
        {
            return;
        }

        var line = SelectedPreviewLine!.LineNumber.ToString(CultureInfo.InvariantCulture);
        EditStatus = _copyText(text)
            ? $"Copied line {line}."
            : $"Line {line} could not be copied because another program is using the clipboard. Try again.";
    }

    private int SelectedIndex => SelectedContainer is { } row && _session is not null ? row.Number - 1 : -1;

    private void CreateEditCommands()
    {
        FormatChoices = IntakeEditRules.Formats
            .Select(f => new SettingChoice(f, _labels.Find("container.format." + f)
                ?? ContainerDisplay.DefaultNames.GetValueOrDefault("container.format." + f, f), true))
            .ToList();

        ChangeFormatCommand = new RelayCommand(_ =>
        {
            // Read before the edit: the refreshed selection fills the fields again.
            var format = SelectedFormat!;
            Edit(i => _session!.ChangeFormat(i, format), i => $"Part {i + 1} is now {FormatLabel(format)}.");
        }, _ => CanEditSelected && SelectedFormat is not null);
        ApplyOptionsCommand = new RelayCommand(_ => ApplyOptions(), _ => IsTableSelected);
        RenameCommand = new RelayCommand(_ => Edit(i => _session!.Rename(i, NewName), i => $"Part {i + 1} renamed."), _ => CanEditSelected);
        SplitCommand = new RelayCommand(_ =>
        {
            var line = SplitLine!.Value;
            // A blank split line moves the split down to the next line (engine rule); the message says where it went.
            Edit(i => _session!.Split(i, line),
                i => $"Split part {i + 1} at line {(_session!.Containers[i + 1].ContentFirstLine ?? line).ToString(CultureInfo.InvariantCulture)}.");
        }, _ => SplitProblem() is null);
        MergeCommand = new RelayCommand(_ => Edit(i => _session!.Merge(i), i => $"Merged part {i + 1} with the next part."),
            _ => CanEditSelected && SelectedIndex < _session!.Containers.Count - 1);
        UndoCommand = new RelayCommand(_ => Edit(_ => _session!.Undo(), _ => "Undid the last change.", whole: true), _ => CanEdit && _session!.CanUndo);
        RedoCommand = new RelayCommand(_ => Edit(_ => _session!.Redo(), _ => "Redid the change.", whole: true), _ => CanEdit && _session!.CanRedo);
        RevertCommand = new RelayCommand(_ => Edit(_ => _session!.Revert(), _ => "Back to the detected result.", whole: true),
            _ => CanEdit && _session!.IsModified);
        CopyPreviewLineCommand = new RelayCommand(_ => CopyPreviewLine(), _ => SelectedPreviewLineText is not null);
    }

    private string FormatLabel(string? format) => FormatChoices.FirstOrDefault(c => c.Value == format)?.Label ?? format ?? string.Empty;

    /// <summary>Null when Split can run; otherwise why not, in one sentence.</summary>
    private string? SplitProblem()
    {
        if (!CanEditSelected)
        {
            return "Scan the file and choose a part first.";
        }

        var info = SelectedContainer!.Info;
        if (SplitLine is not { } line)
        {
            return "Click a line in the Preview, or type a line number, to choose where to split.";
        }

        if (info.ContentFirstLine is not { } first || info.ContentLastLine is not { } last || line <= first || line > last)
        {
            return info.ContentFirstLine is { } f && info.ContentLastLine is { } l && f < l
                ? $"Choose a line from {(f + 1).ToString(CultureInfo.InvariantCulture)} to {l.ToString(CultureInfo.InvariantCulture)}; a part is split before that line."
                : "This part has only one line, so it cannot be split.";
        }

        return null;
    }

    private void ApplyOptions()
    {
        int? skip = null;
        if (!string.IsNullOrWhiteSpace(SkipLinesText))
        {
            if (!int.TryParse(SkipLinesText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                EditStatus = "Skip lines must be a whole number, 0 or more.";
                return;
            }

            skip = value;
        }

        Edit(i => _session!.SetTableOptions(i, SelectedDelimiter, SelectedHeader, skip), i => $"Options of part {i + 1} changed.");
    }

    /// <summary>
    /// Runs one operation. A failure shows the engine's message and changes nothing. A success
    /// refreshes the rows that changed, selects the affected part and shows <paramref name="done"/>.
    /// <paramref name="whole"/>: undo, redo and revert, which do not act on the selected part.
    /// </summary>
    private void Edit(Func<int, EditResult> operation, Func<int, string> done, bool whole = false)
    {
        if (_session is null || (!whole && SelectedIndex < 0))
        {
            return;
        }

        var index = whole ? -1 : SelectedIndex;
        var result = operation(index);
        if (!result.Ok)
        {
            EditStatus = result.Message;
            return;
        }

        var changed = RefreshRows();
        var select = changed >= 0 ? changed : index;
        EditStatus = changed < 0 ? "Nothing changed." : done(whole ? changed : index);
        if (select >= 0 && select < Containers.Count)
        {
            SelectedContainer = Containers[select];
        }

        AfterLayoutChange();
    }

    private void AfterLayoutChange()
    {
        UpdateTotals();
        UpdateLayoutText();
        ShowLineageNote();
        RaiseUnsavedChanged();
        RaiseEditStates();
    }

    private void RaiseEditStates()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanEditSelected));
        OnPropertyChanged(nameof(IsTableSelected));
        OnPropertyChanged(nameof(SplitToolTip));
        foreach (var command in new[] { ChangeFormatCommand, ApplyOptionsCommand, RenameCommand, SplitCommand, MergeCommand, UndoCommand, RedoCommand, RevertCommand })
        {
            command?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Scan with a confirmation when the layout has changes that are not saved (default answer No).</summary>
    private void ScanAgain()
    {
        if (!ConfirmDiscard(DiscardReasonScan))
        {
            return;
        }

        ScanTask = ScanAsync();
    }

    /// <summary>The layout has edits that are not saved: a new scan or a new setting would lose them.</summary>
    private bool HasUnsavedLayoutEdits => _session is { IsModified: true } && _layoutDirty;

    /// <summary>
    /// True when nothing is lost or the user answers Yes (default No). <paramref name="reason"/> is
    /// <see cref="DiscardReasonScan"/>, <see cref="DiscardReasonMode"/>, <see cref="DiscardReasonEncoding"/> or <see cref="DiscardReasonFile"/>.
    /// </summary>
    private bool ConfirmDiscard(string reason)
    {
        if (!HasUnsavedLayoutEdits)
        {
            return true;
        }

        if (!(_confirm?.Invoke(reason == DiscardReasonScan ? RescanQuestion : DiscardQuestion) ?? false))
        {
            return false;
        }

        // The decision is part of the lineage: which edits were thrown away, and why.
        var session = _session!;
        _lineage?.Write(new LineageEvent("edit.discarded", session.IntakeId, session.FileName, session.Sha256, new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["changes"] = session.UndoSteps
        }));
        return true;
    }

    /// <summary>
    /// A refused change: the box shows the kept value again. Raised now and once more after the binding
    /// has finished its own update, because a ComboBox ignores a change raised during that update.
    /// </summary>
    private void KeepShownValue(string property)
    {
        OnPropertyChanged(property);
        _context?.Post(_ => OnPropertyChanged(property), null);
    }

    /// <summary>Off the UI thread: the session, from the node's saved layout when it fits the file.</summary>
    private LayoutStart StartSession(IntakeItem item, IntakeScanResult scan, FileIntakeNodeSettings settings, string intakeId)
    {
        var intakeSettings = settings.ToIntakeSettings();
        string? message = null;
        if (_savedPlanJson is { } json)
        {
            if (IntakePlanJson.Deserialize(json, out var error) is { } plan)
            {
                if (IntakeEditSession.FromPlan(item, scan, plan, _lineage, intakeId, out var refused, intakeSettings) is { } fromPlan)
                {
                    return new LayoutStart(fromPlan, scan, null);
                }

                message = $"{refused} {AutomaticResultText}";
            }
            else
            {
                message = error == IntakePlanJson.VersionMessage(IntakePlan.CurrentFormatVersion + 1)
                    ? $"{error} {AutomaticResultText}"
                    : $"{UnreadablePlanText} {AutomaticResultText}";
            }
        }

        return new LayoutStart(new IntakeEditSession(item, scan, _lineage, intakeId, intakeSettings), scan, message);
    }

    private void ShowSession(LayoutStart start)
    {
        _session = start.Session;
        _scanResult = start.Scan;
        var scanValidation = start.Scan.Validation!.Containers;
        _shownContainers = [.. start.Scan.Intake!.Containers];
        _shownValidation = scanValidation.Count == _shownContainers.Length ? [.. scanValidation] : [.. _session.Validation];
        Containers = new ObservableCollection<IntakeContainerRow>(_shownContainers.Select((c, i) => ToRow(c, _shownValidation[i])));

        // A saved layout changes rows against the scan: the totals follow by difference.
        RefreshRows();
        _savedLayout = [.. _session.Containers];
        EditStatus = start.Message;
        UpdateTotals();
        UpdateLayoutText();
        ShowLineageNote();
        RaiseUnsavedChanged();
        RaiseEditStates();
    }

    private void ClearSession()
    {
        _session = null;
        _scanResult = null;
        _shownContainers = [];
        _shownValidation = [];
        _savedLayout = [];
        _layoutDirty = false;
        LayoutText = null;
        EditStatus = null;
        RaiseEditStates();
    }

    /// <summary>
    /// Replaces only the rows whose container or check changed (compared by value with what is
    /// shown) and keeps the totals by difference. Returns the first changed index, or -1.
    /// </summary>
    private int RefreshRows()
    {
        var containers = _session!.Containers;
        var validation = _session.Validation;
        int oldCount = _shownContainers.Length, newCount = containers.Count, both = Math.Min(oldCount, newCount);
        var prefix = 0;
        while (prefix < both && Same(_shownContainers[prefix], containers[prefix]) && Same(_shownValidation[prefix], validation[prefix]))
        {
            prefix++;
        }

        if (prefix == oldCount && prefix == newCount)
        {
            return -1;
        }

        var suffix = 0;
        while (suffix < both - prefix
               && Same(_shownContainers[oldCount - 1 - suffix], containers[newCount - 1 - suffix])
               && Same(_shownValidation[oldCount - 1 - suffix], validation[newCount - 1 - suffix]))
        {
            suffix++;
        }

        int removed = oldCount - prefix - suffix, added = newCount - prefix - suffix;
        for (var i = prefix; i < prefix + removed; i++)
        {
            Count(_shownValidation[i], -1);
        }

        var rows = new List<IntakeContainerRow>(added);
        for (var i = prefix; i < prefix + added; i++)
        {
            Count(validation[i], +1);
            rows.Add(ToRow(containers[i], validation[i]));
        }

        if (removed + added > RowByRowLimit)
        {
            Containers = new ObservableCollection<IntakeContainerRow>(Containers.Take(prefix).Concat(rows).Concat(Containers.Skip(oldCount - suffix)));
        }
        else
        {
            SelectedContainer = null;
            var same = Math.Min(removed, added);
            for (var i = 0; i < same; i++)
            {
                Containers[prefix + i] = rows[i];
            }

            for (var i = same; i < removed; i++)
            {
                Containers.RemoveAt(prefix + same);
            }

            for (var i = same; i < added; i++)
            {
                Containers.Insert(prefix + i, rows[i]);
            }
        }

        _shownContainers = [.. containers];
        _shownValidation = [.. validation];
        return prefix;
    }

    /// <summary>
    /// Adds or takes away one part's issues from the totals. Issues beyond the shown ones count with
    /// the part's own status (the checks keep only the first ones).
    /// </summary>
    private void Count(ContainerValidation v, int sign)
    {
        var errors = v.Issues.Count(i => i.Severity == ValidationSeverity.Error);
        var warnings = v.Issues.Count(i => i.Severity == ValidationSeverity.Warning);
        var hidden = Math.Max(0, v.TotalIssues - v.Issues.Count);
        if (v.Status == ContainerStatus.Error)
        {
            errors += hidden;
        }
        else if (v.Status == ContainerStatus.Warning)
        {
            warnings += hidden;
        }

        _errors += sign * errors;
        _warnings += sign * warnings;
    }

    private void UpdateTotals()
    {
        var count = IntakeText.Count(Containers.Count, "container");
        TotalsText = $"{count}, {IntakeText.Count(_errors, "error")}, {IntakeText.Count(_warnings, "warning")}";
        Summary = string.Join(", ", new[] { count }
            .Concat(_errors > 0 ? [IntakeText.Count(_errors, "error")] : [])
            .Concat(_warnings > 0 ? [IntakeText.Count(_warnings, "warning")] : []));
    }

    private void UpdateLayoutText()
        => LayoutText = _session switch
        {
            null => null,
            { IsModified: false } => "Layout: automatic",
            { UndoSteps: 0 } => "Layout: edited by you (saved layout)",
            var s => $"Layout: edited by you ({IntakeText.Count(s.UndoSteps, "change")})"
        };

    private void ShowLineageNote()
    {
        if (LineageNote is null && _lineage is TextFileLineageLog { Note: { } note })
        {
            LineageNote = note;
        }
    }

    /// <summary>
    /// Writes the layout to the node: a plan when the layout is edited, nothing (the plan removed)
    /// when the user went back to the detected result of a saved layout.
    /// </summary>
    private void SaveLayout()
    {
        if (_session is null || _scanResult is null)
        {
            return;
        }

        var json = _session.IsModified ? IntakePlanJson.Serialize(IntakePlan.FromSession(_session, _scanResult)) : null;
        _savePlan?.Invoke(json);
        _savedPlanJson = json;
        _savedLayout = [.. _session.Containers];
        ShowLineageNote();
    }

    /// <summary>Records compare by value; an undo gives renumbered copies that equal the originals.</summary>
    private static bool Same<T>(T a, T b) where T : class => ReferenceEquals(a, b) || a.Equals(b);

    private static bool SameLayout(IReadOnlyList<ContainerInfo> current, ContainerInfo[] saved)
    {
        if (current.Count != saved.Length)
        {
            return false;
        }

        for (var i = 0; i < saved.Length; i++)
        {
            if (!Same(current[i], saved[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The edit fields show the selected part's values; the split line is cleared.</summary>
    private void FillEditFields()
    {
        var info = SelectedContainer?.Info;
        SelectedFormat = info is null ? null : IntakeEditRules.FormatName(info.Format);
        SelectedDelimiter = info?.Delimiter switch
        {
            "," => "comma",
            ";" => "semicolon",
            "|" => "pipe",
            "\t" => "tab",
            " " => "space",
            _ => null
        };
        SelectedHeader = info?.HeaderGuess switch { HeaderGuess.Yes => "Yes", HeaderGuess.No => "No", _ => null };
        SkipLinesText = info is null ? string.Empty : info.SkipLines.ToString(CultureInfo.InvariantCulture);
        NewName = info?.Name ?? string.Empty;
        SplitLineText = string.Empty;
        RaiseEditStates();
    }

    /// <summary>The Preview highlights the line of <see cref="SplitLine"/> when it is shown.</summary>
    private void SyncPreviewSelection()
    {
        _syncingPreview = true;
        try
        {
            var line = SplitLine;
            SelectedPreviewLine = line is null ? null : PreviewRows.FirstOrDefault(r => r.LineNumber == line);
        }
        finally
        {
            _syncingPreview = false;
        }
    }
}
