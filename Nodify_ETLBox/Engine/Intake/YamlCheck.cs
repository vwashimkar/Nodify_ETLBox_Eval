using System.IO;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace EtlDesigner.Engine.Intake;

/// <summary>What the parse-only YAML check found. Line and column are 1-based inside the checked text.</summary>
internal sealed record YamlOutcome(
    bool Parsed,
    bool RootIsCollection,
    bool ExplicitDocument,
    long? ErrorLine,
    int? ErrorColumn,
    string? ErrorReason,
    string? LimitReason);

/// <summary>
/// YAML is only parsed, never deserialized: the YamlDotNet parser gives events (scalars, the start
/// and end of mappings and sequences, aliases), and nothing is turned into objects, so no tag or
/// type is resolved and an alias is never expanded. Nodes, nesting depth and alias references are
/// counted and the check stops at the limits of <see cref="ValidationSettings"/>, so a hostile
/// file cannot use much memory or time.
/// </summary>
internal static class YamlCheck
{
    public static YamlOutcome Check(TextReader text, ValidationSettings limits, CancellationToken token)
    {
        var parser = new Parser(text);
        var nodes = 0;
        var depth = 0;
        var aliases = 0;
        var anyCollectionRoot = false;
        var anyScalarRoot = false;
        var explicitDocument = false;

        YamlOutcome Limit(string reason) => new(false, false, explicitDocument, null, null, null, reason);

        try
        {
            while (parser.MoveNext())
            {
                if ((nodes & 1023) == 0 && token.IsCancellationRequested)
                {
                    return Limit("the check was cancelled");
                }

                switch (parser.Current)
                {
                    case DocumentStart document:
                        explicitDocument |= !document.IsImplicit;
                        break;
                    case MappingStart or SequenceStart:
                        anyCollectionRoot |= depth == 0;
                        nodes++;
                        if (++depth > limits.YamlMaxDepth)
                        {
                            return Limit($"it is nested deeper than {IntakeText.Count(limits.YamlMaxDepth, "level")}");
                        }

                        break;
                    case MappingEnd or SequenceEnd:
                        depth--;
                        break;
                    case Scalar:
                        anyScalarRoot |= depth == 0;
                        nodes++;
                        break;
                    case AnchorAlias:
                        nodes++;
                        if (++aliases > limits.YamlMaxAliases)
                        {
                            return Limit($"it has more than {IntakeText.Count(limits.YamlMaxAliases, "alias reference")}");
                        }

                        break;
                }

                if (nodes > limits.YamlMaxNodes)
                {
                    return Limit($"it has more than {IntakeText.Count(limits.YamlMaxNodes, "node")}");
                }
            }

            return new YamlOutcome(true, anyCollectionRoot && !anyScalarRoot, explicitDocument, null, null, null, null);
        }
        catch (YamlException ex)
        {
            return new YamlOutcome(false, false, explicitDocument, (long)ex.Start.Line, (int)ex.Start.Column, CleanReason(ex.Message), null);
        }
    }

    /// <summary>The parser's message without its position prefix "(Line: 3, Col: 5, Idx: 20) - (...): ".</summary>
    private static string CleanReason(string message)
    {
        var at = message.LastIndexOf("): ", StringComparison.Ordinal);
        var reason = (at >= 0 ? message[(at + 3)..] : message).Trim().TrimEnd('.');
        return reason.Length == 0 ? "the structure is not valid" : char.ToLowerInvariant(reason[0]) + reason[1..];
    }
}
