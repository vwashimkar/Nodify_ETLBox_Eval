using System.Runtime.InteropServices;
using System.Windows;

namespace EtlDesigner.UI.Services;

/// <summary>Puts text on the Windows clipboard. False when another program holds the clipboard.</summary>
public static class TextClipboard
{
    public static bool TrySetText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}
