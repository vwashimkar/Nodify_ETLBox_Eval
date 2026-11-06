using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace EtlDesigner.Engine.Intake;

/// <summary>
/// One line of a file. <see cref="Head"/> is valid only during the call and holds at most the first
/// <see cref="LineIndexer.HeadCapacity"/> bytes, without the line ending. <see cref="EndByte"/> is
/// just after the last character, before the line ending. Blank means spaces and tabs only.
/// </summary>
public readonly ref struct IntakeLine(long number, long startByte, long endByte, bool isBlank, ReadOnlySpan<byte> head)
{
    public long Number { get; } = number;

    public long StartByte { get; } = startByte;

    public long EndByte { get; } = endByte;

    public bool IsBlank { get; } = isBlank;

    public ReadOnlySpan<byte> Head { get; } = head;

    public bool HeadIsWholeLine => EndByte - StartByte == Head.Length;
}

public delegate void IntakeLineHandler(in IntakeLine line);

/// <summary>How a scan ended. <see cref="File"/> is set when the file was read to the end.</summary>
public sealed record LineIndexOutcome(IntakeFileInfo? File, long Size, long BytesRead, bool Cancelled, string? Error);

/// <summary>
/// Reads a stream once, in blocks, and reports every line by number and byte offset. No content is
/// kept: only the head of the current line, so memory does not grow with the file or with a very
/// long line. The SHA-256 is computed in the same pass. The stream is closed before this returns,
/// also after a cancel.
/// </summary>
public static class LineIndexer
{
    public const int BlockSize = 64 * 1024;

    /// <summary>Enough for a marker line. A longer line reaches the handler with a cut head [U: chosen].</summary>
    public const int HeadCapacity = 4096;

    public const string NotFoundMessage = "File was not found. Check the path and try again.";

    public const string UnreadableMessage = "The file could not be read. It may be open in another program or you may have no access to it. Close the other program, check the access rights and try again.";

    public const string EmptyMessage = "The file is empty. Choose a file with content and try again.";

    public const string Utf16Message = "The file is UTF-16 or UTF-32, which the intake cannot read yet. Save it as UTF-8 and try again.";

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private static readonly SearchValues<byte> LineBreaks = SearchValues.Create("\r\n"u8);

    public static LineIndexOutcome Scan(
        IntakeItem item,
        int maxFileSizeMb,
        string encodingName,
        IntakeLineHandler onLine,
        Func<int> containersFound,
        Action<IntakeProgress>? progress,
        CancellationToken token)
    {
        try
        {
            using var stream = item.OpenRead();
            return ScanStream(stream, item, maxFileSizeMb, encodingName, onLine, containersFound, progress, token);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new LineIndexOutcome(null, item.Size, 0, false, NotFoundMessage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new LineIndexOutcome(null, item.Size, 0, false, UnreadableMessage);
        }
    }

    private static LineIndexOutcome ScanStream(
        Stream stream,
        IntakeItem item,
        int maxFileSizeMb,
        string encodingName,
        IntakeLineHandler onLine,
        Func<int> containersFound,
        Action<IntakeProgress>? progress,
        CancellationToken token)
    {
        var size = stream.CanSeek ? stream.Length : item.Size;
        if (size > maxFileSizeMb * 1024L * 1024L)
        {
            var message = $"The file is {size / 1048576.0:F1} MB, above the limit of {maxFileSizeMb} MB. Raise the limit or choose a smaller file.";
            return new LineIndexOutcome(null, size, 0, false, message);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BlockSize];
        var head = new byte[HeadCapacity];
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        long blockStart = 0, lineStart = 0, lineNumber = 0;
        long lfCount = 0, crLfCount = 0, crCount = 0;
        var headLength = 0;
        var isBlank = true;
        var afterCr = false;

        LineIndexOutcome Stopped() => new(null, size, blockStart, true, null);

        void Keep(ReadOnlySpan<byte> piece)
        {
            isBlank = isBlank && piece.IndexOfAnyExcept((byte)' ', (byte)'\t') < 0;

            var room = Math.Min(piece.Length, HeadCapacity - headLength);
            if (room > 0)
            {
                piece[..room].CopyTo(head.AsSpan(headLength));
                headLength += room;
            }
        }

        var read = stream.ReadAtLeast(buffer, 4, throwOnEndOfStream: false);
        var start = 0;
        var firstBytes = buffer.AsSpan(0, read);
        var hasBom = firstBytes.StartsWith<byte>([0xEF, 0xBB, 0xBF]);
        if (hasBom)
        {
            start = 3;
            lineStart = 3;
        }
        else if (firstBytes.StartsWith<byte>([0xFF, 0xFE]) || firstBytes.StartsWith<byte>([0xFE, 0xFF]))
        {
            return new LineIndexOutcome(null, size, 0, false, Utf16Message);
        }

        while (read > 0)
        {
            if (token.IsCancellationRequested)
            {
                return Stopped();
            }

            hash.AppendData(buffer, 0, read);
            var block = buffer.AsSpan(0, read);
            var i = start;
            start = 0;

            while (i < read)
            {
                if (afterCr)
                {
                    afterCr = false;
                    if (block[i] == (byte)'\n')
                    {
                        crLfCount++;
                        i++;
                        lineStart = blockStart + i;
                        continue;
                    }

                    crCount++;
                }

                var rest = block[i..];
                var next = rest.IndexOfAny(LineBreaks);
                if (next < 0)
                {
                    Keep(rest);
                    break;
                }

                Keep(rest[..next]);
                i += next;

                lineNumber++;
                onLine(new IntakeLine(lineNumber, lineStart, blockStart + i, isBlank, head.AsSpan(0, headLength)));
                if (token.IsCancellationRequested)
                {
                    blockStart += read;
                    return Stopped();
                }

                if (block[i] == (byte)'\n')
                {
                    lfCount++;
                }
                else
                {
                    afterCr = true;
                }

                i++;
                lineStart = blockStart + i;
                headLength = 0;
                isBlank = true;
            }

            blockStart += read;

            if (progress is not null && clock.Elapsed - lastReport >= ProgressInterval)
            {
                lastReport = clock.Elapsed;
                progress(new IntakeProgress(blockStart, size, containersFound(), IntakePhase.Reading));
            }

            read = stream.Read(buffer);
        }

        if (afterCr)
        {
            crCount++;
        }

        if (blockStart > lineStart)
        {
            lineNumber++;
            onLine(new IntakeLine(lineNumber, lineStart, blockStart, isBlank, head.AsSpan(0, headLength)));
        }

        var encoding = hasBom ? "utf-8" : encodingName;
        var lineEnding = LineEndingOf(lfCount, crLfCount, crCount);
        var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        var file = new IntakeFileInfo(item.Name, blockStart, sha256, encoding, hasBom, lineEnding, lineNumber);

        return new LineIndexOutcome(file, size, blockStart, false, lineNumber == 0 ? EmptyMessage : null);
    }

    private static LineEndingKind LineEndingOf(long lf, long crLf, long cr)
    {
        var kinds = (lf > 0 ? 1 : 0) + (crLf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        if (kinds == 0)
        {
            return LineEndingKind.None;
        }

        if (kinds > 1)
        {
            return LineEndingKind.Mixed;
        }

        return lf > 0 ? LineEndingKind.Lf : crLf > 0 ? LineEndingKind.CrLf : LineEndingKind.Cr;
    }
}
