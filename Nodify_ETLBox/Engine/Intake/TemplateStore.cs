using System.IO;
using System.Text;

namespace EtlDesigner.Engine.Intake;

/// <summary>The outcome of a store operation. <see cref="Message"/> is one plain sentence; on failure nothing has changed.</summary>
public sealed record TemplateStoreResult(bool Ok, string Message)
{
    public static TemplateStoreResult Done { get; } = new(true, string.Empty);

    public static TemplateStoreResult Fail(string message) => new(false, message);
}

/// <summary>Keeps layout templates (plan section 6). An implementation never throws for a bad file or folder.</summary>
public interface ITemplateStore
{
    /// <summary>The templates that could be read, by name.</summary>
    IReadOnlyList<IntakeTemplate> List();

    IntakeTemplate? Get(Guid id);

    /// <summary>Adds the template, or replaces the one with the same id.</summary>
    TemplateStoreResult Save(IntakeTemplate template);

    TemplateStoreResult Delete(Guid id);

    TemplateStoreResult Rename(Guid id, string name);

    /// <summary>One plain sentence per file that was not read.</summary>
    IReadOnlyList<string> Skipped { get; }

    /// <summary>Set when the templates are not kept in the usual folder (or only in memory).</summary>
    string? Note { get; }
}

/// <summary>
/// <see cref="ITemplateStore"/> as one JSON file per template, named <c>&lt;id&gt;.json</c>, in
/// <see cref="TemplateSettings.Folder"/> (or <see cref="TemplateLocation.DefaultFolder"/>). When
/// that folder cannot be written the fallback folder is used and <see cref="Note"/> says so; when
/// nothing can be written the templates are kept in memory for the session. A damaged, oversized
/// or newer file is skipped and listed in <see cref="Skipped"/>; the others still load. Writes are
/// atomic: a temporary file in the same folder, then a replace. Safe for several threads.
/// </summary>
public sealed class FileTemplateStore : ITemplateStore
{
    private const string TempExtension = ".tmp";

    private readonly object _lock = new();
    private readonly TemplateSettings _settings;
    private readonly string _fallbackFolder;
    private readonly Dictionary<Guid, IntakeTemplate> _templates = [];
    private readonly List<string> _skipped = [];
    private string? _folder;
    private bool _loaded;

    /// <param name="fallbackFolder">The folder used when the settings' folder cannot be written; null means <see cref="TemplateLocation.FallbackFolder"/>.</param>
    public FileTemplateStore(TemplateSettings? settings = null, string? fallbackFolder = null)
    {
        _settings = settings ?? new TemplateSettings();
        _fallbackFolder = fallbackFolder ?? TemplateLocation.FallbackFolder();
    }

    /// <summary>The folder in use, or null when the templates are kept in memory only.</summary>
    public string? Folder
    {
        get
        {
            lock (_lock)
            {
                Load();
                return _folder;
            }
        }
    }

    public string? Note { get; private set; }

    public IReadOnlyList<string> Skipped
    {
        get
        {
            lock (_lock)
            {
                Load();
                return [.. _skipped];
            }
        }
    }

    public IReadOnlyList<IntakeTemplate> List()
    {
        lock (_lock)
        {
            Load();
            return _templates.Values.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.CreatedUtc).ToList();
        }
    }

    public IntakeTemplate? Get(Guid id)
    {
        lock (_lock)
        {
            Load();
            return _templates.GetValueOrDefault(id);
        }
    }

    public TemplateStoreResult Save(IntakeTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (IntakeEditRules.NameProblem(template.Name) is { } problem)
        {
            return TemplateStoreResult.Fail(problem);
        }

        var json = IntakeTemplateJson.Serialize(template with { Name = template.Name.Trim() });
        if (Encoding.UTF8.GetByteCount(json) > _settings.MaxFileKb * 1024L)
        {
            return TemplateStoreResult.Fail($"This layout is too large to save as a template (more than {_settings.MaxFileKb} KB).");
        }

        lock (_lock)
        {
            Load();
            if (!_templates.ContainsKey(template.Id) && _templates.Count >= _settings.MaxTemplates)
            {
                return TemplateStoreResult.Fail($"There are already {_settings.MaxTemplates} saved layouts; delete one before saving another.");
            }

            if (_folder is not null && !Write(Path.Combine(_folder, FileName(template.Id)), json))
            {
                return TemplateStoreResult.Fail("The layout could not be saved; the file may be open in another program. Close it and try again.");
            }

            _templates[template.Id] = IntakeTemplateJson.Deserialize(json, out _)!;
            return TemplateStoreResult.Done;
        }
    }

    public TemplateStoreResult Delete(Guid id)
    {
        lock (_lock)
        {
            Load();
            if (!_templates.ContainsKey(id))
            {
                return TemplateStoreResult.Fail("This saved layout no longer exists.");
            }

            if (_folder is not null)
            {
                try
                {
                    File.Delete(Path.Combine(_folder, FileName(id)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return TemplateStoreResult.Fail("The layout could not be deleted; the file may be open in another program. Close it and try again.");
                }
            }

            _templates.Remove(id);
            return TemplateStoreResult.Done;
        }
    }

    public TemplateStoreResult Rename(Guid id, string name)
    {
        if (IntakeEditRules.NameProblem(name) is { } problem)
        {
            return TemplateStoreResult.Fail(problem);
        }

        lock (_lock)
        {
            return Get(id) is { } template ? Save(template with { Name = name.Trim() }) : TemplateStoreResult.Fail("This saved layout no longer exists.");
        }
    }

    private static string FileName(Guid id) => id.ToString("D") + ".json";

    /// <summary>Picks the folder (once) and reads every template file in it.</summary>
    private void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        var folder = _settings.Folder ?? TemplateLocation.DefaultFolder();
        if (CanWrite(folder))
        {
            _folder = folder;
        }
        else if (CanWrite(_fallbackFolder))
        {
            _folder = _fallbackFolder;
            Note = $"Saved layouts are kept in {Path.GetFileName(Path.GetDirectoryName(_fallbackFolder.TrimEnd('\\', '/')))}\\{TemplateLocation.FolderName} because the program folder is not writable.";
        }
        else
        {
            Note = "Saved layouts cannot be written to disk, so they are kept only until the program closes.";
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(_folder, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            try
            {
                if (new FileInfo(file).Length > _settings.MaxFileKb * 1024L)
                {
                    _skipped.Add($"The saved layout '{name}' is larger than {_settings.MaxFileKb} KB, so it was not read.");
                    continue;
                }

                var template = IntakeTemplateJson.Deserialize(File.ReadAllText(file), out var error);
                if (template is null)
                {
                    _skipped.Add(error == IntakeTemplateJson.UnreadableMessage
                        ? $"The saved layout '{name}' could not be read."
                        : $"The saved layout '{name}' was made by a newer version of the tool.");
                    continue;
                }

                if (_templates.Count >= _settings.MaxTemplates)
                {
                    _skipped.Add($"The saved layout '{name}' was not read because there are more than {_settings.MaxTemplates} saved layouts.");
                    continue;
                }

                _templates[template.Id] = template;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _skipped.Add($"The saved layout '{name}' could not be read.");
            }
        }
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, Guid.NewGuid().ToString("N") + TempExtension);
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Writes a temporary file in the same folder, then replaces the target with it; on failure the temporary file is removed and the target is unchanged.</summary>
    private static bool Write(string path, string json)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + TempExtension;
        try
        {
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The temporary file stays; it ends in .tmp and is never read as a template.
            }

            return false;
        }
    }
}
