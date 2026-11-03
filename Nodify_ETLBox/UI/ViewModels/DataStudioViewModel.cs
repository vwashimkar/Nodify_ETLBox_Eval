using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EtlDesigner.Engine.Csv;
using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

public sealed class DataStudioColumnItem : ObservableObject
{
    private bool _isSelected;

    public DataStudioColumnItem(string name, string sampleValue, bool isSelected)
    {
        Name = name;
        SampleValue = sampleValue;
        _isSelected = isSelected;
    }

    public string Name { get; }

    public string SampleValue { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public class DataStudioViewModel : ObservableObject
{
    private readonly NodeViewModel _node;
    private readonly CsvPreviewReader _reader;
    private readonly CsvFileSettings _initialSettings;
    private readonly CsvColumnSelection _workingSelection;

    private DataTable? _allRowsTable;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _reloadDebounceCts;
    private Task<CsvPreview>? _currentLoadTask;
    private bool _isLoading;
    private bool _showCancelLoad;
    private string? _previewError;
    private string _separator;
    private bool _hasHeader;
    private int _skipLines;
    private string _encodingName;
    private int _previewRows;
    private bool _hideIgnoredColumnsInGrid;
    private string _columnFilter;
    private DataView? _dataRowsView;
    private DataView? _resultRowsView;
    private string _selectedCellValue;
    private DataGridCellInfo _currentCell;
    private int _selectedTabIndex;
    private int _missingSelectedNamesCount;
    private bool _isIgnoreMode;

    public DataStudioViewModel(NodeViewModel node, CsvFileSettings settings, CsvPreviewReader? reader = null)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _reader = reader ?? new CsvPreviewReader();
        _initialSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        _workingSelection = node.ColumnSelection.Clone();

        // Show the preset name (Comma, Tab, ...) rather than the raw character.
        _separator = ToNodeSeparatorValue(settings.Separator ?? ",");
        _hasHeader = settings.HasHeader;
        _skipLines = settings.SkipLines;
        _encodingName = settings.EncodingName;
        _previewRows = settings.PreviewRows;
        _hideIgnoredColumnsInGrid = false;
        _columnFilter = string.Empty;
        _selectedCellValue = string.Empty;
        _isIgnoreMode = _workingSelection.Mode == CsvColumnSelectionMode.Ignore;

        FileName = Path.GetFileName(settings.Path);

        SeparatorPresets = new ObservableCollection<string>(new[] { "Comma", "Semicolon", "Tab", "Pipe" });
        EncodingPresets = new ObservableCollection<string>(new[] { "utf-8", "windows-1252", "utf-16", "iso-8859-1", "us-ascii" });
        Columns = new ObservableCollection<DataStudioColumnItem>();
        FilteredColumns = new ObservableCollection<DataStudioColumnItem>();

        ReloadCommand = new AsyncRelayCommand(async _ => await ReloadNowAsync(), _ => !IsLoading);
        CancelLoadCommand = new RelayCommand(_ => CancelLoad(), _ => IsLoading && ShowCancelLoad);
        SaveCommand = new RelayCommand<Window>(window => SaveAndClose(window));
        CancelCommand = new RelayCommand<Window>(window => window?.Close());
        TestDatasetCommand = new RelayCommand(_ => OpenResultTab());
        SelectAllColumnsCommand = new RelayCommand(_ => SetSelection(true));
        SelectNoColumnsCommand = new RelayCommand(_ => SetSelection(false));
        InvertColumnsCommand = new RelayCommand(_ => InvertSelection());

        _ = ReloadNowAsync();
    }

    public string FileName { get; }

    public ObservableCollection<string> SeparatorPresets { get; }

    public ObservableCollection<string> EncodingPresets { get; }

    public ObservableCollection<DataStudioColumnItem> Columns { get; }

    public ObservableCollection<DataStudioColumnItem> FilteredColumns { get; }

    public DataView? DataRowsView
    {
        get => _dataRowsView;
        private set => SetProperty(ref _dataRowsView, value);
    }

    public DataView? ResultRowsView
    {
        get => _resultRowsView;
        private set => SetProperty(ref _resultRowsView, value);
    }

    public string Separator
    {
        get => _separator;
        set
        {
            if (SetProperty(ref _separator, value ?? ","))
            {
                ScheduleReload();
            }
        }
    }

    public bool HasHeader
    {
        get => _hasHeader;
        set
        {
            if (SetProperty(ref _hasHeader, value))
            {
                ScheduleReload();
            }
        }
    }

    public int SkipLines
    {
        get => _skipLines;
        set
        {
            if (SetProperty(ref _skipLines, value < 0 ? 0 : value))
            {
                ScheduleReload();
            }
        }
    }

    public string EncodingName
    {
        get => _encodingName;
        set
        {
            if (SetProperty(ref _encodingName, value ?? "utf-8"))
            {
                ScheduleReload();
            }
        }
    }

    public int PreviewRows
    {
        get => _previewRows;
        set
        {
            var normalized = value < 1 ? 1 : value;
            if (SetProperty(ref _previewRows, normalized))
            {
                ScheduleReload();
            }
        }
    }

    public bool HideIgnoredColumnsInGrid
    {
        get => _hideIgnoredColumnsInGrid;
        set
        {
            if (SetProperty(ref _hideIgnoredColumnsInGrid, value))
            {
                RefreshDataGridView();
            }
        }
    }

    public string ColumnFilter
    {
        get => _columnFilter;
        set
        {
            if (SetProperty(ref _columnFilter, value ?? string.Empty))
            {
                RebuildFilteredColumns();
            }
        }
    }

    public bool IsIgnoreMode
    {
        get => _isIgnoreMode;
        set
        {
            if (SetProperty(ref _isIgnoreMode, value))
            {
                _workingSelection.Mode = value ? CsvColumnSelectionMode.Ignore : CsvColumnSelectionMode.KeepOnly;
                RefreshSelectionCacheFromItems();
                RefreshDataGridView();
                RefreshResultView();
                OnPropertyChanged(nameof(IsKeepOnlyMode));
            }
        }
    }

    public bool IsKeepOnlyMode
    {
        get => !IsIgnoreMode;
        set
        {
            if (value)
            {
                IsIgnoreMode = false;
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                ReloadCommand.RaiseCanExecuteChanged();
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

    public string EmptyStateMessage
        => DataRowsView is null || DataRowsView.Count == 0 ? "This file has no data rows." : string.Empty;

    public int LoadedRowCount => DataRowsView?.Count ?? 0;

    public int TotalColumnCount => Math.Max(0, (_allRowsTable?.Columns.Count ?? 1) - 1);

    public int DisplayedColumnCount => Math.Max(0, (DataRowsView?.Table.Columns.Count ?? 1) - 1);

    public bool HasMoreRows { get; private set; }

    public long ElapsedMs { get; private set; }

    public int MissingSelectedNamesCount
    {
        get => _missingSelectedNamesCount;
        private set => SetProperty(ref _missingSelectedNamesCount, value);
    }

    public string StatusText
    {
        get
        {
            var more = HasMoreRows ? " of more in the file" : string.Empty;
            var missing = MissingSelectedNamesCount > 0 ? $" · {MissingSelectedNamesCount} saved names missing" : string.Empty;
            return $"Showing {LoadedRowCount} rows{more} · Columns {DisplayedColumnCount}/{TotalColumnCount} · {ElapsedMs} ms · {FileName}{missing}";
        }
    }

    public string ResultSummary
        => $"Columns: {Math.Max(0, (ResultRowsView?.Table.Columns.Count ?? 1) - 1)} of {TotalColumnCount}";

    public DataGridCellInfo CurrentCell
    {
        get => _currentCell;
        set
        {
            if (SetProperty(ref _currentCell, value))
            {
                UpdateSelectedCellValue();
            }
        }
    }

    public string SelectedCellValue
    {
        get => _selectedCellValue;
        private set => SetProperty(ref _selectedCellValue, value);
    }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public AsyncRelayCommand ReloadCommand { get; }

    public RelayCommand CancelLoadCommand { get; }

    public RelayCommand<Window> SaveCommand { get; }

    public RelayCommand<Window> CancelCommand { get; }

    public RelayCommand TestDatasetCommand { get; }

    public RelayCommand SelectAllColumnsCommand { get; }

    public RelayCommand SelectNoColumnsCommand { get; }

    public RelayCommand InvertColumnsCommand { get; }

    public async Task WaitForCurrentLoadAsync()
    {
        var attempts = 0;
        while ((_currentLoadTask == null || IsLoading) && attempts < 500)
        {
            attempts++;
            await Task.Delay(10);
        }

        if (_currentLoadTask != null)
        {
            await _currentLoadTask;
        }
    }

    public async Task ReloadNowAsync(CancellationToken token = default)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        var requestToken = _loadCts.Token;
        IsLoading = true;
        ShowCancelLoad = false;

        _ = ShowCancelAfterDelayAsync(requestToken);

        var request = BuildSettingsRequest();

        _currentLoadTask = _reader.ReadAsync(request, requestToken);
        CsvPreview preview;

        try
        {
            preview = await _currentLoadTask;
        }
        catch (OperationCanceledException)
        {
            IsLoading = false;
            ShowCancelLoad = false;
            return;
        }

        if (requestToken.IsCancellationRequested)
        {
            IsLoading = false;
            ShowCancelLoad = false;
            return;
        }

        ElapsedMs = preview.ElapsedMs;
        HasMoreRows = preview.HasMoreRows;
        PreviewError = preview.Error;

        if (preview.Error != null)
        {
            IsLoading = false;
            ShowCancelLoad = false;
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        ApplyPreview(preview);

        IsLoading = false;
        ShowCancelLoad = false;
    }

    private void ApplyPreview(CsvPreview preview)
    {
        var oldSelected = _workingSelection.Names.ToHashSet(StringComparer.OrdinalIgnoreCase);

        _allRowsTable = new DataTable();
        _allRowsTable.Columns.Add("#", typeof(int));

        foreach (var column in preview.Columns)
        {
            _allRowsTable.Columns.Add(column, typeof(string));
        }

        for (var rowIndex = 0; rowIndex < preview.Rows.Count; rowIndex++)
        {
            var row = _allRowsTable.NewRow();
            row[0] = rowIndex + 1;

            var values = preview.Rows[rowIndex];
            for (var colIndex = 0; colIndex < preview.Columns.Count; colIndex++)
            {
                row[colIndex + 1] = colIndex < values.Count ? values[colIndex] : string.Empty;
            }

            _allRowsTable.Rows.Add(row);
        }

        var missing = oldSelected.Count(name => !preview.Columns.Contains(name, StringComparer.OrdinalIgnoreCase));
        MissingSelectedNamesCount = missing;

        Columns.Clear();
        for (var i = 0; i < preview.Columns.Count; i++)
        {
            var columnName = preview.Columns[i];
            var sample = preview.Rows.Count > 0
                ? preview.Rows[0][i]
                : string.Empty;

            var selected = oldSelected.Contains(columnName);
            var item = new DataStudioColumnItem(columnName, sample, selected);
            item.PropertyChanged += OnColumnItemChanged;
            Columns.Add(item);
        }

        RefreshSelectionCacheFromItems();
        RebuildFilteredColumns();
        RefreshDataGridView();
        RefreshResultView();
        UpdateSelectedCellValue();

        OnPropertyChanged(nameof(LoadedRowCount));
        OnPropertyChanged(nameof(TotalColumnCount));
        OnPropertyChanged(nameof(DisplayedColumnCount));
        OnPropertyChanged(nameof(EmptyStateMessage));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ResultSummary));
    }

    private void RefreshDataGridView()
    {
        if (_allRowsTable == null)
        {
            DataRowsView = null;
            OnPropertyChanged(nameof(DisplayedColumnCount));
            OnPropertyChanged(nameof(EmptyStateMessage));
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        var visible = GetDataGridVisibleColumns();
        DataRowsView = BuildView(_allRowsTable, visible).DefaultView;

        OnPropertyChanged(nameof(DisplayedColumnCount));
        OnPropertyChanged(nameof(EmptyStateMessage));
        OnPropertyChanged(nameof(StatusText));
    }

    private void RefreshResultView()
    {
        if (_allRowsTable == null)
        {
            ResultRowsView = null;
            OnPropertyChanged(nameof(ResultSummary));
            return;
        }

        var selected = GetResultColumns();
        ResultRowsView = BuildView(_allRowsTable, selected).DefaultView;

        OnPropertyChanged(nameof(ResultSummary));
    }

    private static DataTable BuildView(DataTable source, IReadOnlyList<string> columns)
    {
        var table = new DataTable();

        foreach (var column in columns)
        {
            table.Columns.Add(column, source.Columns[column].DataType);
        }

        foreach (DataRow row in source.Rows)
        {
            var newRow = table.NewRow();
            foreach (var column in columns)
            {
                newRow[column] = row[column];
            }

            table.Rows.Add(newRow);
        }

        return table;
    }

    private IReadOnlyList<string> GetDataGridVisibleColumns()
    {
        if (_allRowsTable == null)
        {
            return [];
        }

        var all = _allRowsTable.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
        if (!HideIgnoredColumnsInGrid)
        {
            return all;
        }

        var selectedNames = _workingSelection.Names.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dataColumns = all.Where(c => c != "#").ToList();
        var visibleData = _workingSelection.Mode == CsvColumnSelectionMode.Ignore
            ? dataColumns.Where(c => !selectedNames.Contains(c)).ToList()
            : dataColumns.Where(c => selectedNames.Contains(c)).ToList();

        var visible = new List<string> { "#" };
        visible.AddRange(visibleData);
        return visible;
    }

    private IReadOnlyList<string> GetResultColumns()
    {
        if (_allRowsTable == null)
        {
            return [];
        }

        var selected = _workingSelection.Names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dataColumns = _allRowsTable.Columns.Cast<DataColumn>()
            .Select(c => c.ColumnName)
            .Where(c => c != "#")
            .ToList();

        var finalColumns = _workingSelection.Mode == CsvColumnSelectionMode.Ignore
            ? dataColumns.Where(c => !selected.Contains(c)).ToList()
            : dataColumns.Where(c => selected.Contains(c)).ToList();

        var withRecord = new List<string> { "#" };
        withRecord.AddRange(finalColumns);
        return withRecord;
    }

    private void RebuildFilteredColumns()
    {
        FilteredColumns.Clear();

        var filter = ColumnFilter?.Trim();
        foreach (var item in Columns)
        {
            if (string.IsNullOrWhiteSpace(filter)
                || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredColumns.Add(item);
            }
        }
    }

    private void OnColumnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DataStudioColumnItem.IsSelected))
        {
            return;
        }

        RefreshSelectionCacheFromItems();
        RefreshDataGridView();
        RefreshResultView();
    }

