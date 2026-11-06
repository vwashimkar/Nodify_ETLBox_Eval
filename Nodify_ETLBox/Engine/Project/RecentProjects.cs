using System.IO;
using System.Text.Json;

namespace EtlDesigner.Engine.Project;

/// <summary>The most recently used project files, newest first.</summary>
public interface IRecentProjects
{
    IReadOnlyList<string> Load();

    /// <summary>Puts the path on top. The list keeps <see cref="JsonRecentProjects.MaxEntries"/> entries.</summary>
    void Add(string path);

    void Remove(string path);
}

/// <summary>The recent list as a small JSON file, by default %AppData%\EtlDesigner\recent.json.</summary>
public sealed class JsonRecentProjects : IRecentProjects
{
    public const int MaxEntries = 5;

    private readonly string _file;

    public JsonRecentProjects(string? file = null)
    {
        _file = file ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EtlDesigner", "recent.json");
    }

    /// <summary>A missing or damaged list is an empty list: it only saves the user some clicks.</summary>
    public IReadOnlyList<string> Load()
    {
        try
        {
            return File.Exists(_file)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public void Add(string path)
        => Write(Load().Where(p => !SamePath(p, path)).Prepend(path).Take(MaxEntries).ToList());

    public void Remove(string path) => Write(Load().Where(p => !SamePath(p, path)).ToList());

    private void Write(List<string> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(entries, ProjectSerializer.Options));
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
