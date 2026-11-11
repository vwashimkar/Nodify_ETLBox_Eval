using System.IO;
using System.Windows;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Intake;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.UI.Services;

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

    public const string PartAlreadyUsedText = "This part is already the CSV part of the pipeline.";

    public const string PartNotChangedText = "The CSV part of the pipeline was not changed.";

    /// <summary>Right of the File intake node, where a new CSV part node goes (and further down while a node is in the way).</summary>
    private static readonly Vector NewPartOffset = new(200, 0);
    private static readonly Vector NewPartStep = new(0, 140);

    public static string PartChosenText(int partNumber) => $"Part {partNumber} is now the CSV part of this pipeline.";

    public static string ReplacePartQuestion(string partTitle) => $"Replace the part chosen in '{partTitle}' with this one?";

    public static string OtherIntakeText(string intakeTitle, string partTitle)
        => $"'{partTitle}' already reads a part of the file of '{intakeTitle}', and a pipeline reads one CSV part. Remove that node first.";

    /// <summary>
    /// "Use in pipeline" from Intake Studio of <paramref name="intake"/> (G6d): the pipeline's one CSV part node gets
    /// <paramref name="selector"/> and is connected to <paramref name="intake"/>. The node fed by this File intake is
    /// used; else a CSV part node without a File intake; else a new node next to the File intake node. A different
    /// part already chosen is replaced only after Yes; the same part (by selector, ignoring the template's id and
    /// time) changes nothing. When another File intake already feeds a CSV part, nothing changes. Only the CSV part
    /// node is touched; ParseValue and DotNetObject are left to the user. The background read starts through Validate.
    /// </summary>
    internal UsePartOutcome UseCsvPart(NodeViewModel intake, CsvPartSelector selector)
    {
        ArgumentNullException.ThrowIfNull(intake);
        ArgumentNullException.ThrowIfNull(selector);

        var part = Editor.Connections.Where(c => c.Source.ParentNode == intake && c.Target.ParentNode.NodeType == NodeType.CsvPart)
            .Select(c => c.Target.ParentNode).FirstOrDefault();
        if (part is null)
        {
            var other = Editor.Connections.FirstOrDefault(c => c.Source.ParentNode.NodeType == NodeType.FileIntake && c.Target.ParentNode.NodeType == NodeType.CsvPart);
            if (other is not null)
            {
                return new UsePartOutcome(false, OtherIntakeText(other.Source.ParentNode.Title, other.Target.ParentNode.Title));
            }

            // A CSV part node dragged from the palette and not connected yet is used rather than adding a second one.
            part = Editor.Nodes.FirstOrDefault(n => n.NodeType == NodeType.CsvPart && Editor.FileIntakeOf(n) is null);
        }

        var same = part?.CsvPartSelectorJson is { } old && SamePart(old, selector);
        var connected = part is not null && Editor.FileIntakeOf(part) == intake;
        if (same && connected)
        {
            return new UsePartOutcome(false, PartAlreadyUsedText);
        }

        if (part?.CsvPartSelectorJson is not null && !same && !_dialogService.AskYesNo("Use in pipeline", ReplacePartQuestion(part.Title)))
        {
            return new UsePartOutcome(false, PartNotChangedText);
        }

        if (part is null)
        {
            Editor.AddNode(NodeType.CsvPart, FreeSpotNextTo(intake));
            part = Editor.Nodes[^1];
        }

        if (!connected)
        {
            Editor.StartConnection(intake.Outputs[0]);
            Editor.CompleteConnection(part.Inputs[0]);
            if (Editor.FileIntakeOf(part) != intake)
            {
                return new UsePartOutcome(false, Editor.Issues.LastOrDefault() ?? PartNotChangedText);
            }
        }

        if (!same)
        {
            // Marks the project dirty and clears the tested run; the old read and columns are forgotten.
            part.CsvPartSelectorJson = CsvPartSelectorJson.Serialize(selector);
        }

        Editor.Validate();
        return new UsePartOutcome(true, PartChosenText(selector.PartIndex + 1));
    }

    /// <summary>
    /// The CSV part settings dialog (G6d), read-only. After it closed: "Open in Intake Studio" opens the Studio of the
    /// connected File intake node; "Choose columns" opens Data Studio on the located part, and the column choice is
    /// saved on the node (Execute and the ParseValue columns use it).
    /// </summary>
    internal bool ShowCsvPartSettings(NodeViewModel node)
    {
        try
        {
            var intake = Editor.FileIntakeOf(node);
            var read = Editor.CurrentCsvPartRead(node);
            var viewModel = new CsvPartSettingsViewModel(node, Editor.CsvPartFilePath(node), intake is not null, read);
            _dialogService.ShowCsvPartSettings(viewModel);
            switch (viewModel.Next)
            {
                case CsvPartSettingsNext.OpenIntakeStudio when intake is not null:
                    return ShowIntakeStudio(intake);
                case CsvPartSettingsNext.ChooseColumns when read?.Result.Location.Part is { } part:
                    _dialogService.ShowCsvPartColumns(node, part);
                    Editor.Validate();
                    break;
            }

            return true;
        }
        catch (Exception ex)
        {
            try
            {
                ErrorLog.Write("CSV part settings", ex);
            }
            catch (Exception)
            {
                // The log could not be written; the message still shows.
            }

            _dialogService.ShowError("CSV part", "The CSV part settings could not be opened. The details are in unhandled-exception.log next to the program.");
            return false;
        }
    }

    /// <summary>
    /// One <c>pipeline.executed</c> lineage event for a CSV part run (G6d): file name, SHA-256, template, match, part
    /// and its lines, the counts, elapsed ms and cancelled. When the part was not found: the failure kind and no
    /// counts. No path, user, machine name or content. The id is a new run id (a run has no intake id). A CsvFile run
    /// writes nothing, and neither does the generated DLL.
    /// </summary>
    private void WritePipelineExecuted(PipelineResult result)
    {
        if (result.CsvPart is not { } facts)
        {
            return;
        }

        var details = new Dictionary<string, object?>
        {
            ["templateId"] = facts.TemplateId.ToString("D"),
            ["templateName"] = facts.TemplateName,
            ["match"] = facts.Match,
            ["similarity"] = facts.Similarity,
            ["partNumber"] = facts.PartNumber,
            ["firstLine"] = facts.FirstLine,
            ["lastLine"] = facts.LastLine
        };

        if (facts.Failure is { } failure)
        {
            details["failure"] = failure;
        }
        else
        {
            details["read"] = result.Counts.Read;
            details["parsed"] = result.Counts.Parsed;
            details["rejected"] = result.Counts.Rejected;
            details["flagged"] = result.Counts.Flagged;
            details["out"] = result.Counts.Out;
        }

        details["elapsedMs"] = result.ElapsedMs;
        details["cancelled"] = result.Cancelled;
        (_lineageLog ?? AppLineage.Shared).Write(new LineageEvent("pipeline.executed", Guid.NewGuid().ToString("D"), facts.FileName, facts.Sha256, details));
    }

    /// <summary>Two selectors choose the same part when they are equal apart from the template's id and creation time (new on each build).</summary>
    internal static bool SamePart(string json, CsvPartSelector selector)
    {
        if (CsvPartSelectorJson.Deserialize(json, out _) is not { } old)
        {
            return false;
        }

        static string Normal(CsvPartSelector s) => CsvPartSelectorJson.Serialize(s with { Template = s.Template with { Id = Guid.Empty, CreatedUtc = default } });
        return Normal(old) == Normal(selector);
    }

    private Point FreeSpotNextTo(NodeViewModel intake)
    {
        var spot = intake.Location + NewPartOffset;
        for (var i = 0; i < 10 && Editor.Nodes.Any(n => Math.Abs(n.Location.X - spot.X) < NewPartOffset.X && Math.Abs(n.Location.Y - spot.Y) < NewPartStep.Y); i++)
        {
            spot += NewPartStep;
        }

        return spot;
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
