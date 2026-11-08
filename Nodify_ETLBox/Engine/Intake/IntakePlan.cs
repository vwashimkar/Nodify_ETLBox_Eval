using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EtlDesigner.Engine.Intake;

/// <summary>
/// One container of an <see cref="IntakePlan"/>: its lines and byte offsets in the file, its format
/// and options, its name, and the fields the detector gave it, so a plan applied to the same file
/// gives the same containers. Lines and offsets mean the same as in <see cref="ContainerInfo"/>.
/// </summary>
public sealed record PlanContainer(
    long? FirstLine,
    long? LastLine,
    string Format,
    string? SubType,
    string? Delimiter,
    HeaderGuess? Header,
    int SkipLines,
    KeyValueForm? KeyValueForm,
    string Name,
    bool ConfirmedByUser,
    ContainerKind Kind = ContainerKind.Detected,
    long? MarkerBeginLine = null,
    long? MarkerEndLine = null,
    long? StartByte = null,
    long? EndByte = null,
    ContainerConfidence Confidence = ContainerConfidence.Sure,
    string? SubTypeVersion = null,
    string Notes = "",
    ContainerStatus Status = ContainerStatus.Ok,
    string Message = "",
    IReadOnlyList<string>? Reasons = null);

/// <summary>
/// The containers of one file as the user left them in an <see cref="IntakeEditSession"/> (plan
/// section 6, first step). Tied to the file by its SHA-256 and size: it is applied only to the same
/// bytes read in the same mode. Saved as JSON by <see cref="IntakePlanJson"/>.
/// </summary>
public sealed record IntakePlan(
    int FormatVersion,
    string Sha256,
    long FileSize,
    string? Encoding,
    IntakeMode Mode,
    IReadOnlyList<PlanContainer> Containers)
{
    public const int CurrentFormatVersion = 1;

    public const string ChangedFileMessage = "The file has changed since this layout was saved.";

    public const string DoesNotFitMessage = "The saved layout does not fit this file.";

    /// <summary>The session's current containers as a plan. Written to the session's lineage log as <c>plan.saved</c>.</summary>
    public static IntakePlan FromSession(IntakeEditSession session, IntakeScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(scan);
        var file = scan.Intake?.File ?? throw new ArgumentException("Only a scan that read the whole file gives a plan.", nameof(scan));

        var plan = new IntakePlan(CurrentFormatVersion, file.Sha256, file.Size, scan.Intake.EncodingUsed ?? file.Encoding, scan.Mode,
            session.Containers.Select(c => new PlanContainer(
                c.ContentFirstLine, c.ContentLastLine, c.Format, c.SubType, c.Delimiter, c.HeaderGuess, c.SkipLines, c.KeyValueForm, c.Name, c.ConfirmedByUser,
                c.Kind, c.MarkerBeginLine, c.MarkerEndLine, c.ContentStartByte, c.ContentEndByte, c.Confidence, c.SubTypeVersion, c.Notes, c.Status, c.Message,
                c.Reasons)).ToList());
        session.Log("plan.saved", new()
        {
            ["containers"] = plan.Containers.Count,
            ["confirmedByUser"] = plan.Containers.Count(c => c.ConfirmedByUser),
            ["formatVersion"] = plan.FormatVersion
        });
        return plan;
    }

    /// <summary>
    /// Gives the plan's containers for <paramref name="scan"/> when (a) the file's SHA-256 and size
    /// and the reading mode are the plan's, (b) every range is inside the file, in order and not
    /// overlapping, (c) every line the scan put in a container (so every non-blank line) is in one of
    /// them, and (d) formats and options pass the same checks as the edits. Otherwise nothing is
    /// given and <paramref name="message"/> says why in one sentence.
    /// </summary>
    public static bool TryApply(IntakeScanResult scan, IntakePlan plan, out IReadOnlyList<ContainerInfo> containers, out string message)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(plan);
        containers = [];

        if (scan.Intake is not { File: { } file, Error: null, Cancelled: false } intake)
        {
            message = "The file was not read to the end, so the saved layout cannot be applied.";
            return false;
        }

        if (plan.FormatVersion != CurrentFormatVersion)
        {
            message = IntakePlanJson.VersionMessage(plan.FormatVersion);
            return false;
        }

        if (!string.Equals(plan.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase) || plan.FileSize != file.Size)
        {
            message = ChangedFileMessage;
            return false;
        }

        if (plan.Mode != scan.Mode)
        {
            message = "The saved layout was made with another reading mode, so it does not fit this scan.";
            return false;
        }

        var result = new List<ContainerInfo>(plan.Containers.Count);
        long previousEnd = 0;
        foreach (var p in plan.Containers)
        {
            var number = result.Count + 1;
            if (Problem(p, number, file.Size, ref previousEnd) is { } problem)
            {
                message = problem;
                return false;
            }

            var count = p.FirstLine is { } first && p.LastLine is { } last ? last - first + 1 : 0;
            result.Add(new ContainerInfo(number, p.Kind, p.Format, p.Name, p.Notes, p.MarkerBeginLine, p.MarkerEndLine, p.FirstLine, p.LastLine, count,
                p.StartByte, p.EndByte, p.Status, p.Message, p.Confidence, p.SubType, p.Delimiter, p.Header, p.SkipLines, p.KeyValueForm, p.Reasons,
                p.SubTypeVersion, p.ConfirmedByUser));
        }

        var required = intake.Containers.Select(IntakeEditSession.Span).Where(s => s is not null).Select(s => s!.Value).ToList();
        if (!IntakeEditSession.Covers(result, required, file.LineCount))
        {
            message = DoesNotFitMessage;
            return false;
        }

        containers = result;
        message = string.Empty;
        return true;
    }

    /// <summary>Null when the container passes the checks of the edits, else one sentence.</summary>
    private static string? Problem(PlanContainer p, int number, long fileSize, ref long previousEnd)
    {
        string Bad(string why) => $"The saved layout does not fit this file: container {number} {why}.";

        if (p.Format is null || (IntakeEditRules.FormatName(p.Format) != p.Format && !MarkerProfile.DefaultKnownFormats.Contains(p.Format)))
        {
            return Bad($"has the unknown format '{IntakeEditRules.Cut(p.Format ?? string.Empty)}'");
        }

        if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Any(char.IsControl) || (p.ConfirmedByUser && p.Name.Trim().Length > IntakeEditRules.MaxNameLength))
        {
            return Bad("has a name that is empty, too long or contains a control character");
        }

        if (!IntakeEditRules.IsDelimiterValue(p.Format, p.Delimiter))
        {
            return Bad("has a delimiter its format does not allow");
        }

        if ((p.FirstLine is null) != (p.LastLine is null) || (p.StartByte is null) != (p.EndByte is null) || (p.FirstLine is null) != (p.StartByte is null)
            || p.FirstLine < 1 || p.LastLine < p.FirstLine)
        {
            return Bad("has a line range that is not valid");
        }

        var lines = p.FirstLine is { } first ? p.LastLine!.Value - first + 1 : 0;
        if (p.SkipLines < 0 || p.SkipLines > Math.Max(0, lines - 1))
        {
            return Bad("has more skip lines than lines");
        }

        if (p.StartByte is { } start)
        {
            if (start < previousEnd || p.EndByte < start || p.EndByte > fileSize)
            {
                return Bad("has a byte range that is not valid");
            }

            previousEnd = p.EndByte!.Value;
        }

        return null;
    }
}

