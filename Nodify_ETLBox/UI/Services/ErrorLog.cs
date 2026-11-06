using System.IO;
using System.Text;

namespace EtlDesigner.UI.Services;

/// <summary>Writes an exception to unhandled-exception.log next to the program, so that an error is never lost.</summary>
public static class ErrorLog
{
    public static void Write(string source, Exception ex)
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "unhandled-exception.log");
        var text = new StringBuilder()
            .AppendLine($"[{DateTime.UtcNow:O}] {source}")
            .AppendLine($"Type: {ex.GetType().FullName}")
            .AppendLine($"Message: {ex.Message}")
            .AppendLine("StackTrace:")
            .AppendLine(ex.ToString())
            .AppendLine(new string('-', 80))
            .ToString();

        File.AppendAllText(logPath, text);
    }
}