    private void RefreshSelectionCacheFromItems()
    {
        _workingSelection.Names.Clear();
        foreach (var item in Columns.Where(c => c.IsSelected))
        {
            _workingSelection.Names.Add(item.Name);
        }

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ResultSummary));
    }

    private void SetSelection(bool selected)
    {
        foreach (var item in Columns)
        {
            item.IsSelected = selected;
        }

        RefreshSelectionCacheFromItems();
        RefreshDataGridView();
        RefreshResultView();
    }

    private void InvertSelection()
    {
        foreach (var item in Columns)
        {
            item.IsSelected = !item.IsSelected;
        }

        RefreshSelectionCacheFromItems();
        RefreshDataGridView();
        RefreshResultView();
    }

    private void OpenResultTab()
    {
        RefreshResultView();
        SelectedTabIndex = 2;
    }

    private void CancelLoad()
    {
        _loadCts?.Cancel();
    }

    private void SaveAndClose(Window? window)
    {
        WriteBackToNode();
        if (window != null)
        {
            window.DialogResult = true;
            window.Close();
        }
    }

    private void WriteBackToNode()
    {
        SetSettingValue("Separator", ToNodeSeparatorValue(Separator));

        var hasHeaderSetting = _node.Settings.FirstOrDefault(s => s.Key == "HasHeader");
        if (hasHeaderSetting != null)
        {
            hasHeaderSetting.BoolValue = HasHeader;
        }

        SetSettingValue("SkipLines", SkipLines.ToString());
        SetSettingValue("EncodingName", EncodingName);
        SetSettingValue("PreviewRows", PreviewRows.ToString());

        _node.ColumnSelection.Mode = _workingSelection.Mode;
        _node.ColumnSelection.Names.Clear();
        _node.ColumnSelection.Names.AddRange(_workingSelection.Names);
    }

    private void SetSettingValue(string key, string value)
    {
        var setting = _node.Settings.FirstOrDefault(s => s.Key == key);
        if (setting != null)
        {
            setting.Value = value ?? string.Empty;
        }
    }

    private CsvFileSettings BuildSettingsRequest()
    {
        var separator = Separator?.Trim() ?? ",";
        separator = string.Equals(separator, "Comma", StringComparison.OrdinalIgnoreCase) ? "," : separator;
        separator = string.Equals(separator, "Semicolon", StringComparison.OrdinalIgnoreCase) ? ";" : separator;
        separator = string.Equals(separator, "Pipe", StringComparison.OrdinalIgnoreCase) ? "|" : separator;
        separator = string.Equals(separator, "Tab", StringComparison.OrdinalIgnoreCase) ? "tab" : separator;

        return new CsvFileSettings(
            _initialSettings.Path,
            separator,
            HasHeader,
            SkipLines,
            string.IsNullOrWhiteSpace(EncodingName) ? "utf-8" : EncodingName,
            PreviewRows);
    }

    private static string ToNodeSeparatorValue(string separator)
    {
        if (string.Equals(separator, ",", StringComparison.Ordinal)) return "Comma";
        if (string.Equals(separator, ";", StringComparison.Ordinal)) return "Semicolon";
        if (string.Equals(separator, "|", StringComparison.Ordinal)) return "Pipe";
        if (string.Equals(separator, "tab", StringComparison.OrdinalIgnoreCase) || string.Equals(separator, "\\t", StringComparison.Ordinal)) return "Tab";
        return separator;
    }

    private void UpdateSelectedCellValue()
    {
        if (CurrentCell.Item is not DataRowView row || CurrentCell.Column == null)
        {
            SelectedCellValue = string.Empty;
            return;
        }

        var columnName = CurrentCell.Column.SortMemberPath;
        if (string.IsNullOrWhiteSpace(columnName) || !row.Row.Table.Columns.Contains(columnName))
        {
            SelectedCellValue = string.Empty;
            return;
        }

        SelectedCellValue = row[columnName]?.ToString() ?? string.Empty;
    }

    private void ScheduleReload()
    {
        _reloadDebounceCts?.Cancel();
        _reloadDebounceCts?.Dispose();

        _reloadDebounceCts = new CancellationTokenSource();
        var token = _reloadDebounceCts.Token;

        _ = DebouncedReloadAsync(token);
    }

    private async Task DebouncedReloadAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(400, token);
            if (!token.IsCancellationRequested)
            {
                await ReloadNowAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ShowCancelAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(500, token);
            if (!token.IsCancellationRequested)
            {
                ShowCancelLoad = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
