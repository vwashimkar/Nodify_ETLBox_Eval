using System.IO;
using System.Text;

namespace EtlDesigner.Engine.Project;

/// <summary>The file steps of a safe save, so that a test can make one fail.</summary>
public interface IProjectFileSteps
{
    /// <summary>Writes the text and flushes it to the disk.</summary>
    void WriteAndFlush(string path, string text);

    /// <summary>Puts the new file in the place of the old one and keeps the old one as the backup.</summary>
    void Replace(string newFile, string target, string backup);

    /// <summary>Puts the new file at the target, where no file exists yet.</summary>
    void Move(string newFile, string target);
}

/// <summary>Reads and safely writes project files.</summary>
public static class ProjectFileStore
{
    public const string BackupExtension = ".bak";

    /// <summary>The text of a project file. Throws <see cref="ProjectLoadException"/> with the reason.</summary>
    public static string Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectLoadException($"The project file could not be read: {ex.Message} Check that the file exists and that you may read it.", ex);
        }
    }

    /// <summary>
    /// Writes to a temp file in the same folder, flushes it, then replaces the target and keeps
    /// the previous version as "&lt;name&gt;.etlproj.bak". Never writes the target in place: when
    /// a step fails the old file is untouched and the exception goes to the caller.
    /// </summary>
    public static void Save(string path, string text, IProjectFileSteps? steps = null)
    {
        steps ??= new DiskSteps();
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            steps.WriteAndFlush(temp, text);
            if (File.Exists(path))
            {
                steps.Replace(temp, path, path + BackupExtension);
            }
            else
            {
                steps.Move(temp, path);
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file does no harm, and the save's own result matters more.
        }
    }

    private sealed class DiskSteps : IProjectFileSteps
    {
        public void WriteAndFlush(string path, string text)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(text);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        public void Replace(string newFile, string target, string backup)
            => File.Replace(newFile, target, backup, ignoreMetadataErrors: true);

        public void Move(string newFile, string target) => File.Move(newFile, target);
    }
}
