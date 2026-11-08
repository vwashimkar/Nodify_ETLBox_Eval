using System.IO;
using System.Text.Json.Serialization;

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
    GapText,
    Detected
}

/// <summary>Whether the first line of a table after its skip lines is a header. Always a guess.</summary>
public enum HeaderGuess
{
    Yes,
    No,
    Unknown
}

/// <summary>The line form of a key-value container (plan section 4, rule 5).</summary>
public enum KeyValueForm
{
    /// <summary><c>Name: value</c></summary>
    Colon,

    /// <summary><c>Name = value</c>, also with a leading <c>* </c></summary>
    EqualsSign,

    /// <summary><c>"Key","value"</c> rows whose field count may vary</summary>
    Quoted,

    /// <summary><c>Key&lt;TAB&gt;value</c></summary>
    Tab,

    /// <summary>Several <c>Key: value</c> pairs on one line, separated by commas</summary>
    Pairs,

    /// <summary><c>Key,value</c> rows with one field count: distinct labels in the first column and no header</summary>
    Delimited
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
        ["text", "json", "json-lines", "badgerfish-json", "xml", "csv", "tsv", "yaml", "soap", "fixedwidth"];

    public string BeginPrefix { get; init; } = "##### BEGIN ";

    public string BeginSuffix { get; init; } = " #####";

    public string EndLine { get; init; } = "##### END #####";

    public char FieldSeparator { get; init; } = '|';

    public IReadOnlyList<string> KnownFormats { get; init; } = DefaultKnownFormats;
}

/// <summary>
/// The thresholds of the auto-detector (plan section 4). Data, so they can come from configuration
/// later. Values marked [U] are chosen in G2, not given by the plan.
/// </summary>
public sealed record DetectorSettings
{
    public static readonly IReadOnlyList<string> DefaultDelimiters = [",", ";", "|", "\t"];

    /// <summary>A table has at least this many lines with the same field count ...</summary>
    public int MinTableLines { get; init; } = 3;

    /// <summary>... or at least this many lines ...</summary>
    public int MinShortTableLines { get; init; } = 2;

    /// <summary>... when they have at least this many fields.</summary>
    public int MinShortTableFields { get; init; } = 3;

    public IReadOnlyList<string> DelimiterCandidates { get; init; } = DefaultDelimiters;

    /// <summary>How far a field count may differ from the table's count; the table is then Likely.</summary>
    public int FieldCountTolerance { get; init; } = 1;

    /// <summary>Exactly equal field counts over this many lines make a table Sure.</summary>
    public int SureTableMinLines { get; init; } = 5;

    /// <summary>Title lines directly above a table that become its skip lines, at most.</summary>
    public int MaxPreambleLines { get; init; } = 3;

    public int MinKeyValueLines { get; init; } = 3;

    /// <summary>A key-value block of this many lines is Likely; a shorter one Guessed.</summary>
    public int LikelyKeyValueMinLines { get; init; } = 5;

    /// <summary>The longest key of a <c>Name: value</c> or <c>Name = value</c> line [U].</summary>
    public int MaxKeyLength { get; init; } = 60;

    /// <summary>This many blank lines or more split a text block; fewer stay inside it.</summary>
    public int TextSplitBlankLines { get; init; } = 2;

    /// <summary>A JSON or XML region larger than this is not parsed and stays Likely.</summary>
    public int MaxValidationMb { get; init; } = 16;

    /// <summary>A quoted field may span at most this many lines; beyond, the quote is taken as text [U].</summary>
    public int MaxQuotedLines { get; init; } = 10;

    /// <summary>A row shorter than its table's first row stays in the table (ragged right) when it has at least this many fields.</summary>
    public int MinRaggedFields { get; init; } = 2;

    /// <summary>
    /// A block with no header and distinct labels in its first column is key-value; the labels are
    /// checked for at most this many lines, so a longer block stays a table [U: chosen].
    /// </summary>
    public int MaxKeyValueLabelLines { get; init; } = 1000;

    /// <summary>A whitespace-aligned (fixedwidth) table has at least this many rows (blank lines end it) ...</summary>
    public int MinFixedWidthLines { get; init; } = 3;

