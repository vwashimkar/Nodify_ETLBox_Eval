using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Intake;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.UI.Mvvm;
using EtlDesigner.UI.Services;

namespace EtlDesigner.UI.ViewModels;

/// <summary>One column of the chosen table in the Columns step: keep it or not, with the value of the first row.</summary>
public sealed class GuidedColumn : ObservableObject
{
    private readonly Action _changed;
    private bool _isKept = true;

    public GuidedColumn(string name, string sample, Action changed)
    {
        Name = name;
        Sample = sample;
        _changed = changed;
    }

    public string Name { get; }

    public string Sample { get; }

    public bool IsKept
    {
        get => _isKept;
        set
        {
            if (SetProperty(ref _isKept, value))
            {
                _changed();
            }
        }
    }
}

/// <summary>The Columns, Try it and Save as DLL steps of the guided builder (task G7a).</summary>
public sealed partial class GuidedBuilderViewModel
{
    public const string NothingToSplitText = "Nothing to split: '{0}' was not found in column {1}.";
    public const string LicenceNote = "The reader uses ETLBox: where it runs, a valid ETLBox licence is needed to read more than about 5,000 rows.";
    public const string HelperFilesNote = "Copy YamlDotNet.dll (version 18.1.0) and the ETLBox DLLs next to the reader DLL where it runs.";
    public const string SameLayoutText = "Same layout found. {0} rows.";
    public const int TryShownRows = 200;
    public const int ColumnsPreviewRows = 20;
    public const string OtherSeparatorValue = "other";

    private bool _splitEnabled;
    private string? _splitColumn;
    private string _selectedSeparator = "pipe";
    private string _otherSeparator = string.Empty;
    private string _partNamesText = string.Empty;
    private bool _partNamesEdited;
    private bool _settingPartNames;
    private DataView? _columnsPreview;
    private bool _isTrying;
    private bool _trySucceeded;
    private string? _tryMessage;
    private string? _tryDetails;
    private string? _tryCountsText;
    private DataView? _tryRows;
    private string _otherFilePath = string.Empty;
    private bool _isTestingOther;
    private string? _otherFileText;
    private string? _otherFileDetails;
    private GenerateDllViewModel? _dll;
    private string? _saveMessage;
    private string? _templateText;
    private Guid? _savedTemplateId;
    private CancellationTokenSource? _runCancel;
    private EtlDesigner.Engine.Pipeline.PipelineDefinition? _triedDefinition;
    private PipelineResult? _triedResult;

    public IReadOnlyList<SettingChoice> SeparatorChoices { get; } =
    [
        new("comma", "Comma ( , )", true),
        new("semicolon", "Semicolon ( ; )", true),
        new("colon", "Colon ( : )", true),
        new("pipe", "Pipe ( | )", true),
        new("tab", "Tab", true),
        new("space", "Space", true),
        new(OtherSeparatorValue, "Other", true)
    ];

    public RelayCommand TryCommand { get; private set; } = null!;

    public RelayCommand TestOtherCommand { get; private set; } = null!;

    public RelayCommand CreateDllCommand { get; private set; } = null!;

    public RelayCommand SaveSetupCommand { get; private set; } = null!;

    public RelayCommand OpenCanvasCommand { get; private set; } = null!;

    public RelayCommand BrowseFolderCommand { get; private set; } = null!;

    private void CreateRunCommands()
    {
        TryCommand = new RelayCommand(_ => TryTask = TryAsync(), _ => Step == GuidedStep.Try && !IsBusy);
        TestOtherCommand = new RelayCommand(_ => PickOtherFile(), _ => TrySucceeded && !IsBusy && _pickFile is not null);
        CreateDllCommand = new RelayCommand(_ => CreateDllTask = CreateDllAsync(), _ => Dll is { CanGenerate: true } && !IsBusy);
        SaveSetupCommand = new RelayCommand(_ => _main.SaveProjectAsCommand.Execute(null), _ => Step == GuidedStep.Save && !IsBusy);
        OpenCanvasCommand = new RelayCommand(_ => CloseRequested?.Invoke(this, EventArgs.Empty));
        BrowseFolderCommand = new RelayCommand(_ => Dll?.BrowseCommand.Execute(null), _ => Dll is not null && _pickFolder is not null && !IsBusy);
    }

