using System.Runtime.CompilerServices;
using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.Services;

namespace Nodify_ETLBox.Tests;

/// <summary>
/// Keeps the app's shared lineage log out of the test program's folder: a test that forgets to pass
/// its own log writes to a temp folder of this run, which is deleted when the test process ends.
/// </summary>
internal static class TestLineageFolder
{
    public static string Folder { get; } = Path.Combine(Path.GetTempPath(), "EtlDesignerTests", "lineage-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Configure()
    {
        Directory.CreateDirectory(Folder);
        AppLineage.Configure(new LineageSettings { Folder = Folder }, fallbackFolder: Folder);
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
