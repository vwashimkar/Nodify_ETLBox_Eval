using System.IO;

namespace EtlDesigner.UI;

// TEMPORARY (Task C2b): traces the palette drag-and-drop to %TEMP%\palette-trace.log.
// Remove in phase 2. Never throws.
public static class PaletteTrace
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "palette-trace.log");
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} t{Environment.CurrentManagedThreadId} {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Tracing must never change behaviour.
        }
    }

    public static string Describe(object? sender, System.Windows.RoutedEventArgs e)
        => $"sender={Name(sender)} source={e.OriginalSource?.GetType().Name ?? "null"} handled={e.Handled}";

    public static string Name(object? o) => o switch
    {
        null => "null",
        System.Windows.FrameworkElement f when !string.IsNullOrEmpty(f.Name) => $"{f.GetType().Name}#{f.Name}",
        System.Windows.Controls.Button { Tag: string tag } b => $"{b.GetType().Name}[{tag}]",
        _ => o.GetType().Name
    };

    public static string Stack()
    {
        try
        {
            return string.Join(" | ", Environment.StackTrace.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(12));
        }
        catch
        {
            return "(no stack)";
        }
    }
}