    private void RaiseRunStates()
    {
        foreach (var command in new[] { TryCommand, TestOtherCommand, CreateDllCommand, SaveSetupCommand, BrowseFolderCommand })
        {
            command?.RaiseCanExecuteChanged();
        }
    }

    // ----- Step 3: columns -----

    /// <summary>The columns of the chosen table, from the run's reader.</summary>
    public ObservableCollection<GuidedColumn> Columns { get; } = new();

    public IReadOnlyList<string> KeptColumns => Columns.Where(c => c.IsKept).Select(c => c.Name).ToList();

    /// <summary>"Split a column into parts" is on.</summary>
    public bool SplitEnabled
    {
        get => _splitEnabled;
        set
        {
            if (SetProperty(ref _splitEnabled, value))
            {
                if (value && SplitColumn is null)
                {
                    SplitColumn = KeptColumns.FirstOrDefault();
                }

                ColumnsChanged();
            }
        }
    }

    public string? SplitColumn
    {
        get => _splitColumn;
        set
        {
            if (SetProperty(ref _splitColumn, value))
            {
                SuggestPartNames();
                ColumnsChanged();
            }
        }
    }

    public string SelectedSeparator
    {
        get => _selectedSeparator;
        set
        {
            if (SetProperty(ref _selectedSeparator, value ?? "pipe"))
            {
                OnPropertyChanged(nameof(IsOtherSeparator));
                SuggestPartNames();
                ColumnsChanged();
            }
        }
    }

    public bool IsOtherSeparator => SelectedSeparator == OtherSeparatorValue;

    public string OtherSeparator
    {
        get => _otherSeparator;
        set
        {
            if (SetProperty(ref _otherSeparator, value ?? string.Empty))
            {
                SuggestPartNames();
                ColumnsChanged();
            }
        }
    }

    /// <summary>The part names, separated by commas ("Part1, Part2"). Filled from the first rows until the user types.</summary>
    public string PartNamesText
    {
        get => _partNamesText;
        set
        {
            if (SetProperty(ref _partNamesText, value ?? string.Empty))
            {
                if (!_settingPartNames)
                {
                    _partNamesEdited = true;
                }

                ColumnsChanged();
            }
        }
    }

    /// <summary>The separator as text: "|" for pipe, a tab for tab, the typed text for other.</summary>
    public string Separator => SelectedSeparator switch
    {
        "comma" => ",",
        "semicolon" => ";",
        "colon" => ":",
        "pipe" => "|",
        "tab" => "\t",
        "space" => " ",
        _ => OtherSeparator
    };

    public IReadOnlyList<string> PartNames => PartNamesText.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToList();

    /// <summary>The split as set, or null when it is off.</summary>
    public ColumnSplitSettings? SplitSettings => SplitEnabled && SplitColumn is { } column ? new ColumnSplitSettings(column, Separator, PartNames) : null;

    /// <summary>Why the columns cannot be used yet, in one sentence; null when they can.</summary>
    public string? ColumnsProblem
    {
        get
        {
            if (Columns.Count == 0)
            {
                return "Choose a table first.";
            }

            var kept = KeptColumns;
            if (kept.Count == 0)
            {
                return "Keep at least one column.";
            }

            if (!SplitEnabled)
            {
                return null;
            }

            if (SplitColumn is null)
            {
                return "Choose the column to split.";
            }

            if (Separator.Length == 0)
            {
                return "Type the separator to split at.";
            }

            if (!SampleValues(SplitColumn).Any(v => v.Contains(Separator, StringComparison.Ordinal)))
            {
                return string.Format(System.Globalization.CultureInfo.InvariantCulture, NothingToSplitText, SeparatorName, SplitColumn);
            }

            return ColumnSplit.Validate(SplitSettings!, kept).FirstOrDefault();
        }
    }

