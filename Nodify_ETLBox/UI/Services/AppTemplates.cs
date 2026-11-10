using EtlDesigner.Engine.Intake;

namespace EtlDesigner.UI.Services;

/// <summary>
/// The one template store of the app: the <c>templates</c> folder next to the program, or the
/// fallback folder (see <see cref="FileTemplateStore.Note"/>). Made on first use, so a test that
/// never opens Intake Studio through the main window never creates it; tests pass their own store.
/// </summary>
public static class AppTemplates
{
    private static TemplateSettings? _settings;
    private static string? _fallbackFolder;

    private static readonly Lazy<FileTemplateStore> Store = new(() => new FileTemplateStore(_settings, _fallbackFolder));

    public static FileTemplateStore Shared => Store.Value;

    /// <summary>The settings the shared store was configured with (the defaults when never configured).</summary>
    public static TemplateSettings Settings => _settings ?? new TemplateSettings();

    /// <summary>
    /// Sets where the shared store keeps its files. Call it once, before first use; the app never calls
    /// it, so the templates stay next to the program. The test project uses it to keep them in a temp folder.
    /// </summary>
    /// <exception cref="InvalidOperationException">The shared store is already in use.</exception>
    public static void Configure(TemplateSettings settings, string? fallbackFolder = null)
    {
        if (Store.IsValueCreated)
            throw new InvalidOperationException("The shared template store is already in use; configure it before first use.");
        _settings = settings;
        _fallbackFolder = fallbackFolder;
    }
}
