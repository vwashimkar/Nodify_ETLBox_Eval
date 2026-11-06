using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.UI.Mvvm;
using EtlDesigner.UI.Services;

namespace EtlDesigner.UI.ViewModels;

public enum CarryChoice
{
    All,
    None,
    List
}

/// <summary>One delimiter level as the dialog edits it.</summary>
public sealed class DelimiterLevelItem : ObservableObject
{
    private string _delimiter;
    private SplitMode _mode;
    private string? _subPartColumn;

    public DelimiterLevelItem(string delimiter, SplitMode mode, string? subPartColumn = null)
    {
        _delimiter = delimiter;
        _mode = mode;
        _subPartColumn = subPartColumn;
    }

    public string Delimiter
    {
        get => _delimiter;
        set => SetProperty(ref _delimiter, value ?? string.Empty);
    }

    public SplitMode Mode
    {
        get => _mode;
        set => SetProperty(ref _mode, value);
    }

    /// <summary>Output column for the sub-part of an Each level.</summary>
    public string? SubPartColumn
    {
        get => _subPartColumn;
        set => SetProperty(ref _subPartColumn, value);
    }

    public DelimiterLevel ToLevel() => new(Delimiter, Mode, Mode == SplitMode.Each ? SubPartColumn : null);
}

/// <summary>One slot (component position) and its value columns, by name.</summary>
public sealed class SlotItem : ObservableObject
{
    private string _name;

    public SlotItem(string name, IEnumerable<string> columns)
    {
        _name = name;
        Columns = new ObservableCollection<string>(columns);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    public ObservableCollection<string> Columns { get; }

    /// <summary>The kept columns as a drop-down check list; ticking one edits <see cref="Columns"/>.</summary>
    public ObservableCollection<ColumnChoice> Choices { get; } = new();
}

/// <summary>One entry of a check list of the kept columns.</summary>
public sealed class ColumnChoice : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked;

    public ColumnChoice(string name, bool isChecked, Action changed)
    {
        Name = name;
        _isChecked = isChecked;
        _changed = changed;
    }

    public string Name { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                _changed();
            }
        }
    }

    /// <summary>Follows the list it belongs to, without calling back.</summary>
    internal void SetQuietly(bool value)
    {
        if (_isChecked != value)
        {
            _isChecked = value;
            OnPropertyChanged(nameof(IsChecked));
        }
    }
}

/// <summary>One line of the Components list: 1-based number, name, sub-parts joined by " · ".</summary>
public sealed record ComponentRow(int Number, string Name, string SubParts);

/// <summary>A cell of the sample grid: 0-based row in the preview, column by name.</summary>
public sealed record GridCell(int Row, string Column);

/// <summary>What the sample grid has selected; <see cref="Current"/> is the cell with focus.</summary>
public sealed record GridSelection(IReadOnlyList<GridCell> Cells, GridCell? Current);

public sealed record OutputShapeOption(OutputShape Shape, string Label, bool IsAvailable);

/// <summary>What a piece of the selected cell's text is, for colouring it in the dialog.</summary>
public enum SegmentKind
{
    Key,
    Level1Delimiter,
    Level2Delimiter,
    Level3Delimiter,
    Component,
    SubPart
}

/// <summary>One piece of a cell's text. The segments of a cell join back to the exact text.</summary>
public sealed record HighlightSegment(string Text, SegmentKind Kind);

/// <summary>
/// View model of the ParseValue dialog. Works on its own copy of the node's settings: Save writes
/// them back, Cancel leaves the node unchanged. Preview rows come from the upstream CsvFile node
/// through CsvPreviewReader, with only the kept columns.
/// </summary>
public partial class ParseStudioViewModel : ObservableObject
{
    private readonly NodeViewModel _node;
    private readonly ParseValueUpstream _upstream;
    private readonly CsvPreviewReader _reader;

    private CancellationTokenSource? _loadCts;
    private Task<CsvPreview>? _currentLoadTask;
    private bool _isLoading;
    private bool _showCancelLoad;
    private string? _previewError;
    private string _sourceColumn;
    private bool _trim;
    private bool _dropEmpty;
    private ExcessPolicy _excessPolicy;
    private DuplicatePolicy _duplicatePolicy;
    private CarryChoice _carryMode;
    private OutputShape _outputShape;
    private int _selectedRowIndex = -1;
    private string? _componentsError;
    private int _autoFillGroupSize = 3;
    private string? _autoFillError;
    private string? _testError;
    private ParseValueResult? _testResult;
    private readonly string _originalSettingsJson;
    private IReadOnlyList<string> _selectedGridColumns = [];
    private string? _currentColumn;
    private string _displayedCellText = string.Empty;
    private IReadOnlyList<HighlightSegment> _displayedCellSegments = [];
    private string _displayedCellCaption = NothingSelectedText;
    private int _selectedTabIndex;
    private int _focusRowIndex = -1;
    private bool _showDiscardPrompt;
    private bool _hasLoaded;
    private long _elapsedMs;
    private DataView? _rowsView;
    private DataView? _resultRowsView;

    public const string NothingSelectedText = "Click a cell in the grid to see its full text here.";
    public const string AutoFillNoSelectionHint = "Select the value columns in the grid first (for example by Shift-clicking from the first to the last), then press Auto-fill.";

