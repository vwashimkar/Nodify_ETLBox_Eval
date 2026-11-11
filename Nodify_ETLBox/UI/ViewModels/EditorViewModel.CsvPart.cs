using System.IO;
using EtlDesigner.Engine.Csv;

namespace EtlDesigner.UI.ViewModels;

/// <summary>
/// The column read of a CSV part node for one file and one selector (<see cref="CsvPartColumns"/>): it
/// counts only while the node still has that file and that selector.
/// </summary>
public sealed record CsvPartRead(string Path, string SelectorJson, CsvPartColumnsResult Result);

/// <summary>The CSV part node on the canvas (G6c): its Issues lines, status and columns.</summary>
public partial class EditorViewModel
{
    public const string CsvPartNoSelector = "No part is chosen yet. Choose a CSV part of the file in Intake Studio.";

    public const string CsvPartNoFileIntake = "Connect a File intake node to the input of this node.";

    public const string CsvPartNoFile = "The File intake node has no file yet. Choose a file in the File intake node.";

    public const string CsvPartNotToParseValue = "Connect the output of this node to a ParseValue or DotNetObject node.";

    /// <summary>A column split runs only in a guided pipeline (CSV part -> DotNetObject).</summary>
    public const string CsvPartSplitWithParseValue = "The column split runs only when this node feeds a DotNetObject node directly; remove the split or the ParseValue node.";

    public const string CsvPartReady = "Ready";

    public const string CsvPartReading = "Reading the part...";

    /// <summary>The line when the File intake node's file is missing.</summary>
    public static string CsvPartFileMissing(string intakeTitle, string path)
        => $"The file of '{intakeTitle}' was not found ({Path.GetFileName(path)}). Choose the file again in the File intake node.";

    /// <summary>The line on each CSV part node when the canvas has more than one.</summary>
    public static string CsvPartSeveral(int count) => $"The canvas has {count} CSV part nodes; a pipeline reads one. Remove the others.";

    /// <summary>The File intake node connected to a CSV part's input, or null.</summary>
    public NodeViewModel? FileIntakeOf(NodeViewModel csvPart)
        => Connections.Where(c => c.Target.ParentNode == csvPart).Select(c => c.Source.ParentNode).FirstOrDefault(n => n.NodeType == NodeType.FileIntake);

    /// <summary>The path of the File intake node feeding a CSV part, trimmed, or null when none is connected or it has no path.</summary>
    public string? CsvPartFilePath(NodeViewModel csvPart)
        => FileIntakeOf(csvPart)?.GetFileIntakeSettings().Path is { } path && !string.IsNullOrWhiteSpace(path) ? path.Trim() : null;

    /// <summary>The node's read when it belongs to the node's current file and selector, else null.</summary>
    public CsvPartRead? CurrentCsvPartRead(NodeViewModel csvPart)
        => csvPart.CsvPartRead is { } read && read.Path == CsvPartFilePath(csvPart) && read.SelectorJson == csvPart.CsvPartSelectorJson ? read : null;

    /// <summary>
    /// Each problem once, with the node title in front, in this order: the selector, the File intake node
    /// and its file, the locator's sentence from the last column read (unchanged), the ParseValue connection,
    /// and more than one CSV part. Configured = no problem. The status line shows the first problem, or
    /// whether the columns are read.
    /// </summary>
    private void ValidateCsvPartNodes()
    {
        var parts = Nodes.Where(n => n.NodeType == NodeType.CsvPart).ToList();
        foreach (var node in parts)
        {
            var messages = new List<string>();
            if (node.CsvPartSelectorJson is null)
            {
                messages.Add(CsvPartNoSelector);
            }
            else if (node.CsvPartSelectorError is { } selectorError)
            {
                messages.Add(selectorError);
            }

            var intake = FileIntakeOf(node);
            var path = CsvPartFilePath(node);
            if (intake is null)
            {
                messages.Add(CsvPartNoFileIntake);
            }
            else if (path is null)
            {
                messages.Add(CsvPartNoFile);
            }
            else if (!File.Exists(path))
            {
                messages.Add(CsvPartFileMissing(intake.Title, path));
            }

            var read = CurrentCsvPartRead(node);
            if (messages.Count == 0 && read?.Result.Error is { } readError)
            {
                messages.Add(readError);
            }

            node.KnownColumns = messages.Count == 0 && read is { Result.Ok: true } ? read.Result.Columns : null;

            var feedsParseValue = Connections.Any(c => c.Source.ParentNode == node && c.Target.ParentNode.NodeType == NodeType.ParseValue);
            if (!feedsParseValue && !Connections.Any(c => c.Source.ParentNode == node && c.Target.ParentNode.NodeType == NodeType.DotNetObject))
            {
                messages.Add(CsvPartNotToParseValue);
            }

            if (node.ColumnSplit is { } split)
            {
                if (feedsParseValue)
                {
                    messages.Add(CsvPartSplitWithParseValue);
                }
                else
                {
                    messages.AddRange(EtlDesigner.Engine.Pipeline.ColumnSplit.Validate(split, node.KnownColumns is { } known ? node.ColumnSelection.KeptColumns(known) : null));
                }
            }

            if (parts.Count > 1)
            {
                messages.Add(CsvPartSeveral(parts.Count));
            }

            foreach (var message in messages.Distinct())
            {
                Issues.Add($"{node.Title}: {message}");
            }

            node.ConfigurationStatus = messages.Count == 0 ? "Configured" : "Not configured";
            node.CsvPartStatus = messages.Count > 0 ? messages[0]
                : node.KnownColumns is not null ? CsvPartReady
                : CsvPartReading;
        }
    }
}
