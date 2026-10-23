using EtlDesigner.Engine;
using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

public class MainViewModel : ObservableObject
{
    private string _filteredJsonOutput;
    private string _parsedJsonOutput;
    private int _countryLoadVersion;

    public MainViewModel()
    {
        Editor = new EditorViewModel();
        _filteredJsonOutput = "";
        _parsedJsonOutput = "";

        RunCommand = new AsyncRelayCommand(async _ => await RunAsync());

        HookCountryRefreshListeners();
        _ = RefreshCountriesAsync();
    }

    public EditorViewModel Editor { get; }

    public AsyncRelayCommand RunCommand { get; }

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
            Editor.Issues.Add($"CsvSource: {ex.Message}");
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
