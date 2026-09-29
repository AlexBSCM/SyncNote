using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SyncNote.Core.Interfaces;

namespace SyncNote.Windows.Services;

// TCP-реализация ITransport: длина int32 BE + UTF-8 JSON.
// Лимит кадра 128 МБ (чанки идут мелкими кадрами, весь файл в RAM не грузится).
public sealed class TcpFrameTransport : ITransport
{
    private const int MaxFrameBytes = 128 * 1024 * 1024;
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    private TcpFrameTransport(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public static async Task<TcpFrameTransport> ConnectAsync(
        string host, int port, CancellationToken ct = default)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, ct);
            return new TcpFrameTransport(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static async Task<TcpFrameTransport> AcceptAsync(
        TcpListener listener, CancellationToken ct = default)
    {
        var client = await listener.AcceptTcpClientAsync(ct);
        return new TcpFrameTransport(client);
    }

    public async Task SendAsync(string json, CancellationToken ct = default)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        if (body.Length > MaxFrameBytes)
            throw new IOException($"Кадр слишком большой: {body.Length}.");
        byte[] head = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(head, body.Length);
        await _stream.WriteAsync(head.AsMemory(), ct);
        await _stream.WriteAsync(body.AsMemory(), ct);
        await _stream.FlushAsync(ct);
    }

    public async Task<string> ReceiveAsync(CancellationToken ct = default)
    {
        byte[] head = await ReadExactAsync(4, ct);
        int len = BinaryPrimitives.ReadInt32BigEndian(head);
        if (len < 0 || len > MaxFrameBytes)
            throw new IOException($"Некорректная длина кадра: {len}.");
        byte[] body = await ReadExactAsync(len, ct);
        return Encoding.UTF8.GetString(body);
    }

    private async Task<byte[]> ReadExactAsync(int len, CancellationToken ct)
    {
        byte[] buf = new byte[len];
        int off = 0;
        while (off < len)
        {
            int n = await _stream.ReadAsync(buf.AsMemory(off), ct);
            if (n == 0)
                throw new IOException("Пир закрыл соединение.");
            off += n;
        }
        return buf;
    }

    public ValueTask DisposeAsync()
    {
        try { _stream.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