    public ParseStudioViewModel(NodeViewModel node, ParseValueUpstream upstream, CsvPreviewReader? reader = null)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _upstream = upstream ?? throw new ArgumentNullException(nameof(upstream));
        _reader = reader ?? new CsvPreviewReader();

        var settings = node.ParseValueSettings ?? NodeFactory.DefaultParseValueSettings();
        _sourceColumn = settings.SourceColumn;
        _trim = settings.Trim;
        _dropEmpty = settings.DropEmpty;
        _excessPolicy = settings.ExcessPolicy;
        _duplicatePolicy = settings.DuplicatePolicy;
        _outputShape = settings.OutputShape;
        _carryMode = settings.CarryColumns.AllOtherColumns ? CarryChoice.All
            : settings.CarryColumns.Names.Count == 0 ? CarryChoice.None
            : CarryChoice.List;

        UpstreamMessage = upstream.Settings is null ? upstream.Message : null;
        NodeTitle = node.Title;
        FileName = string.IsNullOrEmpty(upstream.Settings?.Path) ? "(no file)" : Path.GetFileName(upstream.Settings.Path);
        _originalSettingsJson = SettingsJson.Serialize(settings);

        Columns = new ObservableCollection<string>();
        Rows = new ObservableCollection<IReadOnlyList<string>>();
        DelimiterLevels = new ObservableCollection<DelimiterLevelItem>();
        Slots = new ObservableCollection<SlotItem>();
        CarryNames = new ObservableCollection<string>(settings.CarryColumns.AllOtherColumns ? [] : settings.CarryColumns.Names);
        CarryChoices = new ObservableCollection<ColumnChoice>();
        AutoFillColumns = new ObservableCollection<string>();
        Components = new ObservableCollection<ParsedComponent>();
        ComponentRows = new ObservableCollection<ComponentRow>();
        OutputColumns = new ObservableCollection<string>();
        ResultColumns = new ObservableCollection<string>();
        ResultRows = new ObservableCollection<IReadOnlyList<string>>();
        Rejects = new ObservableCollection<RecordIssue>();
        Flagged = new ObservableCollection<RecordIssue>();
        OutputShapes =
        [
            new OutputShapeOption(OutputShape.Long, "Long (one row per component)", true),
            new OutputShapeOption(OutputShape.Wide, "Wide output comes later", false)
        ];

        foreach (var level in settings.DelimiterLevels)
        {
            AddLevelItem(new DelimiterLevelItem(level.Delimiter, level.Mode, level.SubPartColumn));
        }

        foreach (var slot in settings.Slots)
        {
            AddSlotItem(new SlotItem(slot.Name, slot.Columns));
        }

        DelimiterLevels.CollectionChanged += (_, _) => OnParsingInputChanged();
        Slots.CollectionChanged += (_, _) => RefreshOutputColumns();
        CarryNames.CollectionChanged += (_, _) =>
        {
            SyncChoices(CarryChoices, CarryNames);
            RefreshOutputColumns();
        };

        LoadCommand = new AsyncRelayCommand(async _ => await ReloadNowAsync(), _ => !IsLoading && UpstreamMessage is null);
        CancelLoadCommand = new RelayCommand(_ => _loadCts?.Cancel(), _ => IsLoading && ShowCancelLoad);
        AddLevelCommand = new RelayCommand(_ => AddLevel());
        RemoveLevelCommand = new RelayCommand(p => RemoveLevel(LevelIndex(p)));
        MoveLevelUpCommand = new RelayCommand(p => MoveLevelUp(LevelIndex(p)));
        MoveLevelDownCommand = new RelayCommand(p => MoveLevelDown(LevelIndex(p)));
        AddSlotCommand = new RelayCommand(_ => AddSlot());
        RemoveSlotCommand = new RelayCommand(p => RemoveSlot(p is SlotItem slot ? Slots.IndexOf(slot) : ToIndex(p)));
        AutoFillCommand = new RelayCommand(_ => AutoFillFromSelection(), _ => SelectedGridColumns.Count > 0);
        TestParsingCommand = new RelayCommand(_ =>
        {
            TestParsing();
            SelectedTabIndex = 1;
        });
        GridSelectionChangedCommand = new RelayCommand(p =>
        {
            if (p is GridSelection selection)
            {
                SetGridSelection(selection);
            }
        });
        UseAsSourceCommand = new RelayCommand(_ => SourceColumn = CurrentColumn!, _ => CurrentColumn is not null);
        UseGridSelectionCommand = new RelayCommand(
            p => AssignColumnsToSlot(p is SlotItem slot ? Slots.IndexOf(slot) : ToIndex(p), SelectedGridColumns),
            _ => SelectedGridColumns.Count > 0);
        SelectRecordCommand = new RelayCommand(p =>
        {
            if (p is RecordIssue issue)
            {
                SelectRecord(issue.RecordNumber);
            }
        });
        SaveCommand = new RelayCommand<Window>(window =>
        {
            Save();
            Close(window, saved: true);
        });
        CancelCommand = new RelayCommand<Window>(window =>
        {
            if (HasChanges)
            {
                ShowDiscardPrompt = true;
            }
            else
            {
                Close(window, saved: false);
            }
        });
        DiscardCommand = new RelayCommand<Window>(window =>
        {
            ShowDiscardPrompt = false;
            Close(window, saved: false);
        });
        KeepEditingCommand = new RelayCommand(_ => ShowDiscardPrompt = false);

