using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SyncNote.Core;

public sealed record SyncSessionResult(int Pushed, int Pulled, int Conflicts);

// Клиентская сторона сессии (телефон): push своего экспорта,
// затем pull экспорта сервера. Отмена — токеном.
public sealed class SyncClient(
    string host, int port, string deviceId, string deviceName, string? token)
{
    public async Task<SyncSessionResult> PushAndPullAsync(
        ISyncStore store, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        using var stream = client.GetStream();
        int pushed = 0, pulled = 0, conflicts = 0;

        var hello = new JsonObject
        {
            ["t"] = "hello",
            ["deviceId"] = deviceId,
            ["deviceName"] = deviceName,
        };
        if (token is not null)
            hello["token"] = token;
        await Frame.WriteAsync(stream, hello, ct);
        var greet = await Frame.ReadAsync(stream, ct);
        if (greet["t"]?.GetValue<string>() != "hello_ok")
            throw new HelloRejectedException(
                greet["reason"]?.GetValue<string>() ?? "отказ без причины");

        foreach (var dto in store.Export())
        {
            await SendNoteAsync(store, stream, dto, ct);
            await Frame.ReadAsync(stream, ct); // applied
            pushed++;
        }
        await Frame.WriteAsync(stream, new JsonObject { ["t"] = "sync_end" }, ct);

        var pending = new Dictionary<string, (string Name, string Mime, byte[] Bytes)>();
        while (true)
        {
            var msg = await Frame.ReadAsync(stream, ct);
            var type = msg["t"]?.GetValue<string>();
            if (type == "sync_end")
                break;
            if (type == "file_begin" || type == "file_chunk" || type == "file_end")
            {
                BufferFileChunk(msg, pending);
                continue;
            }
            if (type == "note_upsert")
            {
                var dto = SyncJson.FromNode(msg["note"]!);
                var (result, _) = store.Apply(dto,
                    sha => pending.TryGetValue(sha, out var f) ? f.Bytes : null);
                pending.Clear();
                pulled++;
                if (result == ApplyResult.Conflict)
                    conflicts++;
                await Frame.WriteAsync(stream, new JsonObject
                {
                    ["t"] = "applied",
                    ["id"] = dto.Id.ToString("N"),
                    ["result"] = result.ToString(),
                }, ct);
                continue;
            }
            throw new IOException($"Неожиданный тип {type}.");
        }
        return new SyncSessionResult(pushed, pulled, conflicts);
    }

    private static void BufferFileChunk(
        JsonObject msg, Dictionary<string, (string Name, string Mime, byte[] Bytes)> pending)
    {
        var type = msg["t"]!.GetValue<string>();
        var sha = msg["sha"]!.GetValue<string>();
        if (type == "file_begin")
        {
            pending[sha] = (msg["name"]!.GetValue<string>(), msg["mime"]!.GetValue<string>(),
                Array.Empty<byte>());
            return;
        }
        if (!pending.TryGetValue(sha, out var cur))
            throw new IOException("Чанк без file_begin.");
        if (type == "file_chunk")
        {
            var part = Convert.FromBase64String(msg["data_b64"]!.GetValue<string>());
            pending[sha] = (cur.Name, cur.Mime, cur.Bytes.Concat(part).ToArray());
            return;
        }
        using var hash = SHA256.Create();
        var hex = Convert.ToHexString(hash.ComputeHash(cur.Bytes)).ToLowerInvariant();
        if (hex != sha.ToLowerInvariant())
            throw new IOException("sha256 файла не сошлось.");
    }

    private static async Task SendNoteAsync(
        ISyncStore store, NetworkStream stream, SyncNoteDto dto, CancellationToken ct)
    {
        var bySha = store.GetAttachments(dto.Id).ToDictionary(a => a.Sha256);
        foreach (var meta in dto.Attachments)
        {
            if (!bySha.TryGetValue(meta.Sha256, out var att))
                throw new IOException($"Нет локального файла {meta.FileName}.");
            var bytes = await File.ReadAllBytesAsync(
                Path.Combine(store.FilesDirectory, att.StoredName), ct);
            await Frame.WriteAsync(stream, new JsonObject
            {
                ["t"] = "file_begin",
                ["sha"] = meta.Sha256,
                ["name"] = meta.FileName,
                ["mime"] = meta.MimeType,
                ["size"] = bytes.Length,
            }, ct);
            const int chunk = 64 * 1024;
            for (int off = 0; off < bytes.Length; off += chunk)
            {
                var part = bytes.AsSpan(off, Math.Min(chunk, bytes.Length - off)).ToArray();
                await Frame.WriteAsync(stream, new JsonObject
                {
                    ["t"] = "file_chunk",
                    ["sha"] = meta.Sha256,
                    ["data_b64"] = Convert.ToBase64String(part),
                }, ct);
            }
            await Frame.WriteAsync(stream, new JsonObject
            {
                ["t"] = "file_end",
                ["sha"] = meta.Sha256,
            }, ct);
        }
        await Frame.WriteAsync(stream, new JsonObject
        {
            ["t"] = "note_upsert",
            ["note"] = SyncJson.ToNode(dto),
        }, ct);
    }
}
