using System.IO;

namespace EtlDesigner.Engine.Project;

/// <summary>The two forms of a file path setting: absolute, and relative to the project file's folder.</summary>
public static class ProjectPaths
{
    /// <summary>
    /// The path relative to the folder of <paramref name="projectFilePath"/>, with forward
    /// slashes. Null when there is no project file yet, the path is not absolute, or it is on
    /// another drive.
    /// </summary>
    public static string? ToRelative(string path, string? projectFilePath)
    {
        var folder = FolderOf(projectFilePath);
        if (folder is null || string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return null;
        }

        var relative = Path.GetRelativePath(folder, path);
        return Path.IsPathRooted(relative) ? null : relative.Replace('\\', '/');
    }

    /// <summary>
    /// The absolute path when that file exists, else the relative path when that file exists, else
    /// the saved path as it is, so that the node's normal validation reports the missing file.
    /// </summary>
    public static string Resolve(string path, string? relativePath, string? projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(relativePath))
        {
            return path;
        }

        if (Path.IsPathRooted(path) && File.Exists(path))
        {
            return path;
        }

        var folder = FolderOf(projectFilePath);
        if (folder is null)
        {
            return path;
        }

        // A path the user typed without a folder is relative to the project too (spec section 4).
        foreach (var candidate in new[] { relativePath, Path.IsPathRooted(path) ? null : path })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                var full = Path.GetFullPath(Path.Combine(folder, candidate));
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return path;
    }

    private static string? FolderOf(string? projectFilePath)
        => string.IsNullOrWhiteSpace(projectFilePath) ? null : Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
}
