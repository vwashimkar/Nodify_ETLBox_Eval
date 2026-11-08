namespace EtlDesigner.Engine.Intake;

/// <summary>
/// The saved settings of a File intake node: the file, the mode (<see cref="AutoDetect"/> or
/// <see cref="MarkersMode"/>), the encoding name (<see cref="IntakeEncodings.Names"/>) and the size
/// limit in MB. A scan result is never part of them.
/// </summary>
public sealed record FileIntakeNodeSettings
{
    public const string AutoDetect = "autodetect";
    public const string MarkersMode = "markers";

    public static IReadOnlyList<string> Modes { get; } = [AutoDetect, MarkersMode];

    public static FileIntakeNodeSettings Default { get; } = new();

    public string Path { get; init; } = string.Empty;

    public string Mode { get; init; } = AutoDetect;

    public string Encoding { get; init; } = IntakeEncodings.Auto;

    public int MaxFileSizeMb { get; init; } = new IntakeSettings().MaxFileSizeMb;

    /// <summary>The reader settings for a scan. An unknown mode reads as Auto-detect.</summary>
    public IntakeSettings ToIntakeSettings() => new()
    {
        Mode = Mode == MarkersMode ? IntakeMode.Markers : IntakeMode.AutoDetect,
        Encoding = Encoding,
        MaxFileSizeMb = MaxFileSizeMb
    };
}
