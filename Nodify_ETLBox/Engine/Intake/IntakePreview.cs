using System.IO;
using System.Text;

namespace EtlDesigner.Engine.Intake;

/// <summary>One line of a preview with its line number in the whole file.</summary>
public sealed record PreviewLine(long LineNumber, string Text);

/// <summary>
/// The first lines of a container. <see cref="Truncated"/> says more lines exist; <see cref="LinesCut"/>
/// says at least one line was shortened (it then ends with "..."). <see cref="Error"/> is one plain
/// sentence; the lines are then empty.
/// </summary>
public sealed record IntakePreviewResult(IReadOnlyList<PreviewLine> Lines, bool Truncated, bool LinesCut, string? Error);

/// <summary>
/// Reads the start of one container by its byte offsets, with the encoding of the read
/// (<see cref="IntakeResult.EncodingUsed"/>). It stops as soon as it has the lines it needs, and
/// keeps at most <c>maxCharsPerLine</c> characters of a line, so a huge container or a very long
/// line costs no more than the preview shows.
/// </summary>
public static class IntakePreview
{
    public const string Ellipsis = "...";

    public static IntakePreviewResult Read(IntakeItem item, ContainerInfo container, string encoding, int maxLines = 200, int maxCharsPerLine = 500,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(container);

        var decoding = IntakeReader.ResolveEncoding(encoding, out var error);
        if (decoding is null)
        {
            return new IntakePreviewResult([], false, false, error);
        }

        if (container.ContentStartByte is not { } start || container.ContentEndByte is not { } end || container.ContentFirstLine is not { } firstLine || maxLines <= 0)
        {
            return new IntakePreviewResult([], container.ContentLineCount > 0 && maxLines <= 0, false, null);
        }

        var lines = new List<PreviewLine>();
        var current = new StringBuilder();
        var lineLength = 0L;
        var cut = false;
        var truncated = false;

        void EndLine()
        {
            var text = current.ToString();
            if (lineLength > maxCharsPerLine)
            {
                text += Ellipsis;
                cut = true;
            }

            lines.Add(new PreviewLine(firstLine + lines.Count, text));
            current.Clear();
            lineLength = 0;
        }

        try
        {
            using var stream = item.OpenRead();
            IntakeReader.MoveTo(stream, start);
            var buffer = new byte[16 * 1024];
            var decoder = decoding.GetDecoder();
            var chars = new char[decoding.GetMaxCharCount(buffer.Length)];
            var remaining = end - start;
            var afterCr = false;

            while (remaining > 0 && !truncated)
            {
                if (token.IsCancellationRequested)
                {
                    return new IntakePreviewResult(lines, true, cut, null);
                }

                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    break;
                }

                remaining -= read;
                var count = decoder.GetChars(buffer, 0, read, chars, 0, flush: remaining == 0);
                for (var i = 0; i < count; i++)
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

                    if (lines.Count == maxLines)
                    {
                        // A character after the last wanted line: there is more.
                        truncated = true;
                        break;
                    }

                    if (c is '\n' or '\r')
                    {
                        EndLine();
                        afterCr = c == '\r';
                        continue;
                    }

                    if (lineLength++ < maxCharsPerLine && c != '﻿')
                    {
                        current.Append(c);
                    }
                }
            }

            if (!truncated)
            {
                if (lines.Count < maxLines)
                {
                    EndLine();
                }
                else
                {
                    // The last wanted line ended with a line break, so another line, maybe blank, follows.
                    truncated = true;
                }
            }

            return new IntakePreviewResult(lines, truncated, cut, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new IntakePreviewResult([], false, false, LineIndexer.NotFoundMessage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new IntakePreviewResult([], false, false, LineIndexer.UnreadableMessage);
        }
    }
}
