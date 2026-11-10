using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using EtlDesigner.Engine;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Generation;
using EtlDesigner.Engine.Project;
using PipelineDefinition = EtlDesigner.Engine.Pipeline.PipelineDefinition;
using PipelineDefinitionValidator = EtlDesigner.Engine.Pipeline.PipelineDefinitionValidator;
using PipelineResult = EtlDesigner.Engine.Pipeline.PipelineResult;
using SettingsJson = EtlDesigner.Engine.Pipeline.SettingsJson;
using EtlDesigner.UI.Mvvm;
using EtlDesigner.UI.Services;

namespace EtlDesigner.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static readonly HashSet<string> ForbiddenAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ETLBox",
        "ETLBox.Csv",
        "ETLBox.Json",
        "ETLBox.Licensing",
        "CsvHelper"
    };

    private string _filteredJsonOutput;
    private string _parsedJsonOutput;
    private int _countryLoadVersion;
    private PipelineSettings? _lastSuccessfulRunSettings;
    private (string Hash, IReadOnlyList<string> Columns)? _lastSuccessfulPipelineRun;
    private bool _isRunInProgress;
    private bool _isGenerating;
    private readonly IDialogService _dialogService;
    private readonly EtlDesigner.Engine.Intake.ILineageLog? _lineageLog;
    private readonly EtlDesigner.Engine.Intake.ITemplateStore? _templateStore;
    private readonly HashSet<(Guid NodeId, CsvFileSettings Settings)> _columnReadsTried = new();

    public MainViewModel(IDialogService? dialogService = null, IRecentProjects? recentProjects = null, EtlDesigner.Engine.Intake.ILineageLog? lineageLog = null,
        EtlDesigner.Engine.Intake.ITemplateStore? templateStore = null)
    {
        _dialogService = dialogService ?? new DialogService();
        _lineageLog = lineageLog;
        _templateStore = templateStore;
        _recentProjects = recentProjects ?? new JsonRecentProjects();
        Editor = new EditorViewModel();
        _filteredJsonOutput = "";
        _parsedJsonOutput = "";

        RunCommand = new AsyncRelayCommand(async _ => await RunAsync(), _ => !_isGenerating);
        GenerateDllCommand = new AsyncRelayCommand(async _ => await GenerateDllAsync(), _ => CanGenerateDll());
        ExecuteCommand = new RelayCommand(_ => Execute(), _ => !_isRunInProgress && !_isGenerating && ExecuteBlockedReason is null);
        CreateProjectCommands();
        Editor.Nodes.CollectionChanged += (_, _) => UpdateCommandStates();
        Editor.Connections.CollectionChanged += (_, _) => UpdateCommandStates();
        Editor.Nodes.CollectionChanged += (_, _) => { ClearSuccessfulPipelineRun(); HookPipelineChangeListeners(); };
        Editor.Connections.CollectionChanged += (_, _) => ClearSuccessfulPipelineRun();
        HookPipelineChangeListeners();

        NodeFactory.CsvPreviewHandler = ShowCsvPreview;
        NodeFactory.ParseValueConfigureHandler = ShowParseValue;
        NodeFactory.FileIntakeConfigureHandler = ShowIntakeStudio;

        HookCountryRefreshListeners();
        HookCsvFileValidationListeners();
        _ = RefreshCountriesAsync();
        UpdateCommandStates();
    }

    public EditorViewModel Editor { get; }

    public AsyncRelayCommand RunCommand { get; }

    public AsyncRelayCommand GenerateDllCommand { get; }

    /// <summary>Runs CsvFile -> ParseValue -> DotNetObject over a whole file in the Execute dialog. The legacy Run is separate.</summary>
    public RelayCommand ExecuteCommand { get; }

    /// <summary>Run and Generate DLL ignore File intake nodes and say so in this one line.</summary>
    public const string FileIntakeNotRunMessage = "File intake nodes are not run yet.";

    public const string RunIsForLegacyChainMessage ="Run is for the old five-node chain. Use Execute for this pipeline.";

    public const string ExecuteReadyHint ="Run CsvFile -> ParseValue -> DotNetObject over the whole file.";

    public const string GenerateDllNotTestedHint = "Run Execute successfully first. The DLL is generated only from a pipeline that has been tested.";

    public const string GenerateDllReadyHint = "Generate a DLL from the tested pipeline.";

    public const string GenerateDllLegacyHint = "Run the five-node chain successfully first, or build CsvFile -> ParseValue -> DotNetObject and run Execute successfully.";

    /// <summary>
    /// Why Generate DLL is disabled, or null when it can run. With CsvFile, ParseValue or DotNetObject on the
    /// canvas the new pipeline rules apply: the definition validates and Execute ran it without an error.
    /// Otherwise the legacy rule applies: the five-node chain ran successfully.
    /// </summary>
    public string? GenerateDllBlockedReason
    {
        get
        {
            if (_isRunInProgress || _isGenerating)
            {
                return "Wait until the current run has finished.";
            }

            if (!IsNewPipelineGraph)
            {
                return _lastSuccessfulRunSettings is null ? GenerateDllLegacyHint : null;
            }

            if (!TryBuildExecuteDefinition(out var definition, out var reason) || definition is null)
            {
                return reason;
            }

            if (_lastSuccessfulPipelineRun is not { } run || run.Hash != PipelineHash(definition))
            {
                return GenerateDllNotTestedHint;
            }

            return PipelineDefinitionValidator.Validate(definition).FirstOrDefault();
        }
    }

    /// <summary>The Generate DLL button's tooltip: the reason it is disabled, or what it does.</summary>
    public string GenerateDllHint => GenerateDllBlockedReason ?? GenerateDllReadyHint;

    private bool IsNewPipelineGraph
        => Editor.Nodes.Any(n => n.NodeType is NodeType.CsvFile or NodeType.ParseValue or NodeType.DotNetObject);

    /// <summary>The key of "last successful run": a SHA-256 of the definition JSON.</summary>
    internal static string PipelineHash(PipelineDefinition definition)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SettingsJson.Serialize(definition))));

    /// <summary>Why Execute is disabled, or null when it can run.</summary>
    public string? ExecuteBlockedReason => TryBuildExecuteDefinition(out _, out var reason) ? null : reason;

    /// <summary>The Execute button's tooltip: the reason it is disabled, or what it does.</summary>
    public string ExecuteHint => ExecuteBlockedReason ?? ExecuteReadyHint;

    /// <summary>
    /// The graph must be exactly CsvFile -> ParseValue -> DotNetObject (three nodes, two
    /// connections), every node Configured, and no Issues line for those nodes.
    /// </summary>
    public bool TryBuildExecuteDefinition(out PipelineDefinition? definition, out string? reason)
    {
        definition = null;

        // An unknown node from a project file, and a File intake node (not run yet), are ignored.
        var nodes = Editor.Nodes.Where(n => n.NodeType is not (NodeType.Unknown or NodeType.FileIntake)).ToList();
        var csv = nodes.FirstOrDefault(n => n.NodeType == NodeType.CsvFile);
        var parse = nodes.FirstOrDefault(n => n.NodeType == NodeType.ParseValue);
        var output = nodes.FirstOrDefault(n => n.NodeType == NodeType.DotNetObject);

        bool Linked(NodeViewModel? from, NodeViewModel? to)
            => from is not null && to is not null && Editor.Connections.Any(c => c.Source.ParentNode == from && c.Target.ParentNode == to);

        if (nodes.Count != 3 || csv is null || parse is null || output is null
            || Editor.Connections.Count != 2 || !Linked(csv, parse) || !Linked(parse, output))
        {
            reason = "Execute needs exactly CsvFile -> ParseValue -> DotNetObject on the canvas, connected in that order.";
            return false;
        }

        foreach (var node in new[] { csv, parse, output })
        {
            if (!string.Equals(node.ConfigurationStatus, "Configured", StringComparison.Ordinal))
            {
                reason = $"Configure every node first: {node.Title} is not configured. See the Issues tab.";
                return false;
            }

            if (Editor.Issues.Any(i => i.StartsWith(node.Title + ":", StringComparison.Ordinal)))
            {
                reason = $"Fix the issues of {node.Title} in the Issues tab first.";
                return false;
            }
        }

        if (parse.ParseValueSettings is null)
        {
            reason = "Configure the ParseValue node first.";
            return false;
        }

        definition = new PipelineDefinition(csv.GetCsvFileSettings(), csv.ColumnSelection, parse.ParseValueSettings, output.GetOutputSettings());
        reason = null;
        return true;
    }

    private void Execute()
    {
        if (!TryBuildExecuteDefinition(out var definition, out var reason) || definition is null)
        {
            Editor.Issues.Add($"Execute: {reason}");
            return;
        }

        try
        {
            var tested = definition;
            _dialogService.ShowExecute(new ExecuteViewModel(definition, _dialogService.PickCsvFile, text => CopyToClipboard(text),
                result => OnExecuteRunFinished(tested, result)));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open the Execute dialog.\n\n{ex}", "Execute error", MessageBoxButton.OK, MessageBoxImage.Error);
            Editor.Issues.Add($"Execute: {ex.Message}");
        }
    }

    /// <summary>A run without an error, cancel or licence stop counts as tested for Generate DLL.</summary>
    internal void OnExecuteRunFinished(PipelineDefinition definition, PipelineResult result)
    {
        if (result.Error is null && !result.Cancelled && !result.LicenceLimitReached)
        {
            _lastSuccessfulPipelineRun = (PipelineHash(definition), result.Columns.ToList());
        }

        UpdateCommandStates();
    }

    private void ClearSuccessfulPipelineRun()
    {
        _lastSuccessfulPipelineRun = null;
        UpdateCommandStates();
    }

    /// <summary>Any change to a node setting (including the ParseValue settings) clears the last successful run.</summary>
    private void HookPipelineChangeListeners()
    {
        foreach (var node in Editor.Nodes)
        {
            node.PropertyChanged -= OnNodePropertyChanged;
            node.PropertyChanged += OnNodePropertyChanged;
            foreach (var setting in node.Settings)
            {
                setting.ValueChanged -= OnAnySettingValueChanged;
                setting.ValueChanged += OnAnySettingValueChanged;
            }
        }
    }

    private void OnAnySettingValueChanged(object? sender, EventArgs e) => ClearSuccessfulPipelineRun();

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NodeViewModel.ParseValueSettings))
        {
            ClearSuccessfulPipelineRun();
        }
    }

    private void CopyToClipboard(string text, string source = "Execute")
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Editor.Issues.Add($"{source}: could not copy to the clipboard. Try again. Details: {ex.Message}");
        }
    }

    public string FilteredJsonOutput
    {
        get => _filteredJsonOutput;
        set => SetProperty(ref _filteredJsonOutput, value ?? "");
    }

    public string ParsedJsonOutput
    {
        get => _parsedJsonOutput;
        set => SetProperty(ref _parsedJsonOutput, value ?? "");
    }

    private async Task RunAsync()
    {
        try
        {
            await RunGraphAsync();
        }
        finally
        {
            AddFileIntakeNotRunLine();
        }
    }

    /// <summary>Adds <see cref="FileIntakeNotRunMessage"/> once when the canvas has a File intake node.</summary>
    private void AddFileIntakeNotRunLine(string prefix = "")
    {
        var line = prefix + FileIntakeNotRunMessage;
        if (Editor.Nodes.Any(n => n.NodeType == NodeType.FileIntake) && !Editor.Issues.Contains(line))
        {
            Editor.Issues.Remove("No issues found.");
            Editor.Issues.Add(line);
        }
    }

    private async Task RunGraphAsync()
    {
        if (Editor.HasOnlyNewStyleNodes)
        {
            Editor.Issues.Clear();
            Editor.Issues.Add(RunIsForLegacyChainMessage);
            return;
        }

        _isRunInProgress = true;
        ClearSuccessfulRunSnapshot();
        UpdateCommandStates();

        try
        {
            Editor.Issues.Clear();
            Editor.ResetNodeRunState();

            if (Editor.TryCreatePipelineSettings(out var settings) == false || settings == null)
            {
                if (string.IsNullOrWhiteSpace(FilteredJsonOutput))
                {
                    FilteredJsonOutput = "";
                }

                ParsedJsonOutput = "";
                return;
            }

            if (Editor.TryGetLinearChain(out var chain, out var chainIssues) == false)
            {
                foreach (var issue in chainIssues)
                {
                    Editor.Issues.Add(issue);
                }

                return;
            }

            SetRunningChain(chain, 0);

            PipelineRunResult runResult;
            try
            {
                runResult = await Task.Run(() => PipelineRunner.RunWithStats(settings));
            }
            catch (Exception ex)
            {
                Editor.Issues.Add($"CsvSource: {ex}");
                return;
            }

            var result = runResult.Result;
            var stats = runResult.Stats;

            SetNodeStats(chain, stats);

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                ApplyFailureState(chain, runResult.FailureNode, result.ErrorMessage);
                Editor.Issues.Add($"{GetFailureNodeTitle(runResult.FailureNode)}: {result.ErrorMessage}");
                FilteredJsonOutput = result.FilteredJson;
                ParsedJsonOutput = result.ParsedJson;
                return;
            }

            FilteredJsonOutput = string.IsNullOrWhiteSpace(result.FilteredJson) ? "[]" : result.FilteredJson;
            ParsedJsonOutput = string.IsNullOrWhiteSpace(result.ParsedJson) ? "[]" : result.ParsedJson;

            foreach (var node in chain)
            {
                node.State = NodeState.Succeeded;
                node.ErrorMessage = null;
                node.ErrorDetail = null;
            }

            if (chain.All(n => n.State == NodeState.Succeeded))
            {
                _lastSuccessfulRunSettings = settings.ToCoreSettings();
                UpdateCommandStates();
            }
        }
        finally
        {
            _isRunInProgress = false;
            UpdateCommandStates();
        }
    }

    private void HookCountryRefreshListeners()
    {
        Editor.Nodes.CollectionChanged += (_, _) =>
        {
            HookCountryRefreshListenersForCurrentNodes();
            _ = RefreshCountriesAsync();
        };

        HookCountryRefreshListenersForCurrentNodes();
    }

    private void HookCsvFileValidationListeners()
    {
        Editor.ValidationStateChanged += (_, _) => UpdateCommandStates();
        Editor.ValidationStateChanged += (_, _) => _ = RefreshParseValueUpstreamColumnsAsync();

        Editor.Nodes.CollectionChanged += (_, _) =>
        {
            foreach (var node in Editor.Nodes)
            {
                if (node.NodeType != NodeType.CsvFile)
                {
                    continue;
                }

                foreach (var setting in node.Settings)
                {
                    setting.ValueChanged -= OnCsvFileSettingValueChanged;
                    setting.ValueChanged += OnCsvFileSettingValueChanged;
                }
            }
        };

        foreach (var node in Editor.Nodes)
        {
            if (node.NodeType != NodeType.CsvFile)
            {
                continue;
            }

            foreach (var setting in node.Settings)
            {
                setting.ValueChanged += OnCsvFileSettingValueChanged;
            }
        }
    }

    /// <summary>
    /// A ParseValue node needs the column names of its CsvFile node. When they are not known yet,
    /// read them once per node and settings (off the validation path), then validate again.
    /// </summary>
    private async Task RefreshParseValueUpstreamColumnsAsync()
    {
        // Opening a project reads the columns of all its CsvFile nodes at once.
        if (_isOpeningProject)
        {
            return;
        }

        try
        {
            foreach (var node in Editor.Nodes.Where(n => n.NodeType == NodeType.ParseValue).ToList())
            {
                var upstream = ParseValueUpstreamResolver.Resolve(node, Editor.Connections);
                if (upstream.CsvNode is null || upstream.Settings is null || upstream.CsvNode.KnownColumns is not null)
                {
                    continue;
                }

                if (!_columnReadsTried.Add((upstream.CsvNode.Id, upstream.Settings)))
                {
                    continue;
                }

                if (await ParseValueUpstreamResolver.RefreshColumnsAsync(upstream.CsvNode, new CsvPreviewReader(), CancellationToken.None))
                {
                    Editor.Validate();
                }
            }
        }
        catch (Exception ex)
        {
            Editor.Issues.Add($"ParseValue: could not read the columns of the CsvFile node. Press Load preview on it. Details: {ex.Message}");
        }
    }

    private void OnCsvFileSettingValueChanged(object? sender, EventArgs e)
    {
        Editor.Validate();
    }

    private bool ShowCsvPreview(NodeViewModel node, CsvFileSettings settings)
    {
        try
        {
            _dialogService.ShowCsvPreview(node, settings);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open CSV preview.\n\n{ex}", "Load preview error", MessageBoxButton.OK, MessageBoxImage.Error);
            Editor.Issues.Add($"CsvFile: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Opens Intake Studio for a File intake node. Save writes the settings to the node's rows
    /// (only a change marks the project dirty). The scan totals go onto the node when the last scan
    /// used the node's settings.
    /// </summary>
    internal bool ShowIntakeStudio(NodeViewModel node)
    {
        try
        {
            var viewModel = new IntakeStudioViewModel(
                node.GetFileIntakeSettings(),
                NodeFactory.Labels,
                _dialogService.PickIntakeFile,
                save: node.SetFileIntakeSettings,
                askUnsaved: () => _dialogService.AskUnsavedChanges($"the settings of {node.Title}"),
                lineage: _lineageLog ?? AppLineage.Shared,
                savedPlanJson: node.IntakePlanJson,
                savePlan: json => node.IntakePlanJson = json,
                confirm: question => _dialogService.AskYesNo("Intake Studio", question),
                templates: _templateStore ?? AppTemplates.Shared,
                templateSettings: _templateStore is null ? AppTemplates.Settings : null);

            _dialogService.ShowIntakeStudio(viewModel);

            if (viewModel.ScannedSettings is { } scanned && scanned == node.GetFileIntakeSettings())
            {
                node.IntakeSummary = viewModel.Summary;
            }

            return true;
        }
        catch (Exception ex)
        {
            try
            {
                ErrorLog.Write("Intake Studio", ex);
            }
            catch (Exception)
            {
                // The log could not be written (for example a read-only program folder); the message still shows.
            }

            _dialogService.ShowError("Intake Studio", "Intake Studio could not be opened. The details are in unhandled-exception.log next to the program.");
            Editor.Issues.Add("Intake Studio could not be opened. The details are in unhandled-exception.log next to the program.");
            return false;
        }
    }

    private bool ShowParseValue(NodeViewModel node)
    {
        try
        {
            _dialogService.ShowParseValue(node, ParseValueUpstreamResolver.Resolve(node, Editor.Connections));
            Editor.Validate();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open the ParseValue dialog.\n\n{ex}", "ParseValue error", MessageBoxButton.OK, MessageBoxImage.Error);
            Editor.Issues.Add($"ParseValue: {ex.Message}");
            return false;
        }
    }
    private void HookCountryRefreshListenersForCurrentNodes()
    {
        foreach (var node in Editor.Nodes)
        {
            foreach (var setting in node.Settings)
            {
                setting.ValueChanged -= OnNodeSettingValueChanged;

                if (node.NodeType == NodeType.CsvSource && (setting.Key == "FilePath" || setting.Key == "Delimiter"))
                {
                    setting.ValueChanged += OnNodeSettingValueChanged;
                }

                if (node.NodeType == NodeType.FilterByCountry && (setting.Key == "LocationColumn" || setting.Key == "Separator"))
                {
                    setting.ValueChanged += OnNodeSettingValueChanged;
                }
            }
        }
    }

    private void OnNodeSettingValueChanged(object? sender, EventArgs e)
    {
        _ = RefreshCountriesAsync();
    }

    private async Task RefreshCountriesAsync()
    {
        var thisRequestVersion = Interlocked.Increment(ref _countryLoadVersion);

        if (Editor.TryCreatePipelineSettings(out var settings) == false || settings == null)
        {
            if (thisRequestVersion != _countryLoadVersion)
                return;

            return;
        }

        var result = await Task.Run(() => PipelineRunner.ReadDistinctCountries(settings));

        if (thisRequestVersion != _countryLoadVersion)
            return;

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            Editor.Issues.Add($"CsvSource: {result.ErrorMessage}");
            UpdateCountryList(Array.Empty<string>(), result.ErrorMessage);
            return;
        }

        UpdateCountryList(result.Countries, null);
    }

    private void UpdateCountryList(IReadOnlyList<string> countries, string? error)
    {
        var filterNode = Editor.Nodes.FirstOrDefault(n => n.NodeType == NodeType.FilterByCountry);
        if (filterNode == null)
            return;

        var countrySetting = filterNode.Settings.FirstOrDefault(s => s.Key == "Country");
        if (countrySetting == null)
            return;

        countrySetting.ComboBoxItems.Clear();
        foreach (var country in countries)
        {
            countrySetting.ComboBoxItems.Add(country);
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            countrySetting.WarningMessage = null;
            return;
        }

        if (!string.IsNullOrWhiteSpace(countrySetting.Value)
            && !countries.Contains(countrySetting.Value.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            countrySetting.WarningMessage = "Selected value is not in the current CSV country list.";
        }
        else
        {
            countrySetting.WarningMessage = null;
        }
    }

    private async Task GenerateDllAsync()
    {
        try
        {
            await GenerateDllForGraphAsync();
        }
        finally
        {
            AddFileIntakeNotRunLine("Generate DLL: ");
        }
    }

    private async Task GenerateDllForGraphAsync()
    {
        if (IsNewPipelineGraph)
        {
            GenerateCsvPipelineDll();
            return;
        }

        if (_lastSuccessfulRunSettings == null)
        {
            Editor.Issues.Add($"Generate DLL: {GenerateDllLegacyHint}");
            return;
        }

        _isGenerating = true;
        UpdateCommandStates();

        try
        {
            var saveFileDialog = new SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = ".dll",
                FileName = "EtlPipeline.dll",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Filter = "DLL files (*.dll)|*.dll"
            };

            if (saveFileDialog.ShowDialog() != true)
            {
                return;
            }

            var outputFolder = Path.GetDirectoryName(saveFileDialog.FileName);
            if (string.IsNullOrWhiteSpace(outputFolder))
            {
                Editor.Issues.Add("Generate DLL: Could not determine the selected output folder.");
                return;
            }

            try
            {
                if (IsInsideProjectFolder(outputFolder))
                {
                    Editor.Issues.Add("This folder is inside a project. The generated .cs files would be compiled into it. Choose a folder outside any project.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Editor.Issues.Add($"Generate DLL: {ex}");
                return;
            }

            var selectedName = Path.GetFileNameWithoutExtension(saveFileDialog.FileName);
            var assemblyName = SanitizeAssemblyName(selectedName);

            if (TryGetForbiddenAssemblyNameReason(assemblyName, out var reason))
            {
                Editor.Issues.Add($"Generate DLL: {reason}");
                return;
            }

            DllGenerationResult result;
            try
            {
                result = await Task.Run(() => DllGenerator.Generate(_lastSuccessfulRunSettings, outputFolder, assemblyName));
            }
            catch (Exception ex)
            {
                Editor.Issues.Add($"Generate DLL: {ex}");
                return;
            }

            if (!result.Success)
            {
                var failureText = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "DLL generation failed."
                    : result.ErrorMessage;
                Editor.Issues.Add($"Generate DLL: {failureText}");

                foreach (var diagnostic in result.Diagnostics)
                {
                    Editor.Issues.Add($"Generate DLL: {diagnostic}");
                }

                return;
            }

            var dllPath = string.IsNullOrWhiteSpace(result.DllPath)
                ? Path.Combine(outputFolder, $"{assemblyName}.dll")
                : result.DllPath;

            var successMessage =
                $"DLL generated at:\n{dllPath}\n\n" +
                "Generated .cs files are in the same folder.\n" +
                "Using the DLL needs the ETLBox 3.11.1 packages and a valid ETLBox license at runtime. The free edition is limited to 5,000 rows." +
                (Editor.Nodes.Any(n => n.NodeType == NodeType.FileIntake) ? "\n\n" + FileIntakeNotRunMessage : "");
            System.Windows.MessageBox.Show(successMessage, "Generate DLL", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        finally
        {
            _isGenerating = false;
            UpdateCommandStates();
        }
    }

    /// <summary>Opens the Generate DLL dialog for CsvFile -> ParseValue -> DotNetObject, or says in Issues why not.</summary>
    private void GenerateCsvPipelineDll()
    {
        var reason = GenerateDllBlockedReason;
        if (reason is not null || !TryBuildExecuteDefinition(out var definition, out _) || definition is null
            || _lastSuccessfulPipelineRun is not { } run)
        {
            Editor.Issues.Add($"Generate DLL: {reason ?? GenerateDllNotTestedHint}");
            return;
        }

        try
        {
            _dialogService.ShowGenerateDll(new GenerateDllViewModel(definition, run.Columns,
                pickFolder: _dialogService.PickFolder, copyText: text => CopyToClipboard(text, "Generate DLL")));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open the Generate DLL dialog.\n\n{ex}", "Generate DLL error", MessageBoxButton.OK, MessageBoxImage.Error);
            Editor.Issues.Add($"Generate DLL: {ex.Message}");
        }
    }

    private bool CanGenerateDll()
    {
        return GenerateDllBlockedReason is null;
    }

    private void ClearSuccessfulRunSnapshot()
    {
        _lastSuccessfulRunSettings = null;
    }

    private void UpdateCommandStates()
    {
        RunCommand.RaiseCanExecuteChanged();
        GenerateDllCommand.RaiseCanExecuteChanged();
        ExecuteCommand?.RaiseCanExecuteChanged();
        RaiseProjectCommandStates();
        OnPropertyChanged(nameof(ExecuteHint));
        OnPropertyChanged(nameof(GenerateDllHint));
    }

    internal static bool IsInsideProjectFolder(string folderPath) => OutputFolderCheck.IsInsideProject(folderPath);

    private static string SanitizeAssemblyName(string selectedName)
    {
        if (string.IsNullOrWhiteSpace(selectedName))
        {
            return "EtlPipeline";
        }

        var builder = new StringBuilder(selectedName.Length);

        foreach (var ch in selectedName.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('_');
            }
        }

        var sanitized = builder.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "EtlPipeline";
        }

        if (char.IsDigit(sanitized[0]))
        {
            sanitized = $"_{sanitized}";
        }

        return sanitized;
    }

    private static bool TryGetForbiddenAssemblyNameReason(string assemblyName, out string reason)
    {
        if (assemblyName.StartsWith("System", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Assembly name '{assemblyName}' is not allowed because names starting with System or Microsoft are reserved.";
            return true;
        }

        if (ForbiddenAssemblyNames.Contains(assemblyName))
        {
            reason = $"Assembly name '{assemblyName}' is not allowed because it conflicts with a referenced assembly.";
            return true;
        }

        reason = "";
        return false;
    }

    private static void SetRunningChain(IReadOnlyList<NodeViewModel> chain, int startIndex)
    {
        for (int i = startIndex; i < chain.Count; i++)
        {
            chain[i].State = NodeState.Running;
            chain[i].ErrorMessage = null;
            chain[i].ErrorDetail = null;
        }
    }

    private static void SetNodeStats(IReadOnlyList<NodeViewModel> chain, PipelineExecutionStats stats)
    {
        chain[0].RowCount = stats.RowsRead;
        chain[1].RowCount = stats.RowsMatched;
        chain[2].RowCount = stats.RowsMatched;
        chain[3].RowCount = stats.ParsedRecordCount;
        chain[4].RowCount = stats.ParsedRecordCount;
    }

    private static void ApplyFailureState(IReadOnlyList<NodeViewModel> chain, PipelineFailureNode failureNode, string fullErrorMessage)
    {
        var failureIndex = failureNode switch
        {
            PipelineFailureNode.CsvSource => 0,
            PipelineFailureNode.ToJson => 2,
            PipelineFailureNode.ParseJson => 3,
            _ => -1
        };

        if (failureIndex < 0)
        {
            return;
        }

        for (int i = 0; i < chain.Count; i++)
        {
            if (i < failureIndex)
            {
                chain[i].State = NodeState.Succeeded;
                chain[i].ErrorMessage = null;
                chain[i].ErrorDetail = null;
            }
            else if (i == failureIndex)
            {
                chain[i].State = NodeState.Failed;
                chain[i].ErrorMessage = "Failed";
                chain[i].ErrorDetail = fullErrorMessage;
            }
            else
            {
                chain[i].State = NodeState.NotRun;
                chain[i].ErrorMessage = "Not run";
                chain[i].ErrorDetail = "Not run because an earlier node failed.";
            }
        }
    }

    private static string GetFailureNodeTitle(PipelineFailureNode failureNode)
    {
        return failureNode switch
        {
            PipelineFailureNode.CsvSource => "CsvSource",
            PipelineFailureNode.ToJson => "ToJson",
            PipelineFailureNode.ParseJson => "ParseJson",
            _ => "Run"
        };
    }
}