    /// <summary>... with at least this many tokens per row.</summary>
    public int MinFixedWidthColumns { get; init; } = 4;

    /// <summary>A JSON block is the sub-type badgerfish (Guessed) when at least this many objects have '@' attribute keys or a '$' text key.</summary>
    public int BadgerFishMinHits { get; init; } = 2;

    /// <summary>The BadgerFish count looks at the first this many JSON tokens only [U].</summary>
    public long BadgerFishMaxTokens { get; init; } = 1_000_000;

    /// <summary>A line of one of these characters repeated is a section separator ...</summary>
    public string SeparatorCharacters { get; init; } = "=-#*~_";

    /// <summary>... when the character appears at least this many times (a name in the middle is allowed).</summary>
    public int MinSeparatorLength { get; init; } = 5;

    /// <summary>A table's first skip line names it when it is shorter than this.</summary>
    public int MaxTitleNameLength { get; init; } = 60;
}

public sealed record IntakeSettings
{
    public IntakeMode Mode { get; init; } = IntakeMode.AutoDetect;

    public DetectorSettings Detector { get; init; } = new();

    public ValidationSettings Validation { get; init; } = new();

    public MarkerProfile Markers { get; init; } = new();

    public int MaxFileSizeMb { get; init; } = 500;

    /// <summary>One of <see cref="IntakeEncodings.Names"/>: "auto" (the default), utf-8, windows-1252, iso-8859-1, utf-16le, utf-16be.</summary>
    public string Encoding { get; init; } = IntakeEncodings.Auto;
}

/// <summary>
/// One container of a file, the columns of the expected <c>*_segments.csv</c> files plus
/// <see cref="Confidence"/>. Line numbers are 1-based and count every line of the file. Byte
/// offsets count from the start of the file, including a BOM; <see cref="ContentEndByte"/> is just
/// after the last content character, before its line ending. An empty segment has no line
/// numbers and no offsets. The fields after <see cref="Confidence"/> are filled by the
/// auto-detector: <see cref="SubType"/> (for example soap), the table's <see cref="Delimiter"/>,
/// <see cref="HeaderGuess"/> and <see cref="SkipLines"/> (title lines at the top of the region),
/// the <see cref="KeyValueForm"/>, and <see cref="Reasons"/>, short texts that say why the format
/// was chosen. <see cref="ConfirmedByUser"/> is set by an edit in <see cref="IntakeEditSession"/>
/// and written to JSON only when true.
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
    ContainerConfidence Confidence,
    string? SubType = null,
    string? Delimiter = null,
    HeaderGuess? HeaderGuess = null,
    int SkipLines = 0,
    KeyValueForm? KeyValueForm = null,
    IReadOnlyList<string>? Reasons = null,
    string? SubTypeVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ConfirmedByUser = false);

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
/// <see cref="Containers"/> holds the containers finished so far. <see cref="EncodingUsed"/> is the
/// encoding the file was decoded with (set when the file was read to the end); everything that
/// decodes the file's bytes later uses it. <see cref="EncodingGuessed"/> is true when Auto chose
/// windows-1252 because the bytes are not valid UTF-8, and <see cref="EncodingReason"/> then says so
/// in words. <see cref="InvalidUtf8Seen"/> is true when the bytes have an invalid UTF-8 sequence.
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
    string? Error,
    string? EncodingUsed = null,
    bool EncodingGuessed = false,
    bool InvalidUtf8Seen = false,
    string? EncodingReason = null);

/// <summary>
/// Where a read is. The last report of a run equals the totals in the result. While validating,
/// <see cref="BytesRead"/> counts the container bytes checked so far and <see cref="ContainersValidated"/>
/// the containers done ("Validating n of m containers", m being <see cref="ContainersFound"/>).
/// </summary>
public sealed record IntakeProgress(long BytesRead, long FileSize, int ContainersFound, IntakePhase Phase, int ContainersValidated = 0);

/// <summary>Lines read back by offsets. <see cref="Error"/> says what went wrong and what to do; the list is then empty.</summary>
public sealed record IntakeLines(IReadOnlyList<string> Lines, string? Error);