    private string SeparatorName => SelectedSeparator switch
    {
        "tab" => "tab",
        "space" => "space",
        _ => Separator
    };

    /// <summary>The first rows as they will leave the reader: kept columns, split applied.</summary>
    public DataView? ColumnsPreview
    {
        get => _columnsPreview;
        private set => SetProperty(ref _columnsPreview, value);
    }

    private CsvPreview? SelectedPreview => SelectedTable is { } table && _previews.TryGetValue(table.Number, out var preview) ? preview : null;

    private IEnumerable<string> SampleValues(string column)
    {
        if (SelectedPreview is not { } preview || preview.Columns.ToList().IndexOf(column) is var index && index < 0)
        {
            return [];
        }

        return preview.Rows.Select(r => index < r.Count ? r[index] : string.Empty);
    }

    private void LoadColumns()
    {
        Columns.Clear();
        _splitEnabled = false;
        _splitColumn = null;
        _partNamesEdited = false;
        if (SelectedPreview is { Error: null } preview)
        {
            var first = preview.Rows.FirstOrDefault();
            for (var i = 0; i < preview.Columns.Count; i++)
            {
                Columns.Add(new GuidedColumn(preview.Columns[i], first is not null && i < first.Count ? first[i] : string.Empty, ColumnsChanged));
            }
        }

        OnPropertyChanged(nameof(SplitEnabled));
        OnPropertyChanged(nameof(SplitColumn));
        SetPartNames(string.Empty);
        ColumnsChanged();
    }

    /// <summary>Part1..PartN for the most pieces in the first rows, until the user types names.</summary>
    private void SuggestPartNames()
    {
        if (_partNamesEdited || SplitColumn is null || Separator.Length == 0)
        {
            return;
        }

        var most = SampleValues(SplitColumn).Select(v => ColumnSplit.Pieces(v, Separator).Count).DefaultIfEmpty(0).Max();
        SetPartNames(string.Join(", ", ColumnSplitSettings.DefaultPartNames(Math.Max(most, 2))));
    }

    private void SetPartNames(string text)
    {
        _settingPartNames = true;
        try
        {
            PartNamesText = text;
        }
        finally
        {
            _settingPartNames = false;
        }
    }

    private void ColumnsChanged()
    {
        OnPropertyChanged(nameof(KeptColumns));
        OnPropertyChanged(nameof(ColumnsProblem));
        OnPropertyChanged(nameof(SplitSettings));
        ColumnsPreview = BuildColumnsPreview();
        InvalidateTry();
        RaiseStates();
    }

    private DataView? BuildColumnsPreview()
    {
        if (SelectedPreview is not { Error: null } preview || KeptColumns is not { Count: > 0 } kept)
        {
            return null;
        }

        var position = preview.Columns.Select((c, i) => (c, i)).GroupBy(p => p.c).ToDictionary(g => g.Key, g => g.First().i);
        var records = preview.Rows.Take(ColumnsPreviewRows).Select((r, n) => new ParseValueRecord(n + 1,
            kept.ToDictionary(c => c, c => (string?)(position[c] < r.Count ? r[position[c]] : string.Empty), StringComparer.Ordinal)));
        var split = ColumnsProblem is null ? SplitSettings : null;
        var (process, columns) = ColumnSplit.Prepare(kept, new GuidedSteps(split));
        return ToView(columns, records.SelectMany(r => process(r).Rows), ColumnsPreviewRows);
    }

    // ----- Step 4: try it -----

    public Task? TryTask { get; private set; }

    public bool IsTrying
    {
        get => _isTrying;
        private set
        {
            if (SetProperty(ref _isTrying, value))
            {
                RaiseStates();
            }
        }
    }

    /// <summary>The last try worked with the settings as they are now.</summary>
    public bool TrySucceeded
    {
        get => _trySucceeded;
        private set
        {
            if (SetProperty(ref _trySucceeded, value))
            {
                RaiseStates();
            }
        }
    }

    public string? TryMessage
    {
        get => _tryMessage;
        private set => SetProperty(ref _tryMessage, value);
    }

