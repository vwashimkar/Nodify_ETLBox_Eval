using System.IO;

namespace EtlDesigner.Engine.Intake;

public enum IntakeMode
{
    AutoDetect,
    Markers,
    MarkersThenAutoDetect
}

public enum ContainerKind
{
    Marker,
    GapText
}

public enum ContainerStatus
{
    Ok,
    Warning,
    Error
}

/// <summary>How sure the tool is about a container (plan section 4). Markers are always Sure.</summary>
public enum ContainerConfidence
{
    Sure,
    Likely,
    Guessed
}

public enum LineEndingKind
{
    None,
    Lf,
    CrLf,
    Cr,
    Mixed
}

public enum IntakePhase
{
    Reading,
    Detecting,
    Validating,
    Done
}

/// <summary>
/// One input from any source (plan section 9). All later code reads the original through
/// <see cref="OpenRead"/> and never writes to it. <see cref="OpenRead"/> gives a read-only stream
/// that the caller disposes; it may throw when the source cannot be opened.
/// </summary>
public sealed record IntakeItem(
    Guid Id,
    string SourceKind,
    string Name,
    long Size,
    DateTime ReceivedUtc,
    Func<Stream> OpenRead);

/// <summary>The marker lines of plan section 5. These are data, so another instrument can have its own profile.</summary>
public sealed record MarkerProfile
{
    public static readonly IReadOnlyList<string> DefaultKnownFormats =
        ["text", "json", "json-lines", "badgerfish-json", "xml", "csv", "tsv", "yaml", "soap"];

    public string BeginPrefix { get; init; } = "##### BEGIN ";

    public string BeginSuffix { get; init; } = " #####";

    public string EndLine { get; init; } = "##### END #####";

    public char FieldSeparator { get; init; } = '|';

    public IReadOnlyList<string> KnownFormats { get; init; } = DefaultKnownFormats;
}

public sealed record IntakeSettings
{
    public IntakeMode Mode { get; init; } = IntakeMode.AutoDetect;

    public MarkerProfile Markers { get; init; } = new();

    public int MaxFileSizeMb { get; init; } = 500;

    public string EncodingName { get; init; } = "utf-8";
}

/// <summary>
/// One container of a file, the columns of the expected <c>*_segments.csv</c> files plus
/// <see cref="Confidence"/>. Line numbers are 1-based and count every line of the file. Byte
/// offsets count from the start of the file, including a BOM; <see cref="ContentEndByte"/> is just
/// after the last content character, before its line ending. An empty segment has no line
/// numbers and no offsets.
/// </summary>
public sealed record ContainerInfo(
    int Order,
    ContainerKind Kind,
    string Format,
    string Name,
    string Notes,
    long? MarkerBeginLine,
    long? MarkerEndLine,
    long? ContentFirstLine,
    long? ContentLastLine,
    long ContentLineCount,
    long? ContentStartByte,
    long? ContentEndByte,
    ContainerStatus Status,
    string Message,
    ContainerConfidence Confidence);

public sealed record IntakeFileInfo(
    string Name,
    long Size,
    string Sha256,
    string Encoding,
    bool HasBom,
    LineEndingKind LineEnding,
    long LineCount);

/// <summary>A warning about the file, not about one container. <see cref="Line"/> is null when it is about no single line.</summary>
public sealed record IntakeWarning(long? Line, string Message);

/// <summary>
/// The outcome of one intake run. <see cref="ItemId"/> ties the run to its <see cref="IntakeItem"/>
/// for lineage. <see cref="File"/> is null when the file was not read to the end. After a cancel
/// <see cref="Containers"/> holds the containers finished so far.
/// </summary>
public sealed record IntakeResult(
    Guid RunId,
    Guid ItemId,
    DateTime StartedUtc,
    DateTime FinishedUtc,
    string ToolVersion,
    IntakeFileInfo? File,
    IReadOnlyList<ContainerInfo> Containers,
    IReadOnlyList<IntakeWarning> Warnings,
    long ElapsedMs,
    bool Cancelled,
    string? Error);

/// <summary>Where a read is. The last report of a run equals the totals in the result.</summary>
public sealed record IntakeProgress(long BytesRead, long FileSize, int ContainersFound, IntakePhase Phase);

/// <summary>Lines read back by offsets. <see cref="Error"/> says what went wrong and what to do; the list is then empty.</summary>
public sealed record IntakeLines(IReadOnlyList<string> Lines, string? Error);
