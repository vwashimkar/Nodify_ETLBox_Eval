using EtlDesigner.Engine.Csv;
using EtlDesigner.UI.ViewModels;

namespace EtlDesigner.UI.Services;

/// <summary>
/// What a ParseValue node gets from its input. <see cref="Settings"/> is set when a configured
/// CsvFile or CSV part node is connected; <see cref="KeptColumns"/> also needs that node's columns to be known.
/// <see cref="Message"/> says what to do when something is missing. For a CSV part, <see cref="Part"/> is the
/// located part (samples are read from it, never from the whole file) and <see cref="Settings"/> mirrors it.
/// </summary>
public sealed record ParseValueUpstream(
    NodeViewModel? CsvNode,
    CsvFileSettings? Settings,
    CsvColumnSelection? Selection,
    IReadOnlyList<string>? KeptColumns,
    string? Message,
    CsvPartSource? Part = null)
{
    public bool IsResolved => Message is null;
}

public static class ParseValueUpstreamResolver
{
    public const string NotConnectedMessage = "Connect a configured CsvFile node to the input of this node.";

    public const string ColumnsUnknownMessage = "The columns of the CsvFile node are not known yet. Press Load preview on the CsvFile node, then Save.";

    public const string PartNotReadyMessage = "The CSV part node is not ready. See its line in the Issues tab.";

    /// <summary>A CSV part's sample in the ParseValue dialog: this many rows of the part.</summary>
    public const int CsvPartSampleRows = 100;

    public static ParseValueUpstream Resolve(NodeViewModel node, IEnumerable<ConnectionViewModel> connections)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(connections);

        var upstreamNode = connections
            .Where(c => c.Target.ParentNode == node)
            .Select(c => c.Source.ParentNode)
            .FirstOrDefault(n => n.NodeType is NodeType.CsvFile or NodeType.CsvPart);

        if (upstreamNode is { NodeType: NodeType.CsvPart })
        {
            return ResolvePart(upstreamNode);
        }

        var csvNode = upstreamNode;

        if (csvNode is null || !string.Equals(csvNode.ConfigurationStatus, "Configured", StringComparison.Ordinal))
        {
            return new ParseValueUpstream(csvNode, null, null, null, NotConnectedMessage);
        }

        var settings = csvNode.GetCsvFileSettings();
        var selection = csvNode.ColumnSelection.Clone();
        if (csvNode.KnownColumns is null)
        {
            return new ParseValueUpstream(csvNode, settings, selection, null, ColumnsUnknownMessage);
        }

        return new ParseValueUpstream(csvNode, settings, selection, KeptColumns(csvNode.KnownColumns, selection), null);
    }

    /// <summary>
    /// A CSV part is ready when it is Configured (its Issues lines are empty, so its read is current) and its
    /// columns are read (<see cref="NodeViewModel.KnownColumns"/>, from the run's reader).
    /// </summary>
    private static ParseValueUpstream ResolvePart(NodeViewModel partNode)
    {
        if (!string.Equals(partNode.ConfigurationStatus, "Configured", StringComparison.Ordinal)
            || partNode.KnownColumns is not { } columns
            || partNode.CsvPartRead?.Result.Location.Part is not { } part)
        {
            return new ParseValueUpstream(partNode, null, null, null, PartNotReadyMessage);
        }

        var settings = new CsvFileSettings(part.Path, part.Delimiter, part.HasHeader, part.SkipLines, part.EncodingName, CsvPartSampleRows);
        var selection = partNode.ColumnSelection.Clone();
        return new ParseValueUpstream(partNode, settings, selection, KeptColumns(columns, selection), null, part);
    }

    /// <summary>
    /// The columns that leave the CsvFile node, in file order. Names match without case, as in
    /// Data Studio.
    /// </summary>
    public static IReadOnlyList<string> KeptColumns(IReadOnlyList<string> columns, CsvColumnSelection selection)
        => selection.KeptColumns(columns);

    /// <summary>
    /// Reads one row through <see cref="CsvPreviewReader"/> (the same path as the preview) and
    /// stores the column names on the CsvFile node. Returns false, and leaves the node alone, when
    /// the read fails, is cancelled, or a setting changed while it ran.
    /// </summary>
    public static async Task<bool> RefreshColumnsAsync(NodeViewModel csvNode, CsvPreviewReader reader, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(csvNode);
        ArgumentNullException.ThrowIfNull(reader);

        var settings = csvNode.GetCsvFileSettings() with { PreviewRows = 1 };
        var preview = await reader.ReadAsync(settings, token);
        if (preview.Cancelled || preview.Error is not null || preview.Columns.Count == 0)
        {
            return false;
        }

        if (csvNode.GetCsvFileSettings() with { PreviewRows = 1 } != settings)
        {
            return false;
        }

        csvNode.KnownColumns = preview.Columns;
        return true;
    }
}
