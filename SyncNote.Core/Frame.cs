using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace SyncNote.Core;

// Кадр протокола: 4 байта big-endian длина + UTF-8 JSON.
// Одинаково для Wi-Fi Direct сокета, Bluetooth RFCOMM и loopback-тестов.
public static class Frame
{
    public const int MaxPayloadBytes = 128 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, JsonObject msg, CancellationToken ct)
    {
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(msg);
        if (payload.Length > MaxPayloadBytes)
            throw new IOException($"Кадр {payload.Length} байт превышает лимит.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<JsonObject> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await ReadExactAsync(stream, header, ct);
        int len = BinaryPrimitives.ReadInt32BigEndian(header);
        if (len < 2 || len > MaxPayloadBytes)
            throw new IOException($"Некорректная длина кадра: {len}.");
        var payload = new byte[len];
        await ReadExactAsync(stream, payload, ct);
        var node = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(payload)
            ?? throw new IOException("Пустое JSON-сообщение.");
        return node;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buf, CancellationToken ct)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await stream.ReadAsync(buf.AsMemory(off), ct);
            if (n == 0)
                throw new IOException("Соединение закрыто посреди кадра.");
            off += n;
        }
    }
}