    public string? TryDetails
    {
        get => _tryDetails;
        private set => SetProperty(ref _tryDetails, value);
    }

    /// <summary>"Rows read 8 · Rows kept 8 · Rows left out 0".</summary>
    public string? TryCountsText
    {
        get => _tryCountsText;
        private set => SetProperty(ref _tryCountsText, value);
    }

    /// <summary>The first 200 rows of the try.</summary>
    public DataView? TryRows
    {
        get => _tryRows;
        private set => SetProperty(ref _tryRows, value);
    }

    /// <summary>Why rows were left out, one plain sentence each (at most five).</summary>
    public ObservableCollection<string> TryReasons { get; } = new();

    /// <summary>The definition of the last successful try (for tests and the DLL).</summary>
    public EtlDesigner.Engine.Pipeline.PipelineDefinition? TriedDefinition => TrySucceeded ? _triedDefinition : null;

    public PipelineResult? TriedResult => TrySucceeded ? _triedResult : null;

    private void InvalidateTry()
    {
        TrySucceeded = false;
        TryMessage = null;
        TryDetails = null;
        TryCountsText = null;
        TryRows = null;
        TryReasons.Clear();
        OtherFileText = null;
        OtherFileDetails = null;
        _triedDefinition = null;
        _triedResult = null;
        Dll = null;
    }

    /// <summary>
    /// Puts the chain on the canvas, waits for the part's read, builds the definition exactly as Execute does and
    /// runs it with the same runner over the sample file; the run counts as tested for Generate DLL.
    /// </summary>
    public async Task TryAsync()
    {
        if (_session is not { } session || SelectedTable is not { } table || ColumnsProblem is not null)
        {
            return;
        }

        InvalidateTry();
        var selector = CsvPartSelector.FromSession(session, table.Number - 1, out var refused, encoding: _settings.Encoding);
        if (selector is null)
        {
            TryMessage = refused;
            return;
        }

        IsTrying = true;
        using var cancel = new CancellationTokenSource();
        _runCancel = cancel;
        try
        {
            var plan = session.IsModified ? IntakePlanJson.Serialize(IntakePlan.FromSession(session, session.Scan)) : null;
            var selection = new CsvColumnSelection { Mode = CsvColumnSelectionMode.Ignore };
            selection.Names.AddRange(Columns.Where(c => !c.IsKept).Select(c => c.Name));
            var partNode = _main.ApplyGuidedChain(_settings, plan, selector, selection, SplitSettings);
            if (_main.CsvPartRefresh is { } refresh)
            {
                await refresh;
            }

            _main.Editor.Validate();
            if (!_main.TryBuildExecuteDefinition(out var definition, out var reason) || definition is null)
            {
                // The table's own problem (for example a second copy in the file) says more than "not configured".
                var prefix = partNode.Title + ":";
                var partIssue = _main.Editor.Issues.FirstOrDefault(i => i.StartsWith(prefix, StringComparison.Ordinal));
                var plainReason = PlainErrorText.Map(partIssue is null ? reason ?? string.Empty : partIssue[prefix.Length..].Trim());
                TryMessage = plainReason.Sentence;
                TryDetails = plainReason.Details;
                return;
            }

            var result = await Task.Run(() => PipelineRunner.RunAsync(definition, null, null, cancel.Token));
            _main.OnExecuteRunFinished(definition, result);
            ShowTry(definition, result);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            TryMessage = "The try stopped with an unexpected problem. Try again; if it happens again, open the canvas (Advanced) and press Execute.";
            TryDetails = ex.Message;
        }
        finally
        {
            _runCancel = null;
            IsTrying = false;
        }
    }

