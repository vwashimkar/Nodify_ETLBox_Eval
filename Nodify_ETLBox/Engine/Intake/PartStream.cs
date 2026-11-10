using System.IO;

namespace EtlDesigner.Engine.Intake;

/// <summary>
/// A read-only, forward-only stream over bytes [<see cref="StartByte"/>, <see cref="StartByte"/> + <see cref="PartLength"/>)
/// of a file. The file is opened for reading only (others may read it too), never written, and
/// closed by <see cref="Stream.Dispose()"/>. Nothing is copied, so memory stays flat for any part size.
/// </summary>
public sealed class PartStream : Stream
{
    public const string ForwardOnlyMessage = "A part of a file is read once from start to end; it cannot jump to another position.";

    private readonly FileStream _file;
    private long _left;

    public PartStream(string path, long startByte, long length)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(startByte);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        StartByte = startByte;
        PartLength = length;
        _left = length;
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        try
        {
            _file.Seek(startByte, SeekOrigin.Begin);
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public long StartByte { get; }

    public long PartLength { get; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException(ForwardOnlyMessage);

    public override long Position
    {
        get => PartLength - _left;
        set => throw new NotSupportedException(ForwardOnlyMessage);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_left <= 0 || buffer.Length == 0)
        {
            return 0;
        }

        var read = _file.Read(buffer[..(int)Math.Min(buffer.Length, _left)]);
        _left -= read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_left <= 0 || buffer.Length == 0)
        {
            return 0;
        }

        var read = await _file.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _left)], cancellationToken).ConfigureAwait(false);
        _left -= read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(ForwardOnlyMessage);

    public override void SetLength(long value) => throw new NotSupportedException(ForwardOnlyMessage);

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(ForwardOnlyMessage);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file.Dispose();
        }

        base.Dispose(disposing);
    }
}
