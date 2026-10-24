using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using EtlDesigner.Engine;
using EtlDesigner.Engine.Generation;
using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

public class MainViewModel : ObservableObject
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
    private bool _isRunInProgress;
    private bool _isGenerating;

    public MainViewModel()
    {
        Editor = new EditorViewModel();
        _filteredJsonOutput = "";
        _parsedJsonOutput = "";

        RunCommand = new AsyncRelayCommand(async _ => await RunAsync(), _ => !_isGenerating);
        GenerateDllCommand = new AsyncRelayCommand(async _ => await GenerateDllAsync(), _ => CanGenerateDll());

        HookCountryRefreshListeners();
        _ = RefreshCountriesAsync();
        UpdateCommandStates();
    }

    public EditorViewModel Editor { get; }

    public AsyncRelayCommand RunCommand { get; }

    public AsyncRelayCommand GenerateDllCommand { get; }

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
        if (_lastSuccessfulRunSettings == null)
        {
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
                "Using the DLL needs the ETLBox 3.11.1 packages and a valid ETLBox license at runtime. The free edition is limited to 5,000 rows.";
            System.Windows.MessageBox.Show(successMessage, "Generate DLL", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        finally
        {
            _isGenerating = false;
            UpdateCommandStates();
        }
    }

    private bool CanGenerateDll()
    {
        return _lastSuccessfulRunSettings != null && !_isRunInProgress && !_isGenerating;
    }

    private void ClearSuccessfulRunSnapshot()
    {
        _lastSuccessfulRunSettings = null;
    }

    private void UpdateCommandStates()
    {
        RunCommand.RaiseCanExecuteChanged();
        GenerateDllCommand.RaiseCanExecuteChanged();
    }

    private static bool IsInsideProjectFolder(string folderPath)
    {
        var current = new DirectoryInfo(folderPath);

        while (current != null)
        {
            if (current.EnumerateFiles("*.csproj", SearchOption.TopDirectoryOnly).Any()
                || current.EnumerateFiles("*.sln", SearchOption.TopDirectoryOnly).Any())
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

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
