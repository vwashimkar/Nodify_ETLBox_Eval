using EtlDesigner.Engine.Intake;

namespace EtlDesigner.UI.Services;

/// <summary>
/// The one lineage log of the app: <c>lineage.log</c> next to the program, or the fallback folder
/// (see <see cref="TextFileLineageLog.Note"/>). Made on first use, so a test that never opens
/// Intake Studio through the main window never creates it; tests pass their own log instead.
/// </summary>
public static class AppLineage
{
    private static LineageSettings? _settings;
    private static string? _fallbackFolder;

    private static readonly Lazy<TextFileLineageLog> Log = new(() => new TextFileLineageLog(_settings, _fallbackFolder));

    public static TextFileLineageLog Shared => Log.Value;

    /// <summary>
    /// Sets where the shared log is written. Call it once, before first use; the app never calls it,
    /// so the log stays next to the program. The test project uses it to keep the log in a temp folder.
    /// </summary>
    /// <exception cref="InvalidOperationException">The shared log is already in use.</exception>
    public static void Configure(LineageSettings settings, string? fallbackFolder = null)
    {
        if (Log.IsValueCreated)
            throw new InvalidOperationException("The shared lineage log is already in use; configure it before first use.");
        _settings = settings;
        _fallbackFolder = fallbackFolder;
    }
}
