using System.Windows;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Intake;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.UI.Mvvm;
using EtlDesigner.UI.Services;

namespace EtlDesigner.UI.ViewModels;

/// <summary>
/// The guided builder (task G7a) on the main window: the "Guided" command, the question before an existing
/// pipeline is replaced, and the chain the builder puts on the canvas (File intake -> CSV part -> DotNetObject,
/// the same nodes a user connects by hand), so Execute and Generate DLL work on it as on any other pipeline.
/// </summary>
public partial class MainViewModel
{
    public const string GuidedReplaceQuestion = "The canvas already has nodes. Replace them with the reader you build in the guided builder?";

    private RelayCommand? _guidedCommand;

    /// <summary>Opens the guided builder ("Build a reader").</summary>
    public RelayCommand GuidedCommand => _guidedCommand ??= new RelayCommand(_ => ShowGuidedBuilder(), _ => !_isRunInProgress && !_isGenerating);

    /// <summary>The builder of the last Guided command (for tests).</summary>
    internal GuidedBuilderViewModel? LastGuidedBuilder { get; private set; }

    /// <summary>
    /// Opens the guided builder. A canvas with nodes that are not already a guided chain asks first (default No);
    /// on No nothing opens. Returns the builder that was shown, or null.
    /// </summary>
    internal GuidedBuilderViewModel? ShowGuidedBuilder()
    {
        if (Editor.Nodes.Count > 0 && FindGuidedChain() is null && !_dialogService.AskYesNo("Build a reader", GuidedReplaceQuestion))
        {
            return null;
        }

        try
        {
            var builder = CreateGuidedBuilder();
            LastGuidedBuilder = builder;
            _dialogService.ShowGuidedBuilder(builder);
            return builder;
        }
        catch (Exception ex)
        {
            try
            {
                ErrorLog.Write("Guided builder", ex);
            }
            catch (Exception)
            {
                // The log could not be written; the message still shows.
            }

            _dialogService.ShowError("Build a reader", "The guided builder could not be opened. The details are in unhandled-exception.log next to the program.");
            return null;
        }
    }

    /// <summary>A builder wired to this window's canvas, dialogs, lineage log and template store.</summary>
    internal GuidedBuilderViewModel CreateGuidedBuilder()
        => new(this,
            _lineageLog ?? AppLineage.Shared,
            _templateStore ?? AppTemplates.Shared,
            _templateStore is null ? AppTemplates.Settings : null,
            _dialogService.PickIntakeFile,
            _dialogService.PickFolder,
            question => _dialogService.AskYesNo("Build a reader", question),
            _dialogService.ShowIntakeStudio);

    /// <summary>The File intake, CSV part and DotNetObject nodes when the canvas holds exactly that chain, connected in order; else null.</summary>
    internal (NodeViewModel Intake, NodeViewModel Part, NodeViewModel Output)? FindGuidedChain()
    {
        var nodes = Editor.Nodes.Where(n => n.NodeType != NodeType.Unknown).ToList();
        var intake = nodes.FirstOrDefault(n => n.NodeType == NodeType.FileIntake);
        var part = nodes.FirstOrDefault(n => n.NodeType == NodeType.CsvPart);
        var output = nodes.FirstOrDefault(n => n.NodeType == NodeType.DotNetObject);
        if (nodes.Count != 3 || intake is null || part is null || output is null || Editor.Connections.Count != 2
            || Editor.FileIntakeOf(part) != intake
            || !Editor.Connections.Any(c => c.Source.ParentNode == part && c.Target.ParentNode == output))
        {
            return null;
        }

        return (intake, part, output);
    }

    /// <summary>
    /// Puts the guided chain on the canvas: the existing guided chain is updated in place; anything else is
    /// cleared first (the builder asked when it opened). Then the File intake settings and saved layout, the
    /// part, the column choice and the split are set, and the graph is validated, which starts the part's
    /// background read (<see cref="CsvPartRefresh"/>). Returns the CSV part node.
    /// </summary>
    internal NodeViewModel ApplyGuidedChain(FileIntakeNodeSettings intakeSettings, string? layoutPlanJson, CsvPartSelector selector,
        CsvColumnSelection columns, ColumnSplitSettings? split)
    {
        ArgumentNullException.ThrowIfNull(intakeSettings);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(columns);

        if (FindGuidedChain() is not { } chain)
        {
            Editor.ClearCanvas();
            Editor.AddNode(NodeType.FileIntake, new Point(40, 60));
            Editor.AddNode(NodeType.CsvPart, new Point(260, 60));
            Editor.AddNode(NodeType.DotNetObject, new Point(480, 60));
            var (intake, part, output) = (Editor.Nodes[0], Editor.Nodes[1], Editor.Nodes[2]);
            Editor.StartConnection(intake.Outputs[0]);
            Editor.CompleteConnection(part.Inputs[0]);
            Editor.StartConnection(part.Outputs[0]);
            Editor.CompleteConnection(output.Inputs[0]);
            chain = (intake, part, output);
        }

        chain.Intake.SetFileIntakeSettings(intakeSettings);
        chain.Intake.IntakePlanJson = layoutPlanJson;
        if (chain.Part.CsvPartSelectorJson is not { } old || !SamePart(old, selector))
        {
            chain.Part.CsvPartSelectorJson = CsvPartSelectorJson.Serialize(selector);
        }

        chain.Part.ColumnSelection.Mode = columns.Mode;
        chain.Part.ColumnSelection.Names.Clear();
        chain.Part.ColumnSelection.Names.AddRange(columns.Names);
        chain.Part.NotifyColumnSelectionChanged();
        chain.Part.ColumnSplit = split;
        Editor.Validate();
        return chain.Part;
    }
}
