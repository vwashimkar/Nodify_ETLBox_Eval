namespace EtlDesigner.Engine.Intake;

public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// One finding of the validator. <see cref="Message"/> is one sentence a lab user can read: no
/// stack trace, no .NET exception name, no full path. <see cref="Line"/> and <see cref="Column"/>
/// are 1-based and count in the whole FILE, not in the container; <see cref="LineCount"/> is set
/// when the issue covers several lines.
/// </summary>
public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Code,
    string Message,
    long? Line,
    int? Column = null,
    long? LineCount = null);

/// <summary>
/// The checks of one container. <see cref="Status"/> is the worst issue (Info counts as Ok).
/// <see cref="Issues"/> holds at most <see cref="ValidationSettings.MaxIssuesPerContainer"/>;
/// <see cref="TotalIssues"/> counts all of them and <see cref="MoreNotShown"/> says some were left out.
/// Rows or lines next to each other with the same problem count as one issue. <see cref="Checked"/>
/// is false when the format has no checks yet (fixedwidth, an unknown format name).
/// </summary>
public sealed record ContainerValidation(
    int Order,
    ContainerStatus Status,
    IReadOnlyList<ValidationIssue> Issues,
    int TotalIssues,
    bool MoreNotShown,
    bool Checked = true);

/// <summary>
/// The outcome of one validation run over the containers of an <see cref="IntakeResult"/>.
/// <see cref="Error"/> says what went wrong for the whole run (a missing or locked file); the
/// containers are then those checked before it. After a cancel the containers checked so far are kept.
/// </summary>
public sealed record IntakeValidationResult(
    Guid RunId,
    Guid ItemId,
    IReadOnlyList<ContainerValidation> Containers,
    int Errors,
    int Warnings,
    int Infos,
    long ElapsedMs,
    bool Cancelled,
    string? Error);

/// <summary>The limits of the validator and of the YAML check. Data, so they can come from configuration later. Values marked [U] are chosen in G3.</summary>
public sealed record ValidationSettings
{
    public int MaxIssuesPerContainer { get; init; } = 20;

    /// <summary>A container larger than this is checked on its first part only.</summary>
    public int MaxValidationMb { get; init; } = 16;

    public int JsonMaxDepth { get; init; } = 64;

    /// <summary>The most characters an XML container may have before the check stops [U].</summary>
    public long XmlMaxCharacters { get; init; } = 32L * 1024 * 1024;

    /// <summary>A SOAP fault text is cut to this many characters in its message.</summary>
    public int MaxFaultLength { get; init; } = 200;

    /// <summary>YAML: the most nodes (scalars, mappings, sequences, aliases) read before the check stops [U].</summary>
    public int YamlMaxNodes { get; init; } = 100_000;

    /// <summary>YAML: the deepest nesting of mappings and sequences [U].</summary>
    public int YamlMaxDepth { get; init; } = 64;

    /// <summary>YAML: the most alias references (<c>*name</c>) [U]. Aliases are never expanded.</summary>
    public int YamlMaxAliases { get; init; } = 1_000;
}

/// <summary>Collects the issues of one container, keeping at most the cap and counting the rest.</summary>
internal sealed class IssueCollector(int cap)
{
    private readonly List<ValidationIssue> _shown = [];

    public int Total { get; private set; }

    public ValidationSeverity? Worst { get; private set; }

    public void Add(ValidationSeverity severity, string code, string message, long? line, int? column = null, long? lineCount = null)
    {
        Total++;
        BySeverity[(int)severity]++;
        Worst = Worst is null || severity > Worst ? severity : Worst;
        if (_shown.Count < cap)
        {
            _shown.Add(new ValidationIssue(severity, code, message, line, column, lineCount));
        }
    }

    /// <summary>Issues of each severity, shown or not, indexed by the severity's value.</summary>
    public int[] BySeverity { get; } = new int[3];

    public ContainerValidation ToValidation(int order, bool isChecked = true)
        => new(order, Worst switch { ValidationSeverity.Error => ContainerStatus.Error, ValidationSeverity.Warning => ContainerStatus.Warning, _ => ContainerStatus.Ok },
            _shown.ToList(), Total, Total > _shown.Count, isChecked);
}
