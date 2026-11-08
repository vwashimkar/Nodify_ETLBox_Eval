using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.ViewModels;

namespace EtlDesigner.UI.Services;

/// <summary>
/// Reads the labels file once at startup and writes the default file the first time a File intake
/// node is created in a session (only when no file exists). A missing, locked or broken labels file
/// never stops the app: the names fall back to their defaults.
/// </summary>
public static class LabelsStartup
{
    public static void Install(string path)
    {
        try
        {
            NodeFactory.Labels = Labels.Load(path);
        }
        catch (Exception ex)
        {
            // Labels.Load does not throw; this is only a guard so a labels file can never stop the app.
            NodeFactory.Labels = Labels.Empty;
            TryLog(ex);
        }

        var written = false;
        NodeFactory.FileIntakeCreated = () =>
        {
            if (!written)
            {
                written = true;
                Labels.WriteDefaultFile(path);
            }
        };
    }

    private static void TryLog(Exception ex)
    {
        try
        {
            ErrorLog.Write("Labels", ex);
        }
        catch (Exception)
        {
            // Nothing more can be done; the defaults are used.
        }
    }
}
