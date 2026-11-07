using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace EtlDesigner.Engine.Intake;

/// <summary>
/// One line of a file. <see cref="Head"/> is valid only during the call and holds at most the first
/// <see cref="LineIndexer.HeadCapacity"/> bytes, without the line ending. For a UTF-16 file the head
/// is the line converted to UTF-8, while the offsets stay offsets in the original file.
/// <see cref="EndByte"/> is just after the last character, before the line ending. Blank means
/// spaces and tabs only.
/// </summary>
public readonly ref struct IntakeLine(long number, long startByte, long endByte, bool isBlank, ReadOnlySpan<byte> head, bool headIsWholeLine)
{
    public long Number { get; } = number;

    public long StartByte { get; } = startByte;

    public long EndByte { get; } = endByte;

    public bool IsBlank { get; } = isBlank;

    public ReadOnlySpan<byte> Head { get; } = head;

    public bool HeadIsWholeLine { get; } = headIsWholeLine;
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

    public const string Utf32Message = "The file is UTF-32, which the intake cannot read. Save it as UTF-8 or UTF-16 and try again.";

    public const string NoBomMessage = "The file contains zero bytes but has no byte order mark (BOM). It may be UTF-16 without a BOM, whose byte order is not guessed, or a binary file. Save it as UTF-8, or as UTF-16 with a BOM, and try again.";

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
        else if (firstBytes.StartsWith<byte>([0xFF, 0xFE, 0x00, 0x00]) || firstBytes.StartsWith<byte>([0x00, 0x00, 0xFE, 0xFF]))
        {
            return new LineIndexOutcome(null, size, 0, false, Utf32Message);
        }
        else if (firstBytes.StartsWith<byte>([0xFF, 0xFE]) || firstBytes.StartsWith<byte>([0xFE, 0xFF]))
        {
            return ScanUtf16(stream, item, size, buffer, read, firstBytes[0] == 0xFE, onLine, containersFound, progress, token);
        }
        else if (LooksLikeUtf16WithoutBom(firstBytes))
        {
            return new LineIndexOutcome(null, size, 0, false, NoBomMessage);
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
                onLine(new IntakeLine(lineNumber, lineStart, blockStart + i, isBlank, head.AsSpan(0, headLength), blockStart + i - lineStart == headLength));
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
            onLine(new IntakeLine(lineNumber, lineStart, blockStart, isBlank, head.AsSpan(0, headLength), blockStart - lineStart == headLength));
        }

        var encoding = hasBom ? "utf-8" : encodingName;
        var lineEnding = LineEndingOf(lfCount, crLfCount, crCount);
        var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        var file = new IntakeFileInfo(item.Name, blockStart, sha256, encoding, hasBom, lineEnding, lineNumber);

        return new LineIndexOutcome(file, size, blockStart, false, lineNumber == 0 ? EmptyMessage : null);
    }

    /// <summary>
    /// The same scan for UTF-16 with a BOM, in two-byte units. The BOM decides the byte order. Line
    /// ends are LF, CRLF or CR as units; offsets count bytes of the original file, BOM included.
    /// </summary>
    private static LineIndexOutcome ScanUtf16(
        Stream stream,
        IntakeItem item,
        long size,
        byte[] buffer,
        int read,
        bool bigEndian,
        IntakeLineHandler onLine,
        Func<int> containersFound,
        Action<IntakeProgress>? progress,
        CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Three UTF-8 bytes at most per char, so the converted head always fits.
        var chars = new char[HeadCapacity / 3];
        var head = new byte[HeadCapacity];
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        long blockStart = 0, lineStart = 2, lineNumber = 0;
        long lfCount = 0, crLfCount = 0, crCount = 0;
        var charCount = 0;
        var lineChars = 0L;
        var isBlank = true;
        var afterCr = false;
        var start = 2;
        int? pendingByte = null;

        void Report(long endByte)
        {
            lineNumber++;
            var length = System.Text.Encoding.UTF8.GetBytes(chars, 0, charCount, head, 0);
            onLine(new IntakeLine(lineNumber, lineStart, endByte, isBlank, head.AsSpan(0, length), lineChars == charCount));
            charCount = 0;
            lineChars = 0;
            isBlank = true;
        }

        while (read > 0)
        {
            if (token.IsCancellationRequested)
            {
                return new LineIndexOutcome(null, size, blockStart, true, null);
            }

            hash.AppendData(buffer, 0, read);
            for (var i = start; i < read; i++)
            {
                if (pendingByte is null)
                {
                    pendingByte = buffer[i];
                    continue;
                }

                var unitStart = blockStart + i - 1;
                var c = bigEndian ? (char)((pendingByte.Value << 8) | buffer[i]) : (char)(pendingByte.Value | (buffer[i] << 8));
                pendingByte = null;

                if (afterCr)
                {
                    afterCr = false;
                    if (c == '\n')
                    {
                        crLfCount++;
                        lineStart = unitStart + 2;
                        continue;
                    }

                    crCount++;
                }

                if (c is '\n' or '\r')
                {
                    Report(unitStart);
                    if (token.IsCancellationRequested)
                    {
                        return new LineIndexOutcome(null, size, blockStart + read, true, null);
                    }

                    if (c == '\n')
                    {
                        lfCount++;
                    }
                    else
                    {
                        afterCr = true;
                    }

                    lineStart = unitStart + 2;
                    continue;
                }

                isBlank = isBlank && c is ' ' or '\t';
                lineChars++;
                if (charCount < chars.Length)
                {
                    chars[charCount++] = c;
                }
            }

            start = 0;
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

        // An odd last byte is no character; it counts only in the size and the hash [U: chosen].
        var end = blockStart - (pendingByte is null ? 0 : 1);
        if (end > lineStart)
        {
            Report(end);
        }

        var lineEnding = LineEndingOf(lfCount, crLfCount, crCount);
        var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        var file = new IntakeFileInfo(item.Name, blockStart, sha256, bigEndian ? "utf-16be" : "utf-16le", true, lineEnding, lineNumber);

        return new LineIndexOutcome(file, size, blockStart, false, lineNumber == 0 ? EmptyMessage : null);
    }

    /// <summary>
    /// UTF-16 text without a BOM has zero bytes at every second position (the high byte of ASCII)
    /// and almost none at the others. This only refuses such a file; it never reads it [U: chosen,
    /// 30 % and 5 % of the first 1,024 bytes].
    /// </summary>
    private static bool LooksLikeUtf16WithoutBom(ReadOnlySpan<byte> first)
    {
        first = first[..Math.Min(first.Length, 1024)];
        if (first.Length < 4)
        {
            return false;
        }

        int even = 0, odd = 0;
        for (var i = 0; i < first.Length; i++)
        {
            if (first[i] == 0 && i % 2 == 0)
            {
                even++;
            }
            else if (first[i] == 0)
            {
                odd++;
            }
        }

        var half = first.Length / 2.0;
        return (even >= 0.3 * half && odd < 0.05 * half) || (odd >= 0.3 * half && even < 0.05 * half);
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
