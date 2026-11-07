using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace EtlDesigner.Engine.Intake;

/// <summary>The limits of a labels file. Data, so they can come from configuration later. Values are chosen in G4a.</summary>
public sealed record LabelsSettings
{
    /// <summary>A longer value makes its line ignored.</summary>
    public int MaxValueLength { get; init; } = 80;

    /// <summary>A longer line is ignored.</summary>
    public int MaxLineLength { get; init; } = 200;

    /// <summary>A larger file is not read at all.</summary>
    public int MaxFileKb { get; init; } = 64;

    /// <summary>The names used when no labels file overrides them. Format names come from <see cref="ContainerDisplay.DefaultNames"/>.</summary>
    public static IReadOnlyDictionary<string, string> DefaultValues { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["node.fileintake.title"] = "File intake",
        ["node.fileintake.subtitle"] = "Reads one file and splits it into containers"
    }.Concat(ContainerDisplay.DefaultNames).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
}

/// <summary>
/// Names a user can change, read from a UTF-8 text file with one <c>key = value</c> per line. A
/// line starting with <c>#</c> is a comment; spaces around key and value are trimmed; the last of
/// two equal keys wins. Keys are lower-case letters, digits, dot, dash and underscore. A line that
/// is too long, has a bad key, no <c>=</c> or an empty value is ignored and counted in
/// <see cref="IgnoredLines"/>. Reading never throws: a missing, locked or too large file gives no
/// labels and a plain <see cref="LoadNote"/>.
/// </summary>
public sealed class Labels
{
    private static readonly Regex KeyPattern = new("^[a-z0-9._-]+$", RegexOptions.CultureInvariant);

    private readonly Dictionary<string, string> _values;

    public Labels(IReadOnlyDictionary<string, string>? values = null)
        => _values = values is null ? new(StringComparer.Ordinal) : new(values, StringComparer.Ordinal);

    public static Labels Empty { get; } = new();

    public int Count => _values.Count;

    public int IgnoredLines { get; private init; }

    /// <summary>Why the file was not read, in one sentence; null when it was read.</summary>
    public string? LoadNote { get; private init; }

    /// <summary>The label of a key, or null when there is none.</summary>
    public string? Find(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public string Get(string key, string fallback) => Find(key) ?? fallback;

    public static Labels Load(string path, LabelsSettings? settings = null)
    {
        settings ??= new LabelsSettings();
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new Labels { LoadNote = "There is no labels file, so the default names are used." };
            }

            if (info.Length > settings.MaxFileKb * 1024L)
            {
                return new Labels { LoadNote = $"The labels file is larger than {settings.MaxFileKb} KB, so it was not read and the default names are used." };
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return Parse(reader.ReadToEnd(), settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return new Labels { LoadNote = "The labels file could not be read. It may be open in another program or you may have no access to it. The default names are used." };
        }
    }

    /// <summary>
    /// Through the configuration store: the text stored under <paramref name="key"/>. The store gives
    /// no size and no reason for a failed read, so the size limit applies to the text instead.
    /// </summary>
    public static Labels Load(IConfigStore store, string key, LabelsSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        settings ??= new LabelsSettings();
        try
        {
            var text = store.ReadText(key);
            if (text is null)
            {
                return new Labels { LoadNote = "There is no labels file, so the default names are used." };
            }

            return Encoding.UTF8.GetByteCount(text) > settings.MaxFileKb * 1024L
                ? new Labels { LoadNote = $"The labels file is larger than {settings.MaxFileKb} KB, so it was not read and the default names are used." }
                : Parse(text, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Labels { LoadNote = "The labels file could not be read. The default names are used." };
        }
    }

    public static Labels Parse(string text, LabelsSettings? settings = null)
    {
        settings ??= new LabelsSettings();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var ignored = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').TrimStart('﻿');
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var at = trimmed.IndexOf('=');
            var key = at > 0 ? trimmed[..at].Trim() : string.Empty;
            var value = at > 0 ? trimmed[(at + 1)..].Trim() : string.Empty;
            if (line.Length > settings.MaxLineLength || !KeyPattern.IsMatch(key) || value.Length == 0 || value.Length > settings.MaxValueLength)
            {
                ignored++;
                continue;
            }

            values[key] = value;
        }

        return new Labels(values) { IgnoredLines = ignored };
    }

    /// <summary>
    /// Writes a commented file that lists every known key with its default, creating the folder.
    /// Never overwrites an existing file. True when the file was written; never throws.
    /// </summary>
    public static bool WriteDefaultFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
            writer.WriteLine("# Labels of the ETL designer. One 'key = value' per line; '#' starts a comment.");
            writer.WriteLine("# Remove the '#' in front of a line to change that name. Values up to 80 characters.");
            writer.WriteLine("# node.fileintake.*: the File intake node. container.format.<format>[.<subtype>]: the name of a container format.");
            writer.WriteLine();
            foreach (var (key, value) in LabelsSettings.DefaultValues.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteLine($"# {key} = {value}");
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>Where the labels file is.</summary>
public static class LabelsLocation
{
    public const string FileName = "labels.txt";

    /// <summary>%AppData%\EtlDesigner\labels.txt, or the same under <paramref name="baseFolder"/> (for tests).</summary>
    public static string DefaultPath(string? baseFolder = null)
        => Path.Combine(baseFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EtlDesigner", FileName);
}
