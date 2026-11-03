using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;
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
}

public sealed record OutputShapeOption(OutputShape Shape, string Label, bool IsAvailable);

/// <summary>
/// View model of the ParseValue dialog. Works on its own copy of the node's settings: Save writes
/// them back, Cancel leaves the node unchanged. Preview rows come from the upstream CsvFile node
/// through CsvPreviewReader, with only the kept columns.
/// </summary>
public class ParseStudioViewModel : ObservableObject
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

        Columns = new ObservableCollection<string>();
        Rows = new ObservableCollection<IReadOnlyList<string>>();
        DelimiterLevels = new ObservableCollection<DelimiterLevelItem>();
        Slots = new ObservableCollection<SlotItem>();
        CarryNames = new ObservableCollection<string>(settings.CarryColumns.AllOtherColumns ? [] : settings.CarryColumns.Names);
        AutoFillColumns = new ObservableCollection<string>();
        Components = new ObservableCollection<ParsedComponent>();
        OutputColumns = new ObservableCollection<string>();
        ResultColumns = new ObservableCollection<string>();
        ResultRows = new ObservableCollection<IReadOnlyList<string>>();
        Rejects = new ObservableCollection<RecordIssue>();
        Flagged = new ObservableCollection<RecordIssue>();
        OutputShapes =
        [
            new OutputShapeOption(OutputShape.Long, "Long (one row per component)", true),
            new OutputShapeOption(OutputShape.Wide, "Wide (not available yet)", false)
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
        CarryNames.CollectionChanged += (_, _) => RefreshOutputColumns();

        LoadCommand = new AsyncRelayCommand(async _ => await ReloadNowAsync(), _ => !IsLoading && UpstreamMessage is null);
        CancelLoadCommand = new RelayCommand(_ => _loadCts?.Cancel(), _ => IsLoading && ShowCancelLoad);
        AddLevelCommand = new RelayCommand(_ => AddLevel());
        RemoveLevelCommand = new RelayCommand(p => RemoveLevel(ToIndex(p)));
        MoveLevelUpCommand = new RelayCommand(p => MoveLevelUp(ToIndex(p)));
        MoveLevelDownCommand = new RelayCommand(p => MoveLevelDown(ToIndex(p)));
        AddSlotCommand = new RelayCommand(_ => AddSlot());
        RemoveSlotCommand = new RelayCommand(p => RemoveSlot(ToIndex(p)));
        AutoFillCommand = new RelayCommand(_ => AutoFill(AutoFillGroupSize));
        TestParsingCommand = new RelayCommand(_ => TestParsing());
        SaveCommand = new RelayCommand<Window>(window =>
        {
            Save();
            if (window != null)
            {
                window.DialogResult = true;
                window.Close();
            }
        });
        CancelCommand = new RelayCommand<Window>(window => window?.Close());

        RefreshOutputColumns();
    }

    /// <summary>Set when there is no configured CsvFile node above; says what to do.</summary>
    public string? UpstreamMessage { get; }

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
        private set => SetProperty(ref _previewError, value);
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
            }
        }
    }

    public string SelectedCellValue { get; private set; } = string.Empty;

    /// <summary>What CompositeCellParser gives for the selected row's source cell.</summary>
    public ObservableCollection<ParsedComponent> Components { get; }

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
        if (preview.Error is null)
        {
            ApplyPreview(preview);
        }

        IsLoading = false;
        ShowCancelLoad = false;
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
                TestError = ex.Message;
            }
            catch (NotSupportedException ex)
            {
                TestError = ex.Message;
            }
        }

        if (_testResult is not null)
        {
            foreach (var column in _testResult.OutputColumns) ResultColumns.Add(column);
            foreach (var row in _testResult.Rows) ResultRows.Add(row);
            foreach (var reject in _testResult.Rejects) Rejects.Add(reject);
            foreach (var flag in _testResult.Flagged) Flagged.Add(flag);
        }

        OnPropertyChanged(nameof(RowsIn));
        OnPropertyChanged(nameof(RowsOut));
        OnPropertyChanged(nameof(RowsRejected));
        OnPropertyChanged(nameof(RowsFlagged));
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

        // Select the first row so the components show at once [U].
        if (_selectedRowIndex < 0 || _selectedRowIndex >= Rows.Count)
        {
            _selectedRowIndex = Rows.Count > 0 ? 0 : -1;
            OnPropertyChanged(nameof(SelectedRowIndex));
        }

        UpdateSelectedCell();
        RefreshOutputColumns();
    }

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
            }
        }
        catch (ArgumentException ex)
        {
            ComponentsError = ex.Message;
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
        Slots.Add(item);
    }

    private void OnLevelChanged(object? sender, PropertyChangedEventArgs e) => OnParsingInputChanged();

    private void OnSlotColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshOutputColumns();

    private void OnParsingInputChanged()
    {
        RefreshComponents();
        RefreshOutputColumns();
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
