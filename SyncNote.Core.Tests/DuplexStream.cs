using System.Collections.Concurrent;
using System.IO;

namespace SyncNote.Core.Tests;

// In-memory дуплексная пара Stream: доказывает, что сессия не зависит от TCP
// (тот же код позже поедет поверх Bluetooth RFCOMM).
public sealed class DuplexStream : Stream
{
    private readonly BlockingCollection<byte[]> _incoming = new();
    private byte[] _current = Array.Empty<byte>();
    private int _pos;
    private bool _disposed;

    public DuplexStream Peer { get; private set; } = null!;

    public static (DuplexStream A, DuplexStream B) CreatePair()
    {
        var a = new DuplexStream();
        var b = new DuplexStream();
        a.Peer = b;
        b.Peer = a;
        return (a, b);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken ct)
    {
        while (_pos >= _current.Length)
        {
            byte[] next;
            try
            {
                next = await Task.Run(() => _incoming.Take(ct), ct);
            }
            catch (InvalidOperationException)
            {
                return 0; // CompletedAdding — конец потока.
            }
            _current = next;
            _pos = 0;
        }
        int n = Math.Min(count, _current.Length - _pos);
        Buffer.BlockCopy(_current, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, offset, copy, 0, count);
        Peer._incoming.Add(copy);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            try { Peer._incoming.CompleteAdding(); } catch { }
            _incoming.CompleteAdding();
        }
        base.Dispose(disposing);
    }
}
