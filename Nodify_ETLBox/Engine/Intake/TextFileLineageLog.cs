using System.Collections;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace EtlDesigner.Engine.Intake;

/// <summary>Where and how big the lineage file is. Data, so it can come from configuration later.</summary>
public sealed record LineageSettings
{
    /// <summary>The folder of <c>lineage.log</c>; null means the folder of the running program.</summary>
    public string? Folder { get; init; }

    /// <summary>When the file is larger than this before an append, it becomes <c>lineage.1.log</c> and a new file starts.</summary>
    public double MaxMb { get; init; } = 10;

    /// <summary>When no file can be written, at most this many events are kept in memory.</summary>
    public int MaxMemoryEvents { get; init; } = 1000;

    /// <summary>Texts in an event (names the user typed, reasons) are cut to this many characters.</summary>
    public int MaxTextLength { get; init; } = 80;
}

/// <summary>Where the lineage file goes.</summary>
public static class LineageLocation
{
    public const string FileName = "lineage.log";

    public const string RotatedFileName = "lineage.1.log";

    /// <summary><c>lineage.log</c> in <paramref name="baseFolder"/>, or in the folder of the running program.</summary>
    public static string DefaultPath(string? baseFolder = null) => Path.Combine(baseFolder ?? AppContext.BaseDirectory, FileName);

    /// <summary>The folder used when the program folder cannot be written: <c>%AppData%\EtlDesigner</c>.</summary>
    public static string FallbackFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EtlDesigner");
}

/// <summary>
/// <see cref="ILineageLog"/> as a text file of JSON lines (one event per line, UTF-8 without BOM,
/// <c>\n</c> line ends), append only. Each event is one write call to a file opened with
/// <c>FileShare.ReadWrite</c>, so another process can append too and a killed program never leaves
/// half a line. When the program folder cannot be written the file goes to
/// <see cref="LineageLocation.FallbackFolder"/> and <see cref="Note"/> says so; when nothing can be
/// written the last events are kept in memory. It never throws. Safe for several threads.
/// Lines hold a file name only, never a folder, user or machine name, and no file content.
/// </summary>
public sealed class TextFileLineageLog : ILineageLog
{
    private static readonly JsonSerializerOptions DetailOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly object _lock = new();
    private readonly LineageSettings _settings;
    private readonly string _fallbackFolder;
    private readonly Queue<string> _memory = new();
    private string? _path;
    private bool _resolved;

    /// <param name="fallbackFolder">The folder used when the settings' folder cannot be written; null means <see cref="LineageLocation.FallbackFolder"/>.</param>
    public TextFileLineageLog(LineageSettings? settings = null, string? fallbackFolder = null)
    {
        _settings = settings ?? new LineageSettings();
        _fallbackFolder = fallbackFolder ?? LineageLocation.FallbackFolder();
    }

    /// <summary>The file written to, or null before the first event and when events are kept in memory only.</summary>
    public string? FilePath
    {
        get
        {
            lock (_lock)
            {
                return _path;
            }
        }
    }

    /// <summary>One plain sentence when the file is not in the program folder or not written at all; else null.</summary>
    public string? Note { get; private set; }

    /// <summary>The events kept in memory when no file could be written, as their JSON lines.</summary>
    public IReadOnlyList<string> MemoryLines
    {
        get
        {
            lock (_lock)
            {
                return [.. _memory];
            }
        }
    }

    public void Record(IntakeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Write(new LineageEvent("intake.scanned", result.ItemId.ToString(), result.File?.Name, result.File?.Sha256, new Dictionary<string, object?>
        {
            ["size"] = result.File?.Size,
            ["encoding"] = result.EncodingUsed,
            ["guessed"] = result.EncodingGuessed,
            ["containers"] = result.Containers.Count,
            ["warnings"] = result.Warnings.Count
        }));
    }

    public void Write(LineageEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string line;
        try
        {
            line = Line(entry);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            line = Line(entry with { Details = new Dictionary<string, object?> { ["note"] = "The details could not be written." } });
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
        lock (_lock)
        {
            if (!_resolved)
            {
                Resolve();
            }

            if (_path is not null && !Append(_path, bytes))
            {
                // The folder stopped being writable: try the fallback once, then memory.
                var fallback = Path.Combine(_fallbackFolder, LineageLocation.FileName);
                if (_path != fallback && Append(fallback, bytes))
                {
                    _path = fallback;
                    Note = FallbackNote();
                    return;
                }

                _path = null;
                Note = MemoryNote();
            }

            if (_path is null)
            {
                _memory.Enqueue(line.TrimEnd('\n'));
                while (_memory.Count > Math.Max(1, _settings.MaxMemoryEvents))
                {
                    _memory.Dequeue();
                }
            }
        }
    }

    /// <summary>Picks the first folder that can be written: the settings' folder (or the program folder), then the fallback.</summary>
    private void Resolve()
    {
        _resolved = true;
        var main = LineageLocation.DefaultPath(_settings.Folder);
        if (CanWrite(main))
        {
            _path = main;
            return;
        }

        var fallback = Path.Combine(_fallbackFolder, LineageLocation.FileName);
        if (CanWrite(fallback))
        {
            _path = fallback;
            Note = FallbackNote();
            return;
        }

        Note = MemoryNote();
    }

    private string FallbackNote()
        => $"The lineage file is kept in {ShortName(_fallbackFolder)} because the program folder is not writable.";

    private string MemoryNote()
        => $"The lineage file could not be written anywhere, so only the last {IntakeText.Count(_settings.MaxMemoryEvents, "event")} are kept in memory.";

    /// <summary>"%AppData%\EtlDesigner" for the usual folder, else the folder's own name: never a full path.</summary>
    private string ShortName(string folder)
        => string.Equals(Path.GetFullPath(folder), Path.GetFullPath(LineageLocation.FallbackFolder()), StringComparison.OrdinalIgnoreCase)
            ? @"%AppData%\EtlDesigner"
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));

    private static bool CanWrite(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private bool Append(string path, byte[] bytes)
    {
        try
        {
            Rotate(path);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            // One write call, so the line is never written in part.
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private void Rotate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > _settings.MaxMb * 1024 * 1024)
            {
                File.Move(path, Path.Combine(info.DirectoryName!, LineageLocation.RotatedFileName), overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another process may hold the file; keep appending and rotate next time.
        }
    }

    private string Line(LineageEvent entry)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("id", Guid.NewGuid().ToString());
            writer.WriteString("utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("event", entry.Event);
            writer.WriteString("intakeId", entry.IntakeId);
            writer.WriteString("file", entry.File is null ? null : Path.GetFileName(entry.File));
            writer.WriteString("sha256", entry.Sha256);
            writer.WritePropertyName("details");
            JsonSerializer.Serialize(writer, Clean(entry.Details), DetailOptions);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>The details with every text cut to <see cref="LineageSettings.MaxTextLength"/> characters.</summary>
    private object? Clean(object? value) => value switch
    {
        null => null,
        string text => text.Length <= _settings.MaxTextLength ? text : text[.._settings.MaxTextLength],
        IEnumerable<KeyValuePair<string, object?>> pairs => pairs.ToDictionary(p => p.Key, p => Clean(p.Value)),
        IEnumerable items => items.Cast<object?>().Select(Clean).ToList(),
        _ => value
    };
}
