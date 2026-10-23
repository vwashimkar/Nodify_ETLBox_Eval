using EtlDesigner.Engine;
using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

public class MainViewModel : ObservableObject
{
    private string _filteredJsonOutput;
    private string _parsedJsonOutput;

    public MainViewModel()
    {
        Editor = new EditorViewModel();
        _filteredJsonOutput = "";
        _parsedJsonOutput = "";

        RunCommand = new AsyncRelayCommand(async _ => await RunAsync());
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

        SetState(chain[0], NodeState.Running);

        PipelineRunResult runResult;
        try
        {
            runResult = await Task.Run(() => PipelineRunner.RunWithStats(settings));
        }
        catch (Exception ex)
        {
            SetFailed(chain, ex.Message);
            Editor.Issues.Add($"CsvSource: {ex.Message}");
            FilteredJsonOutput = "[]";
            ParsedJsonOutput = "[]";
            return;
        }

        var result = runResult.Result;
        var stats = runResult.Stats;

        SetNodeStats(chain, stats);

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            SetFailed(chain, result.ErrorMessage);
            Editor.Issues.Add($"CsvSource: {result.ErrorMessage}");
            FilteredJsonOutput = result.FilteredJson;
            ParsedJsonOutput = result.ParsedJson;
            return;
        }

        FilteredJsonOutput = string.IsNullOrWhiteSpace(result.FilteredJson) ? "[]" : result.FilteredJson;
        ParsedJsonOutput = string.IsNullOrWhiteSpace(result.ParsedJson) ? "[]" : result.ParsedJson;

        foreach (var node in chain)
        {
            node.State = NodeState.Succeeded;
        }
    }

    private static void SetState(NodeViewModel node, NodeState state)
    {
        node.State = state;
        node.ErrorMessage = null;
    }

    private static void SetNodeStats(IReadOnlyList<NodeViewModel> chain, PipelineExecutionStats stats)
    {
        // CsvSource
        chain[0].RowCount = stats.RowsRead;

        // FilterByCountry shows matched rows
        chain[1].RowCount = stats.RowsMatched;

        // ToJson output count == matched rows
        chain[2].RowCount = stats.RowsMatched;

        // ParseJson parsed records
        chain[3].RowCount = stats.ParsedRecordCount;

        // ResultJson parsed records
        chain[4].RowCount = stats.ParsedRecordCount;

        chain[1].ErrorMessage = $"not-matching: {stats.RowsNotMatched}, malformed-skipped: {stats.RowsMalformedSkipped}";
    }

    private static void SetFailed(IReadOnlyList<NodeViewModel> chain, string errorMessage)
    {
        foreach (var node in chain)
        {
            node.State = NodeState.Failed;
            node.ErrorMessage = errorMessage;
        }
    }
}
