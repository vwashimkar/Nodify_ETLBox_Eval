using System.IO;
using EtlDesigner.Engine.Csv;

namespace EtlDesigner.UI.ViewModels;

/// <summary>The CSV part node's background column read (G6c), like the CsvFile node's.</summary>
public partial class MainViewModel
{
    private readonly HashSet<(Guid NodeId, string Path, string SelectorJson)> _partReadsTried = new();

    /// <summary>The newest CSV part column read, for tests to await. Null before the first.</summary>
    internal Task? CsvPartRefresh { get; private set; }

    /// <summary>
    /// Reads the columns of each CSV part whose file and selector have no read yet: the locator, then the
    /// run's reader (<see cref="CsvPartColumns"/>), off the UI thread, once per node, file and selector. The
    /// result is kept on the node and the graph is validated again, so the node and Issues show the columns
    /// or the locator's sentence.
    /// </summary>
    private Task RefreshCsvPartColumnsAsync(CancellationToken token = default)
    {
        if (_isOpeningProject)
        {
            return Task.CompletedTask;
        }

        var pending = new List<(NodeViewModel Node, string Path, string Json)>();
        foreach (var node in Editor.Nodes.Where(n => n.NodeType == NodeType.CsvPart))
        {
            if (node.CsvPartSelector is null || node.CsvPartSelectorJson is not { } json || Editor.CsvPartFilePath(node) is not { } path
                || !File.Exists(path) || Editor.CurrentCsvPartRead(node) is not null || !_partReadsTried.Add((node.Id, path, json)))
            {
                continue;
            }

            pending.Add((node, path, json));
        }

        if (pending.Count == 0)
        {
            return Task.CompletedTask;
        }

        var task = ReadCsvPartsAsync(pending, token);
        CsvPartRefresh = task;
        return task;
    }

    private async Task ReadCsvPartsAsync(IReadOnlyList<(NodeViewModel Node, string Path, string Json)> pending, CancellationToken token)
    {
        foreach (var (node, path, json) in pending)
        {
            node.IsRefreshing = true;
            node.CsvPartStatus = EditorViewModel.CsvPartReading;
        }

        foreach (var (node, path, json) in pending)
        {
            try
            {
                var selector = node.CsvPartSelector!;
                var result = await Task.Run(() => CsvPartColumns.ReadAsync(path, selector, token));
                if (!token.IsCancellationRequested && node.CsvPartSelectorJson == json)
                {
                    node.CsvPartRead = new CsvPartRead(path, json, result);
                }
            }
            catch (Exception ex)
            {
                Editor.Issues.Add($"{node.Title}: could not read the columns of the part. Open the file in Intake Studio to check it. Details: {ex.Message}");
            }
            finally
            {
                node.IsRefreshing = false;
            }
        }

        if (!token.IsCancellationRequested)
        {
            Editor.Validate();
        }
    }
}