/// <summary>An <see cref="IntakePlan"/> as JSON: indented, camelCase, enums and names as strings; unknown properties are ignored.</summary>
public static class IntakePlanJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(IntakePlan plan) => JsonSerializer.Serialize(plan, Options);

    /// <summary>The plan, or null with one plain sentence in <paramref name="error"/>.</summary>
    public static IntakePlan? Deserialize(string json, out string? error)
    {
        error = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("formatVersion", out var version) || !version.TryGetInt32(out var number) || number < 1)
            {
                error = "The layout file could not be read because it is not a saved layout.";
                return null;
            }

            if (number != IntakePlan.CurrentFormatVersion)
            {
                error = VersionMessage(number);
                return null;
            }

            var plan = document.RootElement.Deserialize<IntakePlan>(Options);
            if (plan is null || plan.Sha256 is null || plan.Containers is null)
            {
                error = "The layout file could not be read because it is not a saved layout.";
                return null;
            }

            return plan;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            error = "The layout file could not be read because it is not a saved layout.";
            return null;
        }
    }

    internal static string VersionMessage(int version)
        => version > IntakePlan.CurrentFormatVersion
            ? "This layout was saved by a newer version of the tool; update the tool to use it."
            : "The layout file could not be read because it is not a saved layout.";
}