    private void ShowTry(EtlDesigner.Engine.Pipeline.PipelineDefinition definition, PipelineResult result)
    {
        var counts = result.Counts;
        TryCountsText = $"Rows read {N(counts.Read)} · Rows kept {N(counts.Out)} · Rows left out {N(counts.Rejected)}"
                        + (counts.Flagged > 0 ? $" · Rows with a note {N(counts.Flagged)}" : string.Empty);
        TryRows = ToView(result.Columns, result.Rows.Select(r => (IReadOnlyList<string>)result.Columns.Select(c => r.GetValueOrDefault(c) ?? string.Empty).ToList()), TryShownRows);
        foreach (var reason in result.Rejects.Concat(result.Flagged).Select(r => $"Row {N(r.RecordNumber)}: {PlainErrorText.RowReason(r.Reason)}").Take(5))
        {
            TryReasons.Add(reason);
        }

        if (result.Cancelled)
        {
            TryMessage = PlainErrorText.Cancelled;
            return;
        }

        if (result.Error is not null)
        {
            var plain = PlainErrorText.Map(result.Error, FailureOf(result));
            TryMessage = plain.Sentence;
            TryDetails = plain.Details;
            return;
        }

        _triedDefinition = definition;
        _triedResult = result;
        TryMessage = counts.Out == 0 ? "The try worked, but no rows came out. Check the columns and the split." : "The try worked.";
        TrySucceeded = counts.Out > 0;
    }

    private static CsvPartFailure? FailureOf(PipelineResult result)
        => result.CsvPart?.Failure is { } failure && Enum.TryParse<CsvPartFailure>(failure, out var kind) ? kind : null;

    // ----- Step 4: test on another file -----

    public string OtherFilePath
    {
        get => _otherFilePath;
        private set => SetProperty(ref _otherFilePath, value);
    }

    public bool IsTestingOther
    {
        get => _isTestingOther;
        private set
        {
            if (SetProperty(ref _isTestingOther, value))
            {
                RaiseStates();
            }
        }
    }

    /// <summary>"Same layout found. N rows." or the plain reason the table was not found.</summary>
    public string? OtherFileText
    {
        get => _otherFileText;
        private set => SetProperty(ref _otherFileText, value);
    }

    public string? OtherFileDetails
    {
        get => _otherFileDetails;
        private set => SetProperty(ref _otherFileDetails, value);
    }

    public Task? OtherFileTask { get; private set; }

    private void PickOtherFile()
    {
        if (_pickFile?.Invoke(FilePath) is { } chosen && !string.IsNullOrWhiteSpace(chosen))
        {
            OtherFileTask = TestOtherFileAsync(chosen);
        }
    }

    /// <summary>Runs the tried pipeline over another file: the table is found by its structure, as the DLL does. Nothing on the canvas changes.</summary>
    public async Task TestOtherFileAsync(string path)
    {
        if (TriedDefinition is not { } definition)
        {
            return;
        }

        OtherFilePath = path;
        OtherFileText = null;
        OtherFileDetails = null;
        IsTestingOther = true;
        using var cancel = new CancellationTokenSource();
        _runCancel = cancel;
        try
        {
            var result = await Task.Run(() => PipelineRunner.RunAsync(definition, path, null, cancel.Token));
            if (result.Cancelled)
            {
                OtherFileText = PlainErrorText.Cancelled;
            }
            else if (result.Error is not null)
            {
                var plain = PlainErrorText.Map(result.Error, FailureOf(result));
                OtherFileText = plain.Sentence;
                OtherFileDetails = plain.Details;
            }
            else
            {
                OtherFileText = string.Format(System.Globalization.CultureInfo.InvariantCulture, SameLayoutText, N(result.Counts.Out));
            }
        }
        finally
        {
            _runCancel = null;
            IsTestingOther = false;
        }
    }

    // ----- Step 5: save as DLL -----

    /// <summary>The existing Generate DLL logic (name, folder, generator, result), made when the Save step opens.</summary>
    public GenerateDllViewModel? Dll
    {
        get => _dll;
        private set
        {
            if (_dll is not null)
            {
                _dll.PropertyChanged -= OnDllChanged;
            }

            if (SetProperty(ref _dll, value) && value is not null)
            {
                value.PropertyChanged += OnDllChanged;
            }

            WrittenFiles.Clear();
            SaveMessage = null;
            RaiseStates();
        }
    }

