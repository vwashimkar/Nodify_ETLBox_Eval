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

        var tracker = EncodingTracker.For(settings.Encoding, out var encodingError);

        IntakeResult Finish(IntakeFileInfo? file, IReadOnlyList<ContainerInfo> containers, bool cancelled, string? error)
        {
            var known = file is not null;
            return new(Guid.NewGuid(), item.Id, startedUtc, DateTime.UtcNow, ToolVersion, file, containers, warnings, clock.ElapsedMilliseconds, cancelled, error,
                known ? tracker!.Name : null, known && tracker!.Guessed, known && tracker!.InvalidUtf8Seen,
                known && tracker!.Guessed ? IntakeEncodings.GuessReason : null);
        }

        if (token.IsCancellationRequested)
        {
            return Finish(null, [], cancelled: true, null);
        }

        if (tracker is null)
        {
            return Finish(null, [], cancelled: false, encodingError);
        }

        // Markers mode: the scanner alone. AutoDetect: the detector alone. MarkersThenAutoDetect: the
        // scanner hands the lines between marker segments to the detector. Both decode with the
        // encoding of the bytes read so far, which Auto can change from UTF-8 to windows-1252.
        var detector = settings.Mode == IntakeMode.Markers ? null : new AutoDetector(item, settings.Detector, () => tracker.Current, settings.Validation);
        var scanner = settings.Mode == IntakeMode.AutoDetect ? null : new MarkerScanner(settings.Markers, () => tracker.HeadEncoding, detector);
        IntakeLineHandler onLine = scanner is not null ? scanner.OnLine : detector!.OnLine;
        int Found() => (scanner?.ContainerCount ?? 0) + (detector?.ContainerCount ?? 0);

        var outcome = LineIndexer.Scan(item, settings.MaxFileSizeMb, tracker, onLine, Found, progress, token);

        if (outcome.File is not null && outcome.Error is null)
        {
            scanner?.Finish();
            detector?.Finish();
            progress?.Invoke(new IntakeProgress(outcome.BytesRead, outcome.Size, Found(), IntakePhase.Detecting));
        }

        if (outcome.Error is not null)
        {
            return Finish(outcome.File, [], cancelled: false, outcome.Error);
        }

        warnings.AddRange(scanner?.Warnings ?? []);
        warnings.AddRange(detector?.Warnings ?? []);
        warnings.Sort((a, b) => (a.Line ?? 0).CompareTo(b.Line ?? 0));

        var containers = Merge(scanner?.Containers ?? [], detector?.Containers ?? []);
        progress?.Invoke(new IntakeProgress(outcome.BytesRead, outcome.Size, containers.Count, IntakePhase.Done));
        return Finish(outcome.File, containers, outcome.Cancelled, null);
    }

    /// <summary>Marker segments and detected containers in file order, numbered again from 1.</summary>
    private static IReadOnlyList<ContainerInfo> Merge(IReadOnlyList<ContainerInfo> markers, IReadOnlyList<ContainerInfo> detected)
    {
        if (detected.Count == 0)
        {
            return markers;
        }

        if (markers.Count == 0)
        {
            return detected;
        }

        return markers.Concat(detected)
            .OrderBy(c => c.MarkerBeginLine ?? c.ContentFirstLine ?? 0)
            .Select((c, i) => c with { Order = i + 1 })
            .ToList();
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

        var encoding = ResolveEncoding(encodingName, out var encodingError);
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

    internal static void MoveTo(Stream stream, long position)
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
    /// The encoding to decode a file's bytes with, by the name of <see cref="IntakeResult.EncodingUsed"/>
    /// or <see cref="IntakeFileInfo.Encoding"/>. "auto" or no name means UTF-8 here: only a whole read
    /// can tell Auto's choice.
    /// </summary>
    internal static Encoding? ResolveEncoding(string? encodingName, out string? error)
    {
        error = null;
        var encoding = string.IsNullOrWhiteSpace(encodingName) || IntakeEncodings.IsAuto(encodingName)
            ? IntakeEncodings.Get(IntakeEncodings.Utf8)
            : IntakeEncodings.Get(encodingName);
        if (encoding is null)
        {
            error = IntakeEncodings.UnknownMessage;
        }

        return encoding;
    }
}