        RefreshOutputColumns();
    }

    /// <summary>Set when there is no configured CsvFile node above; says what to do.</summary>
    public string? UpstreamMessage { get; }

    /// <summary>
    /// Raised by Save, Cancel and Discard when no window was passed to the command. The argument
    /// is true after Save. The dialog service closes the window.
    /// </summary>
    public event EventHandler<bool>? CloseRequested;

    public string NodeTitle { get; }

    /// <summary>The upstream file's name, without the folder.</summary>
    public string FileName { get; }

    public long ElapsedMs
    {
        get => _elapsedMs;
        private set => SetProperty(ref _elapsedMs, value);
    }

    public string StatusText => UpstreamMessage is not null || !_hasLoaded
        ? string.Empty
        : $"Showing {Rows.Count} rows · {Columns.Count} columns · {ElapsedMs} ms";

    /// <summary>
    /// What the grid area says instead of an empty grid: the upstream message, the load error,
    /// or "no rows". Null when the grid has rows.
    /// </summary>
    public string? GridMessage => UpstreamMessage
        ?? PreviewError
        ?? (_hasLoaded && Rows.Count == 0 ? "The file has no data rows. Check the CsvFile node's settings." : null);

    /// <summary>The preview rows for the grid, with a "#" row-number column first.</summary>
    public DataView? RowsView
    {
        get => _rowsView;
        private set => SetProperty(ref _rowsView, value);
    }

    /// <summary>0 = Data tab, 1 = Result tab.</summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    /// <summary>
    /// A row the grid should select and scroll to (0-based), set when a reject is clicked. The
    /// grid behaviour reacts to each change; -1 means none.
    /// </summary>
    public int FocusRowIndex
    {
        get => _focusRowIndex;
        private set => SetProperty(ref _focusRowIndex, value);
    }

    /// <summary>Distinct columns of the selected grid cells, in file order.</summary>
    public IReadOnlyList<string> SelectedGridColumns
    {
        get => _selectedGridColumns;
        private set => SetProperty(ref _selectedGridColumns, value);
    }

    /// <summary>Column of the grid cell with focus, or null.</summary>
    public string? CurrentColumn
    {
        get => _currentColumn;
        private set => SetProperty(ref _currentColumn, value);
    }

    /// <summary>
    /// The text of the cell shown in the Selected cell pane: the cell with focus, or else the
    /// source cell of the selected row.
    /// </summary>
    public string DisplayedCellText
    {
        get => _displayedCellText;
        private set => SetProperty(ref _displayedCellText, value);
    }

    public IReadOnlyList<HighlightSegment> DisplayedCellSegments
    {
        get => _displayedCellSegments;
        private set => SetProperty(ref _displayedCellSegments, value);
    }

    /// <summary>"Row 2 · column "sample"", or the empty-state text.</summary>
    public string DisplayedCellCaption
    {
        get => _displayedCellCaption;
        private set => SetProperty(ref _displayedCellCaption, value);
    }

    /// <summary>True when the settings differ from the node's.</summary>
    public bool HasChanges => SettingsJson.Serialize(BuildSettings()) != _originalSettingsJson;

    /// <summary>Shown after Cancel with unsaved changes: Discard or Keep editing.</summary>
    public bool ShowDiscardPrompt
    {
        get => _showDiscardPrompt;
        private set => SetProperty(ref _showDiscardPrompt, value);
    }

    // ----- Preview -----

    /// <summary>The kept columns of the upstream CsvFile node, in file order.</summary>
    public ObservableCollection<string> Columns { get; }

    public ObservableCollection<IReadOnlyList<string>> Rows { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                LoadCommand.RaiseCanExecuteChanged();
                CancelLoadCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ShowCancelLoad
    {
        get => _showCancelLoad;
        private set
        {
            if (SetProperty(ref _showCancelLoad, value))
            {
                CancelLoadCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? PreviewError
    {
        get => _previewError;
        private set
        {
            if (SetProperty(ref _previewError, value))
            {
                OnPropertyChanged(nameof(GridMessage));
            }
        }
    }

    // ----- Settings being edited -----

    public string SourceColumn
    {
        get => _sourceColumn;
        set
        {
            if (SetProperty(ref _sourceColumn, value ?? string.Empty))
            {
                UpdateSelectedCell();
                RefreshOutputColumns();
                UpdateDisplayedCell();
            }
        }
    }

    public ObservableCollection<DelimiterLevelItem> DelimiterLevels { get; }

    public bool Trim
    {
        get => _trim;
        set
        {
            if (SetProperty(ref _trim, value))
            {
                RefreshComponents();
            }
        }
    }

    public bool DropEmpty
    {
        get => _dropEmpty;
        set
        {
            if (SetProperty(ref _dropEmpty, value))
            {
                RefreshComponents();
            }
        }
    }

    public ExcessPolicy ExcessPolicy
    {
        get => _excessPolicy;
        set => SetProperty(ref _excessPolicy, value);
    }

    public DuplicatePolicy DuplicatePolicy
    {
        get => _duplicatePolicy;
        set => SetProperty(ref _duplicatePolicy, value);
    }

    public CarryChoice CarryMode
    {
        get => _carryMode;
        set
        {
            if (SetProperty(ref _carryMode, value))
            {
                RefreshOutputColumns();
            }
        }
    }

    /// <summary>The carried columns when <see cref="CarryMode"/> is List.</summary>
    public ObservableCollection<string> CarryNames { get; }

    /// <summary>The kept columns as a check list that edits <see cref="CarryNames"/>.</summary>
    public ObservableCollection<ColumnChoice> CarryChoices { get; }

    public IReadOnlyList<OutputShapeOption> OutputShapes { get; }

    /// <summary>Only Long can be chosen; setting Wide is ignored (not defined in the spec yet).</summary>
    public OutputShape OutputShape
    {
        get => _outputShape;
        set
        {
            if (OutputShapes.Any(o => o.Shape == value && o.IsAvailable))
            {
                SetProperty(ref _outputShape, value);
            }
            else
            {
                OnPropertyChanged();
            }
        }
    }

    public ObservableCollection<SlotItem> Slots { get; }

    /// <summary>The columns Auto-fill cuts into slots, in the order the user picked.</summary>
    public ObservableCollection<string> AutoFillColumns { get; }

    public int AutoFillGroupSize
    {
        get => _autoFillGroupSize;
        set => SetProperty(ref _autoFillGroupSize, value);
    }

    /// <summary>Tells the user what to do while no grid columns are selected and Auto-fill is disabled.</summary>
    public string? AutoFillHint => SelectedGridColumns.Count == 0 ? AutoFillNoSelectionHint : null;

    public string? AutoFillError
    {
        get => _autoFillError;
        private set => SetProperty(ref _autoFillError, value);
    }

    // ----- Selected row and its components -----

    /// <summary>0-based index into <see cref="Rows"/>, or -1.</summary>
    public int SelectedRowIndex
    {
        get => _selectedRowIndex;
        set
        {
            if (SetProperty(ref _selectedRowIndex, value))
            {
                UpdateSelectedCell();
                UpdateDisplayedCell();
            }
        }
    }

    public string SelectedCellValue { get; private set; } = string.Empty;

    /// <summary>What CompositeCellParser gives for the selected row's source cell.</summary>
    public ObservableCollection<ParsedComponent> Components { get; }

    /// <summary><see cref="Components"/> numbered for the dialog's list.</summary>
    public ObservableCollection<ComponentRow> ComponentRows { get; }

    public string? ComponentsError
    {
        get => _componentsError;
        private set => SetProperty(ref _componentsError, value);
    }

    /// <summary>
    /// The output schema: carried columns, sample_id, component, the sub-part columns of Each
    /// levels, then value_a, value_b, ... for the largest slot. Same rule as ParseValueTransform.
    /// </summary>
    public ObservableCollection<string> OutputColumns { get; }

    // ----- Test result -----

    public int RowsIn => _testResult?.RowsIn ?? 0;

    public int RowsOut => _testResult?.RowsOut ?? 0;

    public int RowsRejected => _testResult?.RowsRejected ?? 0;

    public int RowsFlagged => _testResult?.RowsFlagged ?? 0;

    public ObservableCollection<string> ResultColumns { get; }

    public ObservableCollection<IReadOnlyList<string>> ResultRows { get; }

    /// <summary>The test output rows for the Result grid.</summary>
    public DataView? ResultRowsView
    {
        get => _resultRowsView;
        private set => SetProperty(ref _resultRowsView, value);
    }

    public string ResultSummary => _testResult is null
        ? "Press Test parsing to run the settings on the preview rows."
        : $"In {RowsIn} · Out {RowsOut} · Rejected {RowsRejected} · Flagged {RowsFlagged}";

    public ObservableCollection<RecordIssue> Rejects { get; }

    public ObservableCollection<RecordIssue> Flagged { get; }

    public string? TestError
    {
        get => _testError;
        private set => SetProperty(ref _testError, value);
    }

    // ----- Commands -----

    public AsyncRelayCommand LoadCommand { get; }

    public RelayCommand CancelLoadCommand { get; }

    public RelayCommand AddLevelCommand { get; }

    public RelayCommand RemoveLevelCommand { get; }

    public RelayCommand MoveLevelUpCommand { get; }

    public RelayCommand MoveLevelDownCommand { get; }

    public RelayCommand AddSlotCommand { get; }

    public RelayCommand RemoveSlotCommand { get; }

    public RelayCommand AutoFillCommand { get; }

    public RelayCommand TestParsingCommand { get; }

    public RelayCommand<Window> SaveCommand { get; }

    public RelayCommand<Window> CancelCommand { get; }

    public RelayCommand<Window> DiscardCommand { get; }

    public RelayCommand KeepEditingCommand { get; }

    /// <summary>Takes a <see cref="GridSelection"/> from the grid behaviour.</summary>
    public RelayCommand GridSelectionChangedCommand { get; }

    /// <summary>Makes the column of the cell with focus the source column.</summary>
    public RelayCommand UseAsSourceCommand { get; }

    /// <summary>Gives a slot (the parameter) the columns of the selected grid cells.</summary>
    public RelayCommand UseGridSelectionCommand { get; }

    /// <summary>Selects a reject's or flagged record's row (a RecordIssue) in the sample grid.</summary>
    public RelayCommand SelectRecordCommand { get; }

    // ----- Loading (same cancel and newest-reload rules as DataStudioViewModel) -----

    public async Task ReloadNowAsync(CancellationToken token = default)
    {
        if (_upstream.Settings is null)
        {
            return;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        var thisLoad = _loadCts;
        var requestToken = thisLoad.Token;
        IsLoading = true;
        ShowCancelLoad = false;

        _ = ShowCancelAfterDelayAsync(requestToken);

        _currentLoadTask = _reader.ReadAsync(_upstream.Settings, requestToken);
        CsvPreview preview;

        try
        {
            preview = await _currentLoadTask;
        }
        catch (OperationCanceledException)
        {
            EndCancelledLoad(thisLoad);
            return;
        }

        if (preview.Cancelled || requestToken.IsCancellationRequested)
        {
            EndCancelledLoad(thisLoad);
            return;
        }

        PreviewError = preview.Error;
        ElapsedMs = preview.ElapsedMs;
        if (preview.Error is null)
        {
            ApplyPreview(preview);
        }

        _hasLoaded = true;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(GridMessage));

        IsLoading = false;
        ShowCancelLoad = false;
    }

    // ----- Messages -----

    /// <summary>
    /// Removes the " (Parameter 'levels')" that .NET adds to an ArgumentException message, so the
    /// dialog shows only the sentence meant for the user. Other text is left as it is.
    /// </summary>
    public static string WithoutParameterName(string message)
        => ParameterSuffix().Replace(message ?? string.Empty, string.Empty);

    [GeneratedRegex(@"\s*\(Parameter '[^']*'\)\s*$")]
    private static partial Regex ParameterSuffix();

    // ----- Highlighting -----

    /// <summary>Splits <paramref name="cellText"/> with the delimiter levels as currently edited.</summary>
    public IReadOnlyList<HighlightSegment> HighlightSegments(string? cellText)
        => HighlightSegments(cellText, DelimiterLevels.Select(l => l.ToLevel()).ToList());

    /// <summary>
    /// Splits a cell into coloured segments, following the same rules as CompositeCellParser but
    /// keeping every character (nothing trimmed or dropped), so the pieces join back to the text.
    /// Level 1 splits once (key), level 2 splits all (components), each later level splits what
    /// is left of a component at its first delimiter (sub-parts; all shown as Level3Delimiter).
    /// A level with no delimiter text is skipped. Without the key delimiter the whole text is Key.
    /// Pure: no state.
    /// </summary>
    public static IReadOnlyList<HighlightSegment> HighlightSegments(string? cellText, IReadOnlyList<DelimiterLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);

        var segments = new List<HighlightSegment>();
        var text = cellText ?? string.Empty;
        if (text.Length == 0)
        {
            return segments;
        }

        var keyDelimiter = levels.Count > 0 ? levels[0].Delimiter : string.Empty;
        var keyEnd = keyDelimiter.Length > 0 ? text.IndexOf(keyDelimiter, StringComparison.Ordinal) : -1;
        if (keyEnd < 0)
        {
            Add(segments, text, SegmentKind.Key);
            return segments;
        }

        Add(segments, text[..keyEnd], SegmentKind.Key);
        Add(segments, keyDelimiter, SegmentKind.Level1Delimiter);
        var rest = text[(keyEnd + keyDelimiter.Length)..];

        var itemDelimiter = levels.Count > 1 ? levels[1].Delimiter : string.Empty;
        var items = itemDelimiter.Length > 0 ? rest.Split(itemDelimiter) : [rest];
        var subLevels = levels.Skip(2).Where(l => l.Delimiter.Length > 0).ToList();

        for (var i = 0; i < items.Length; i++)
        {
            if (i > 0)
            {
                Add(segments, itemDelimiter, SegmentKind.Level2Delimiter);
            }

            var current = items[i];
            var kind = SegmentKind.Component;
            foreach (var level in subLevels)
            {
                var at = current.IndexOf(level.Delimiter, StringComparison.Ordinal);
                if (at < 0)
                {
                    break;
                }

                Add(segments, current[..at], kind);
                Add(segments, level.Delimiter, SegmentKind.Level3Delimiter);
                current = current[(at + level.Delimiter.Length)..];
                kind = SegmentKind.SubPart;
            }

            Add(segments, current, kind);
        }

        return segments;
    }

    private static void Add(List<HighlightSegment> segments, string text, SegmentKind kind)
    {
        if (text.Length > 0)
        {
            segments.Add(new HighlightSegment(text, kind));
        }
    }

    // ----- Delimiter levels -----

    /// <summary>
    /// Adds a level. The first two are the key (":" once) and the components ("|" all); later
    /// ones split each component ("/" each) into a column named part_N [U].
    /// </summary>
    public void AddLevel()
    {
        var item = DelimiterLevels.Count switch
        {
            0 => new DelimiterLevelItem(":", SplitMode.Once),
            1 => new DelimiterLevelItem("|", SplitMode.All),
            _ => new DelimiterLevelItem("/", SplitMode.Each, $"part_{DelimiterLevels.Count - 1}")
        };

        AddLevelItem(item);
    }

    public void RemoveLevel(int index)
    {
        if (index < 0 || index >= DelimiterLevels.Count)
        {
            return;
        }

        DelimiterLevels[index].PropertyChanged -= OnLevelChanged;
        DelimiterLevels.RemoveAt(index);
    }

    public void MoveLevelUp(int index)
    {
        if (index > 0 && index < DelimiterLevels.Count)
        {
            DelimiterLevels.Move(index, index - 1);
        }
    }

    public void MoveLevelDown(int index)
    {
        if (index >= 0 && index < DelimiterLevels.Count - 1)
        {
            DelimiterLevels.Move(index, index + 1);
        }
    }

    // ----- Slots -----

    public void AddSlot()
    {
        var number = Slots.Count + 1;
        while (Slots.Any(s => s.Name == $"S{number}"))
        {
            number++;
        }

        AddSlotItem(new SlotItem($"S{number}", []));
    }

    public void RemoveSlot(int index)
    {
        if (index < 0 || index >= Slots.Count)
        {
            return;
        }

        Slots[index].Columns.CollectionChanged -= OnSlotColumnsChanged;
        Slots.RemoveAt(index);
    }

    /// <summary>Replaces the columns of the slot at <paramref name="index"/>, in the given order.</summary>
    public void AssignColumnsToSlot(int index, IEnumerable<string> columns)
    {
        if (index < 0 || index >= Slots.Count)
        {
            return;
        }

        var slot = Slots[index].Columns;
        slot.Clear();
        foreach (var column in columns)
        {
            slot.Add(column);
        }
    }

    /// <summary>Cuts <see cref="AutoFillColumns"/> into slots of <paramref name="groupSize"/>.</summary>
    public void AutoFill(int groupSize) => AutoFill(AutoFillColumns.ToList(), groupSize);

    /// <summary>
    /// Replaces all slots with groups of <paramref name="groupSize"/> over <paramref name="columns"/>
    /// (ParseValueSettings.SlotsFromGroups). Every slot can be edited afterwards.
    /// </summary>
    public void AutoFill(IReadOnlyList<string> columns, int groupSize)
    {
        if (groupSize < 1)
        {
            AutoFillError = "Group size must be 1 or more.";
            return;
        }

        if (columns.Count == 0)
        {
            AutoFillError = "Pick the columns to fill the slots with first.";
            return;
        }

        AutoFillError = null;
        for (var i = Slots.Count - 1; i >= 0; i--)
        {
            RemoveSlot(i);
        }

        foreach (var slot in ParseValueSettings.SlotsFromGroups(columns, groupSize))
        {
            AddSlotItem(new SlotItem(slot.Name, slot.Columns));
        }
    }

    /// <summary>
    /// Auto-fill from the columns selected in the grid. There is no fallback: guessing the value
    /// columns once turned id, batch and name into slots instead of carrying them.
    /// </summary>
    public void AutoFillFromSelection() => AutoFill(SelectedGridColumns, AutoFillGroupSize);

    // ----- Grid selection -----

    /// <summary>Takes the grid's selected cells; cells outside the preview are ignored.</summary>
    public void SetGridSelection(GridSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        bool Valid(GridCell c) => c.Row >= 0 && c.Row < Rows.Count && IndexOf(Columns, c.Column) >= 0;
        var cells = selection.Cells.Where(Valid).ToList();
        var chosen = cells.Select(c => c.Column).ToHashSet(StringComparer.Ordinal);
        SelectedGridColumns = Columns.Where(chosen.Contains).ToList();

        var current = selection.Current is not null && Valid(selection.Current) ? selection.Current : cells.LastOrDefault();
        CurrentColumn = current?.Column;
        if (current is not null)
        {
            SelectedRowIndex = current.Row;
        }

        UpdateDisplayedCell();
        UseAsSourceCommand.RaiseCanExecuteChanged();
        UseGridSelectionCommand.RaiseCanExecuteChanged();
        AutoFillCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(AutoFillHint));
    }

    /// <summary>Selects record <paramref name="recordNumber"/> (1-based) in the grid when it is in the preview.</summary>
    public void SelectRecord(int recordNumber)
    {
        var row = recordNumber - 1;
        if (row < 0 || row >= Rows.Count)
        {
            return;
        }

        SelectedTabIndex = 0;
        SelectedRowIndex = row;
        FocusRowIndex = -1;
        FocusRowIndex = row;
    }

    // ----- Test, save -----

    /// <summary>Runs ParseValueTransform on the preview rows. Never writes anywhere.</summary>
    public void TestParsing()
    {
        ResultColumns.Clear();
        ResultRows.Clear();
        Rejects.Clear();
        Flagged.Clear();
        _testResult = null;
        TestError = null;

        if (UpstreamMessage is not null)
        {
            TestError = UpstreamMessage;
        }
        else if (Columns.Count == 0)
        {
            TestError = "Load the preview first.";
        }
        else
        {
            var columns = Columns.ToList();

            // Record numbers: the preview row index plus 1 (header and skipped lines not counted).
            var records = Rows.Select((row, i) => new ParseValueRecord(
                i + 1,
                columns.Select((c, k) => (c, k)).ToDictionary(x => x.c, x => (string?)row[x.k], StringComparer.Ordinal)));

            try
            {
                _testResult = ParseValueTransform.Run(columns, records.ToList(), BuildSettings());
            }
            catch (ArgumentException ex)
            {
                TestError = WithoutParameterName(ex.Message);
            }
            catch (NotSupportedException ex)
            {
                TestError = WithoutParameterName(ex.Message);
            }
        }

        if (_testResult is not null)
        {
            foreach (var column in _testResult.OutputColumns) ResultColumns.Add(column);
            foreach (var row in _testResult.Rows) ResultRows.Add(row);
            foreach (var reject in _testResult.Rejects) Rejects.Add(reject);
            foreach (var flag in _testResult.Flagged) Flagged.Add(flag);
        }

        ResultRowsView = ResultColumns.Count == 0 ? null : BuildTable(ResultColumns, ResultRows, rowNumbers: false).DefaultView;

        OnPropertyChanged(nameof(RowsIn));
        OnPropertyChanged(nameof(RowsOut));
        OnPropertyChanged(nameof(RowsRejected));
        OnPropertyChanged(nameof(RowsFlagged));
        OnPropertyChanged(nameof(ResultSummary));
    }

    /// <summary>The settings as currently edited in the dialog.</summary>
    public ParseValueSettings BuildSettings() => new()
    {
        SourceColumn = SourceColumn,
        DelimiterLevels = DelimiterLevels.Select(l => l.ToLevel()).ToList(),
        Trim = Trim,
        DropEmpty = DropEmpty,
        Slots = Slots.Select(s => new Slot(s.Name, s.Columns.ToList())).ToList(),
        ExcessPolicy = ExcessPolicy,
        DuplicatePolicy = DuplicatePolicy,
        CarryColumns = CarryMode switch
        {
            CarryChoice.All => CarryColumns.All,
            CarryChoice.List => CarryColumns.Of(CarryNames.ToArray()),
            _ => CarryColumns.None
        },
        OutputShape = OutputShape
    };

    /// <summary>Writes the edited settings to the node. Closing without Save changes nothing.</summary>
    public void Save() => _node.ParseValueSettings = BuildSettings();

    // ----- Helpers -----

    private void ApplyPreview(CsvPreview preview)
    {
        var kept = _upstream.Selection is null
            ? preview.Columns
            : ParseValueUpstreamResolver.KeptColumns(preview.Columns, _upstream.Selection);
        var positions = kept.Select(c => IndexOf(preview.Columns, c)).ToList();

        Columns.Clear();
        foreach (var column in kept)
        {
            Columns.Add(column);
        }

        Rows.Clear();
        foreach (var row in preview.Rows)
        {
            Rows.Add(positions.Select(i => i < row.Count ? row[i] : string.Empty).ToList());
        }

        RowsView = BuildTable(Columns, Rows, rowNumbers: true).DefaultView;
        foreach (var slot in Slots)
        {
            RebuildChoices(slot.Choices, slot.Columns);
        }

        RebuildChoices(CarryChoices, CarryNames);

        // Select the first row so the components show at once [U].
        if (_selectedRowIndex < 0 || _selectedRowIndex >= Rows.Count)
        {
            _selectedRowIndex = Rows.Count > 0 ? 0 : -1;
            OnPropertyChanged(nameof(SelectedRowIndex));
        }

        UpdateSelectedCell();
        RefreshOutputColumns();
        UpdateDisplayedCell();
    }

    private void UpdateDisplayedCell()
    {
        var column = CurrentColumn ?? (IndexOf(Columns, SourceColumn) >= 0 ? SourceColumn : null);
        var columnIndex = column is null ? -1 : IndexOf(Columns, column);
        if (SelectedRowIndex < 0 || SelectedRowIndex >= Rows.Count || columnIndex < 0)
        {
            DisplayedCellText = string.Empty;
            DisplayedCellSegments = [];
            DisplayedCellCaption = NothingSelectedText;
            return;
        }

        DisplayedCellText = Rows[SelectedRowIndex][columnIndex];
        DisplayedCellSegments = HighlightSegments(DisplayedCellText);
        DisplayedCellCaption = $"Row {SelectedRowIndex + 1} · column \"{column}\"";
    }

    /// <summary>
    /// A table for a grid. A repeated name gets " (2)", " (3)" because a DataTable needs unique
    /// column names [U]. The first column is "#" (row number) when asked.
    /// </summary>
    private static DataTable BuildTable(IEnumerable<string> columns, IEnumerable<IReadOnlyList<string>> rows, bool rowNumbers)
    {
        var table = new DataTable();
        if (rowNumbers)
        {
            table.Columns.Add("#", typeof(int));
        }

        foreach (var column in columns)
        {
            var name = column;
            for (var n = 2; table.Columns.Contains(name); n++)
            {
                name = $"{column} ({n})";
            }

            table.Columns.Add(name, typeof(string));
        }

        var offset = rowNumbers ? 1 : 0;
        var number = 1;
        foreach (var row in rows)
        {
            var values = new object[table.Columns.Count];
            if (rowNumbers)
            {
                values[0] = number;
            }

            for (var i = offset; i < values.Length; i++)
            {
                values[i] = i - offset < row.Count ? row[i - offset] : string.Empty;
            }

            table.Rows.Add(values);
            number++;
        }

        return table;
    }

    private void RebuildChoices(ObservableCollection<ColumnChoice> choices, ObservableCollection<string> target)
    {
        var chosen = target.ToHashSet(StringComparer.Ordinal);
        choices.Clear();
        foreach (var column in Columns)
        {
            choices.Add(new ColumnChoice(column, chosen.Contains(column), () => ApplyChoices(choices, target)));
        }
    }

    private static void SyncChoices(IEnumerable<ColumnChoice> choices, IEnumerable<string> target)
    {
        var chosen = target.ToHashSet(StringComparer.Ordinal);
        foreach (var choice in choices)
        {
            choice.SetQuietly(chosen.Contains(choice.Name));
        }
    }

    /// <summary>Ticked columns in file order, then any names the list does not show (kept as they are).</summary>
    private static void ApplyChoices(IReadOnlyCollection<ColumnChoice> choices, ObservableCollection<string> target)
    {
        var listed = choices.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var names = choices.Where(c => c.IsChecked).Select(c => c.Name)
            .Concat(target.Where(n => !listed.Contains(n)))
            .ToList();

        target.Clear();
        foreach (var name in names)
        {
            target.Add(name);
        }
    }

    private void Close(Window? window, bool saved)
    {
        if (window is null)
        {
            CloseRequested?.Invoke(this, saved);
            return;
        }

        if (saved)
        {
            window.DialogResult = true;
        }

        window.Close();
    }

    private int LevelIndex(object? parameter)
        => parameter is DelimiterLevelItem level ? DelimiterLevels.IndexOf(level) : ToIndex(parameter);

    private void UpdateSelectedCell()
    {
        var column = IndexOf(Columns, SourceColumn);
        SelectedCellValue = SelectedRowIndex >= 0 && SelectedRowIndex < Rows.Count && column >= 0
            ? Rows[SelectedRowIndex][column]
            : string.Empty;
        OnPropertyChanged(nameof(SelectedCellValue));
        RefreshComponents();
    }

    private void RefreshComponents()
    {
        Components.Clear();
        ComponentRows.Clear();
        ComponentsError = null;

        if (SelectedRowIndex < 0 || IndexOf(Columns, SourceColumn) < 0)
        {
            return;
        }

        try
        {
            var cell = CompositeCellParser.Parse(SelectedCellValue, DelimiterLevels.Select(l => l.ToLevel()).ToList(), Trim, DropEmpty);
            if (!cell.IsValid)
            {
                ComponentsError = cell.Error;
                return;
            }

            foreach (var component in cell.Components)
            {
                Components.Add(component);
                ComponentRows.Add(new ComponentRow(Components.Count, component.Name, string.Join(" · ", component.SubParts)));
            }
        }
        catch (ArgumentException ex)
        {
            ComponentsError = WithoutParameterName(ex.Message);
        }
    }

    private void RefreshOutputColumns()
    {
        var slotColumns = Slots.SelectMany(s => s.Columns).ToHashSet(StringComparer.Ordinal);
        var carried = CarryMode switch
        {
            CarryChoice.All => Columns.Where(c => c != SourceColumn && !slotColumns.Contains(c)).ToList(),
            CarryChoice.List => CarryNames.ToList(),
            _ => new List<string>()
        };

        var output = new List<string>(carried) { ParseValueTransform.KeyColumn, ParseValueTransform.ComponentColumn };
        output.AddRange(DelimiterLevels.Skip(2).Where(l => l.Mode == SplitMode.Each).Select(l => l.SubPartColumn ?? string.Empty));
        var valueWidth = Slots.Count == 0 ? 0 : Slots.Max(s => s.Columns.Count);
        output.AddRange(Enumerable.Range(0, valueWidth).Select(ParseValueSettings.ValueColumnName));

        OutputColumns.Clear();
        foreach (var column in output)
        {
            OutputColumns.Add(column);
        }
    }

    private void AddLevelItem(DelimiterLevelItem item)
    {
        item.PropertyChanged += OnLevelChanged;
        DelimiterLevels.Add(item);
    }

    private void AddSlotItem(SlotItem item)
    {
        item.Columns.CollectionChanged += OnSlotColumnsChanged;
        RebuildChoices(item.Choices, item.Columns);
        Slots.Add(item);
    }

    private void OnLevelChanged(object? sender, PropertyChangedEventArgs e) => OnParsingInputChanged();

    private void OnSlotColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Slots.FirstOrDefault(s => ReferenceEquals(s.Columns, sender)) is { } slot)
        {
            SyncChoices(slot.Choices, slot.Columns);
        }

        RefreshOutputColumns();
    }

    private void OnParsingInputChanged()
    {
        RefreshComponents();
        RefreshOutputColumns();
        UpdateDisplayedCell();
    }

    private void EndCancelledLoad(CancellationTokenSource load)
    {
        if (!ReferenceEquals(load, _loadCts))
        {
            return;
        }

        IsLoading = false;
        ShowCancelLoad = false;
    }

    private async Task ShowCancelAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(500, token);
            if (!token.IsCancellationRequested && IsLoading)
            {
                ShowCancelLoad = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static int IndexOf(IEnumerable<string> columns, string name)
    {
        var i = 0;
        foreach (var column in columns)
        {
            if (string.Equals(column, name, StringComparison.Ordinal))
            {
                return i;
            }

            i++;
        }

        return -1;
    }

    private static int ToIndex(object? parameter) => parameter switch
    {
        int i => i,
        string s when int.TryParse(s, out var i) => i,
        _ => -1
    };
}
