using System.Runtime.CompilerServices;
using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.Services;

namespace Nodify_ETLBox.Tests;

/// <summary>
/// Keeps the app's shared template store out of the test program's folder: a test that forgets to
/// pass its own store writes to a temp folder of this run, which is deleted when the test process ends.
/// </summary>
internal static class TestTemplateFolder
{
    public static string Folder { get; } = Path.Combine(Path.GetTempPath(), "EtlDesignerTests", "templates-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Configure()
    {
        Directory.CreateDirectory(Folder);
        AppTemplates.Configure(new TemplateSettings { Folder = Folder }, fallbackFolder: Folder);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(Folder, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        };
    }
}
