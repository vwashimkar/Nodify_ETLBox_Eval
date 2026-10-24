namespace EtlDesigner.Engine.Generation;

public record DllGenerationResult(
    bool Success,
    string? DllPath,
    IReadOnlyList<string> SourcePaths,
    IReadOnlyList<string> Diagnostics,
    string? ErrorMessage);
