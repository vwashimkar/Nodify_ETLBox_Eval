using System.Diagnostics;
using System.IO;
using System.Text;

namespace EtlDesigner.Engine.Intake;

/// <summary>
/// The entry point of the intake: reads an <see cref="IntakeItem"/> into an <see cref="IntakeResult"/>
/// and reads lines back by offsets. Problems come back as a message in the result, not as an exception.
/// </summary>
public static class IntakeReader
{
    public const string AutoDetectPendingMessage = "Auto-detection comes in the next stage. Use Markers mode for now.";

    public const string Utf16SettingMessage = "UTF-16 and UTF-32 cannot be chosen as the encoding: a UTF-16 file is read when it starts with a byte order mark (BOM), which decides. Choose utf-8 or a one-byte encoding such as windows-1252.";

    private static readonly string ToolVersion = typeof(IntakeResult).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    /// <summary>Runs <see cref="Read"/> off the calling thread.</summary>
    public static Task<IntakeResult> ReadAsync(IntakeItem item, IntakeSettings settings, Action<IntakeProgress>? progress, CancellationToken token)
        => Task.Run(() => Read(item, settings, progress, token));

    public static IntakeResult Read(IntakeItem item, IntakeSettings settings, Action<IntakeProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);

        var startedUtc = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();
        var warnings = new List<IntakeWarning>();

        IntakeResult Finish(IntakeFileInfo? file, IReadOnlyList<ContainerInfo> containers, bool cancelled, string? error)
            => new(Guid.NewGuid(), item.Id, startedUtc, DateTime.UtcNow, ToolVersion, file, containers, warnings, clock.ElapsedMilliseconds, cancelled, error);

        if (token.IsCancellationRequested)
        {
            return Finish(null, [], cancelled: true, null);
        }

        var encoding = ResolveEncoding(settings.EncodingName, out var encodingError);
        if (encoding is null)
        {
            return Finish(null, [], cancelled: false, encodingError);
        }

        MarkerScanner? scanner = null;
        IntakeLineHandler onLine = NoLine;
        if (settings.Mode != IntakeMode.AutoDetect)
        {
            scanner = new MarkerScanner(settings.Markers, encoding);
            onLine = scanner.OnLine;
        }

        var outcome = LineIndexer.Scan(item, settings.MaxFileSizeMb, settings.EncodingName, onLine, () => scanner?.ContainerCount ?? 0, progress, token);

        if (outcome.File is not null && outcome.Error is null)
        {
            scanner?.Finish();
        }

        if (outcome.Error is not null)
        {
            return Finish(outcome.File, [], cancelled: false, outcome.Error);
        }

        if (settings.Mode != IntakeMode.Markers && !outcome.Cancelled)
        {
            warnings.Add(new IntakeWarning(null, AutoDetectPendingMessage));
        }

        if (scanner is not null)
        {
            warnings.AddRange(scanner.Warnings);
        }

        IReadOnlyList<ContainerInfo> containers = scanner is null ? [] : scanner.Containers;
        progress?.Invoke(new IntakeProgress(outcome.BytesRead, outcome.Size, containers.Count, IntakePhase.Done));
        return Finish(outcome.File, containers, outcome.Cancelled, null);
    }

    /// <summary>
    /// Returns the lines between two byte offsets, at most <paramref name="maxLines"/>, exactly as
    /// they are in the file and without their line endings. <paramref name="lastByte"/> is the end
    /// of the last line, as in <see cref="ContainerInfo.ContentEndByte"/>, so a range that ends just
    /// after a line ending has a blank last line. For a file with a BOM pass the encoding of
    /// <see cref="IntakeFileInfo.Encoding"/>.
    /// </summary>
    public static IntakeLines ReadLines(IntakeItem item, long firstByte, long lastByte, int maxLines, string encodingName = "utf-8")
    {
        ArgumentNullException.ThrowIfNull(item);

        if (firstByte < 0 || lastByte < firstByte)
        {
            return new IntakeLines([], $"The byte range {firstByte} to {lastByte} is not valid. Read the file again and use the offsets of that result.");
        }

        var encoding = ResolveEncoding(encodingName, out var encodingError, allowUtf16: true);
        if (encoding is null)
        {
            return new IntakeLines([], encodingError);
        }

        var lines = new List<string>();
        if (maxLines <= 0)
        {
            return new IntakeLines(lines, null);
        }

        try
        {
            using var stream = item.OpenRead();
            MoveTo(stream, firstByte);

            // Decoded first and split on characters, so a two-byte encoding splits the same way.
            var buffer = new byte[LineIndexer.BlockSize];
            var decoder = encoding.GetDecoder();
            var chars = new char[encoding.GetMaxCharCount(buffer.Length)];
            var current = new StringBuilder();
            var remaining = lastByte - firstByte;
            var afterCr = false;

            while (remaining > 0 && lines.Count < maxLines)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                remaining -= read;
                var count = decoder.GetChars(buffer, 0, read, chars, 0, flush: read == 0 || remaining == 0);
                for (var i = 0; i < count && lines.Count < maxLines; i++)
                {
                    var c = chars[i];
                    if (afterCr)
                    {
                        afterCr = false;
                        if (c == '\n')
                        {
                            continue;
                        }
                    }

                    if (c is '\n' or '\r')
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                        afterCr = c == '\r';
                        continue;
                    }

                    current.Append(c);
                }

                if (read == 0)
                {
                    break;
                }
            }

            if (lines.Count < maxLines)
            {
                lines.Add(current.ToString());
            }

            return new IntakeLines(lines, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new IntakeLines([], LineIndexer.NotFoundMessage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new IntakeLines([], LineIndexer.UnreadableMessage);
        }
    }

    private static void MoveTo(Stream stream, long position)
    {
        if (stream.CanSeek)
        {
            stream.Seek(position, SeekOrigin.Begin);
            return;
        }

        var skip = new byte[LineIndexer.BlockSize];
        while (position > 0)
        {
            var read = stream.Read(skip, 0, (int)Math.Min(skip.Length, position));
            if (read == 0)
            {
                return;
            }

            position -= read;
        }
    }

    /// <summary>
    /// The scan finds line ends on bytes, so a setting must keep ASCII as single bytes. UTF-16 is
    /// read only when the file has a BOM, which decides; reading lines back accepts the names
    /// utf-16le and utf-16be that <see cref="IntakeFileInfo.Encoding"/> gives such a file.
    /// </summary>
    private static Encoding? ResolveEncoding(string encodingName, out string? error, bool allowUtf16 = false)
    {
        error = null;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (allowUtf16 && string.Equals(encodingName, "utf-16le", StringComparison.OrdinalIgnoreCase))
        {
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
        }

        if (allowUtf16 && string.Equals(encodingName, "utf-16be", StringComparison.OrdinalIgnoreCase))
        {
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
        }

        try
        {
            var encoding = Encoding.GetEncoding(string.IsNullOrWhiteSpace(encodingName) ? "utf-8" : encodingName);
            if (encoding.GetByteCount("\n") == 1)
            {
                return encoding;
            }

            error = Utf16SettingMessage;
            return null;
        }
        catch (ArgumentException)
        {
            error = "Encoding name is unknown. Enter a valid encoding like utf-8 or windows-1252.";
            return null;
        }
    }

    private static void NoLine(in IntakeLine line)
    {
    }
}
