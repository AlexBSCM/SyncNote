namespace SyncNote.Core;

// Объединение раздельных read/write потоков (Bluetooth RFCOMM)
// в один дуплексный Stream для StreamSession.
public sealed class JoinedStream(Stream read, Stream write) : Stream
{
    public override bool CanRead => read.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => write.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => write.Flush();
    public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);

    public override int Read(byte[] buffer, int offset, int count) =>
        read.Read(buffer, offset, count);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        read.ReadAsync(buffer, offset, count, ct);

    public override void Write(byte[] buffer, int offset, int count) =>
        write.Write(buffer, offset, count);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        write.WriteAsync(buffer, offset, count, ct);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            read.Dispose();
            write.Dispose();
        }
        base.Dispose(disposing);
    }
}