    private void OnDllChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RaiseStates();

    public Task? CreateDllTask { get; private set; }

    /// <summary>What was written after "Create DLL", one file per line, then the notes.</summary>
    public ObservableCollection<string> WrittenFiles { get; } = new();

    public string? SaveMessage
    {
        get => _saveMessage;
        private set => SetProperty(ref _saveMessage, value);
    }

    /// <summary>What happened to the saved layout ("Layout saved as 'name'.", or why not).</summary>
    public string? TemplateText
    {
        get => _templateText;
        private set => SetProperty(ref _templateText, value);
    }

    private void PrepareSave()
    {
        if (TriedDefinition is not { } definition || TriedResult is not { } result)
        {
            return;
        }

        var name = MainViewModel.SanitizeAssemblyName(Path.GetFileNameWithoutExtension(FilePath) + "Reader");
        Dll = new GenerateDllViewModel(definition, result.Columns, _generate, _pickFolder) { AssemblyName = name };
    }

    /// <summary>Uses the canvas's own gate, then the existing generator. Writes <c>guided.saved-dll</c> after a success.</summary>
    public async Task CreateDllAsync()
    {
        if (Dll is not { } dll)
        {
            return;
        }

        WrittenFiles.Clear();
        if (_main.GenerateDllBlockedReason is { } blocked)
        {
            SaveMessage = PlainErrorText.Map(blocked).Sentence;
            return;
        }

        await dll.GenerateAsync();
        if (!dll.Succeeded)
        {
            var technical = string.Join(" ", dll.Messages.Concat(dll.CompileErrors.Select(e => e.ToString())));
            SaveMessage = $"The DLL was not created. {PlainErrorText.Map(technical).Sentence}";
            return;
        }

        foreach (var file in new[] { dll.DllPath, dll.ReadmePath }.Where(f => f is not null))
        {
            WrittenFiles.Add(file!);
        }

        WrittenFiles.Add(HelperFilesNote);
        SaveMessage = LicenceNote;
        var selector = _triedDefinition?.CsvPart;
        _lineage.Write(new LineageEvent("guided.saved-dll", _guidedId, _session?.FileName, _session?.Sha256, new Dictionary<string, object?>
        {
            ["templateId"] = selector?.Template.Id.ToString("D"),
            ["templateName"] = selector?.Template.Name,
            ["partNumber"] = selector is null ? null : selector.PartIndex + 1,
            ["columns"] = _triedResult?.Columns.Count
        }));
    }

    // ----- the saved layout -----

    /// <summary>
    /// Saves the layout as a template named after the file, with the chosen table in its notes, so the next file of
    /// the same instrument is recognised. A template of that name saved earlier by this builder is replaced;
    /// another one only after Yes (default No).
    /// </summary>
    private void SaveLayoutTemplate()
    {
        if (_session is not { } session || SelectedTable is not { } table)
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(FilePath).Trim();
        name = IntakeEditRules.NameProblem(name) is null ? IntakeEditRules.Cut(name) : "Reader layout";
        var existing = _templates.List().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && existing.Id != _savedTemplateId
            && !_confirm($"A saved layout named '{name}' already exists. Replace it with this one?"))
        {
            TemplateText = $"The saved layout '{name}' was kept as it was.";
            return;
        }

        if (existing is not null)
        {
            _templates.Delete(existing.Id);
        }

        var template = session.ToTemplate(name, _templateSettings) with { Notes = GuidedNotePrefix + table.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var saved = _templates.Save(template);
        if (!saved.Ok)
        {
            TemplateText = $"The layout was not saved: {saved.Message}";
            return;
        }

        _savedTemplateId = template.Id;
        TemplateText = $"Layout saved as '{name}'. The next file of this instrument will be recognised.";
        _lineage.Write(new LineageEvent("template.saved", session.IntakeId, session.FileName, session.Sha256, new Dictionary<string, object?>
        {
            ["templateId"] = template.Id.ToString("D"),
            ["templateName"] = template.Name,
            ["parts"] = template.Parts.Count
        }));
    }
}
