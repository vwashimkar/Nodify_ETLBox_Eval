using System.IO;

namespace EtlDesigner.Engine.Generation;

/// <summary>
/// Checks an output folder before a DLL is generated. The folder may not exist yet: the generator
/// creates it, so the checks that need a real folder use the nearest part of the path that exists.
/// </summary>
public static class OutputFolderCheck
{
    public const string EmptyMessage = "Choose an output folder.";
    public const string InvalidPathMessage = "This folder path contains characters Windows does not allow. Remove them or choose another folder.";
    public const string NoExistingParentMessage = "No part of this path exists, so the folder cannot be created. Check the drive or network location, or choose another folder.";
    public const string NotAFolderMessage = "Part of this path is a file, not a folder. Choose another folder.";
    public const string ProjectFolderMessage = "This folder is inside a project. The generated .cs files would be compiled into it. Choose a folder outside any project.";
    public const string NotWritableMessage = "This location cannot be written to. It may be read-only or need other permissions. Choose another folder.";

    /// <param name="isInsideProject">True when a folder is at or under a .csproj or .sln; <see cref="IsInsideProject"/> by default.</param>
    /// <returns>Why the folder cannot be used, or null when it can.</returns>
    public static string? Check(string? folder, Func<string, bool>? isInsideProject = null)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return EmptyMessage;
        }

        try
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(folder.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return InvalidPathMessage;
            }

            if (HasInvalidCharacters(fullPath))
            {
                return InvalidPathMessage;
            }

            var existing = NearestExisting(fullPath);
            if (existing is null)
            {
                return NoExistingParentMessage;
            }

            if (File.Exists(existing))
            {
                return NotAFolderMessage;
            }

            if ((isInsideProject ?? IsInsideProject)(fullPath))
            {
                return ProjectFolderMessage;
            }

            return CanWriteTo(existing) ? null : NotWritableMessage;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"The folder cannot be used. Choose another one. Details: {ex.Message}";
        }
    }

    /// <summary>True when a .csproj or .sln is at or above the folder, walking up from the nearest folder that exists.</summary>
    public static bool IsInsideProject(string folderPath)
    {
        var existing = NearestExisting(Path.GetFullPath(folderPath));
        var current = existing is null ? null : new DirectoryInfo(existing);

        while (current != null)
        {
            if (current.EnumerateFiles("*.csproj", SearchOption.TopDirectoryOnly).Any()
                || current.EnumerateFiles("*.sln", SearchOption.TopDirectoryOnly).Any())
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    /// <summary>The path itself or the nearest parent that exists (a folder or a file), or null when none does.</summary>
    private static string? NearestExisting(string fullPath)
    {
        for (var current = fullPath; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current) || File.Exists(current))
            {
                return current;
            }
        }

        return null;
    }

    private static bool HasInvalidCharacters(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        return fullPath[root.Length..]
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0);
    }

    /// <summary>The read-only attribute on a folder does not stop writes on Windows, so this creates a real file.</summary>
    private static bool CanWriteTo(string folder)
    {
        var probe = Path.Combine(folder, $".etl-write-check-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
