using EtlDesigner.Engine.Intake;

namespace EtlDesigner.UI.Services;

/// <summary>
/// The one lineage log of the app: <c>lineage.log</c> next to the program, or the fallback folder
/// (see <see cref="TextFileLineageLog.Note"/>). Made on first use, so a test that never opens
/// Intake Studio through the main window never creates it; tests pass their own log instead.
/// </summary>
public static class AppLineage
{
    private static readonly Lazy<TextFileLineageLog> Log = new(() => new TextFileLineageLog());

    public static TextFileLineageLog Shared => Log.Value;
}
