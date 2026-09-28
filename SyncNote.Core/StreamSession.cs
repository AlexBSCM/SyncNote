using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace SyncNote.Core;

public sealed class HelloRejectedException(string reason)
    : IOException($"Сопряжение отклонено: {reason}")
{
    public string Reason { get; } = reason;
}

// Сессия поверх любого дуплексного Stream: TCP (петля, Wi-Fi Direct, LAN)
// или RFCOMM (Bluetooth-резерв). Кадры и сообщения — те же.
// Экономия трафика: клиент шлёт только правки (rev > syncRev) и карту
// знаний (sync_begin + file_knowledge); сервер не шлёт неизменённое.
// Совместимость: новые кадры шлются только при совпадении caps;
// неизвестные типы молча игнорируются с Warning (не роняют сессию).
public static class StreamSession
{
    // Возможности стороны. Расширять только добавлением новых строк.
    public static readonly string[] Caps = ["files-v1"];
    private const string FilesCap = "files-v1";

    // Приём файлов — во временные файлы, не в RAM (стриминг чанков).
    public sealed class PendingFile
    {
        public string Name { get; set; } = string.Empty;
        public string Mime { get; set; } = "application/octet-stream";
        public string TmpPath { get; set; } = string.Empty;
        public long Size { get; set; }
    }

    public static async Task ServerSideAsync(
        ISyncStore store, PairingService pairing, Stream stream, CancellationToken ct)
    {
        var pending = new Dictionary<string, PendingFile>();
        try
        {
            var hello = await Frame.ReadAsync(stream, ct);
            if (hello["t"]?.GetValue<string>() != "hello")
            {
                await SendErr(stream, "первым сообщением ждали hello", ct);
                return;
            }
            var deviceId = hello["deviceId"]?.GetValue<string>() ?? "";
            var deviceName = hello["deviceName"]?.GetValue<string>() ?? "?";
            var token = hello["token"]?.GetValue<string>();

            bool ok;
            if (!string.IsNullOrEmpty(token))
            {
                ok = pairing.RedeemToken(token);
                if (ok)
                    pairing.TrustDevice(deviceId, deviceName);
            }
            else
            {
                ok = pairing.IsTrusted(deviceId);
            }
            if (!ok || string.IsNullOrEmpty(deviceId))
            {
                ServerLog.Line("hello rejected");
                await SendErr(stream, "нет доверия: нужен токен сопряжения", ct);
                return;
            }
            ServerLog.Line($"hello ok device={deviceId[..Math.Min(8, deviceId.Length)]}");
            var helloOk = new JsonObject
            {
                ["t"] = "hello_ok",
                ["serverDeviceId"] = store.DeviceId,
            };
            var capsArr = new JsonArray();
            foreach (var c in Caps)
                capsArr.Add(c);
            helloOk["caps"] = capsArr;
            await Frame.WriteAsync(stream, helloOk, ct);

            var (knowledge, fileKnowledge, peerCaps, firstExtra) = await ReadKnowledgeAsync(stream, ct);
            ServerLog.Line($"knowledge items={knowledge.Count} fileItems={fileKnowledge.Count} hasExtra={firstExtra is not null}");
            bool peerFiles = peerCaps.Contains(FilesCap);
            if (firstExtra is not null)
            {
                await HandleClientMessage(store, stream, firstExtra, pending, ct);
                if (firstExtra["t"]?.GetValue<string>() == "sync_end")
                    goto PushPhase;
            }
            while (true)
            {
                var msg = await Frame.ReadAsync(stream, ct);
                var type = msg["t"]?.GetValue<string>();
                if (type == "sync_end")
                    break;
                await HandleClientMessage(store, stream, msg, pending, ct);
            }

        PushPhase:
            foreach (var dto in store.Export())
            {
                var idN = dto.Id.ToString("N");
                if (knowledge.TryGetValue(idN, out var kr) && kr >= dto.Rev
                    && dto.Rev <= store.GetSyncRev(dto.Id))
                    continue;
                await SendNoteAsync(store, stream, dto, ct);
                var ack = await Frame.ReadAsync(stream, ct); // applied
                if (ack["result"]?.GetValue<string>() is "Inserted" or "FastForwarded" or "NoOp")
                    store.SetSyncRev(dto.Id, dto.Rev);
            }
            // Фаза файлов — только при совпадении caps и включённом флаге.
            if (peerFiles && FeatureFlags.EnableSeparateFiles && store is IFileStore files)            {
                foreach (var f in files.ExportFiles())
                {
                    var idN = f.Id.ToString("N");
                    if (fileKnowledge.TryGetValue(idN, out var kr) && kr >= f.Rev
                        && f.Rev <= files.GetFileSyncRev(f.Id))
                        continue;
                    var ackResult = await SendFileDtoAsync(store, stream, f, ct);
                    if (ackResult is "Inserted" or "FastForwarded" or "NoOp")
                        files.SetFileSyncRev(f.Id, f.Rev);
                }
            }
            await Frame.WriteAsync(stream, new JsonObject { ["t"] = "sync_end" }, ct);
            ServerLog.Line("session ok");
        }
        catch (IOException ex) { ServerLog.Line($"session io: {ex.GetType().Name}"); }
        catch (OperationCanceledException)
        {
            var stack = string.Join(" <- ", new System.Diagnostics.StackTrace()
                .GetFrames().Take(6).Select(f => f.GetMethod()?.Name));
            ServerLog.Line($"session cancelled: {stack}");
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
            ServerLog.Line($"session fail: {ex.GetType().Name}: {msg}");
        }
        finally
        {
            DiscardPending(pending);
        }
    }

    // Карта ревизий клиента (может отсутствовать у старых версий —
    // тогда последнее значение содержит первое сообщение потока).
    private static async Task<(Dictionary<string, long> Knowledge, Dictionary<string, long> FileKnowledge, HashSet<string> Caps, JsonObject? FirstExtra)>
        ReadKnowledgeAsync(Stream stream, CancellationToken ct)
    {
        var knowledge = new Dictionary<string, long>();
        var fileKnowledge = new Dictionary<string, long>();
        var caps = new HashSet<string>();
        var first = await Frame.ReadAsync(stream, ct);
        if (first["t"]?.GetValue<string>() != "sync_begin")
            return (knowledge, fileKnowledge, caps, first);
        foreach (var item in first["knowledge"]?.AsArray() ?? new JsonArray())
        {
            var o = item!.AsObject();
            knowledge[o["id"]!.GetValue<string>()] = o["rev"]!.GetValue<long>();
        }
        foreach (var item in first["file_knowledge"]?.AsArray() ?? new JsonArray())
        {
            var o = item!.AsObject();
            fileKnowledge[o["id"]!.GetValue<string>()] = o["rev"]!.GetValue<long>();
        }
        foreach (var c in first["caps"]?.AsArray() ?? new JsonArray())
            caps.Add(c!.GetValue<string>());
        return (knowledge, fileKnowledge, caps, null);
    }

    private static async Task HandleClientMessage(
        ISyncStore store, Stream stream, JsonObject msg,
        Dictionary<string, PendingFile> pending,
        CancellationToken ct)
    {
        var type = msg["t"]?.GetValue<string>();
        if (type == "sync_begin")
        {
            // Повторный sync_begin посреди потока — игнорируем (caps уже сняты).
            return;
        }
        if (type == "file_begin" || type == "file_chunk" || type == "file_end")
        {
            BufferFileChunk(msg, pending);
            return;
        }
        if (type == "query_has_hash")
        {
            await AnswerHasHashAsync(store, stream, msg, ct);
            return;
        }
        if (type == "file_register")
        {
            await ApplyFileRegisterAsync(store, stream, msg, pending, ct);
            return;
        }
        if (type == "note_upsert")
        {
            var dto = SyncJson.FromNode(msg["note"]!);
            var (result, conflict) = store.Apply(dto,
                sha => ReadPendingBytes(pending, sha));
            DiscardPending(pending);
            ServerLog.Line($"applied rev={dto.Rev} -> {result}");
            var ack = new JsonObject
            {
                ["t"] = "applied",
                ["id"] = dto.Id.ToString("N"),
                ["result"] = result.ToString(),
            };
            if (conflict is not null)
                ack["copyId"] = conflict.CopyId.ToString("N");
            await Frame.WriteAsync(stream, ack, ct);
            return;
        }
        if (type == "sync_end")
            return;
        // Неизвестный тип — тихий игнор с Warning (forward compatibility).
        ServerLog.Line($"warning: unknown frame {type}, ignored");
    }

    public static async Task<SyncSessionResult> ClientSideAsync(
        ISyncStore store, Stream stream,
        string deviceId, string deviceName, string? token, CancellationToken ct)
    {
        var pending = new Dictionary<string, PendingFile>();
        try
        {
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
            var serverCaps = new HashSet<string>();
            foreach (var c in greet["caps"]?.AsArray() ?? new JsonArray())
                serverCaps.Add(c!.GetValue<string>());
            bool filesCapable = serverCaps.Contains(FilesCap)
                && FeatureFlags.EnableSeparateFiles && store is IFileStore;

            var knowledge = new JsonArray();
            foreach (var dto in store.Export())
                knowledge.Add(new JsonObject
                {
                    ["id"] = dto.Id.ToString("N"),
                    ["rev"] = dto.Rev,
                });
            var fileKnowledge = new JsonArray();
            if (filesCapable)
            {
                foreach (var f in ((IFileStore)store).ExportFiles())
                    fileKnowledge.Add(new JsonObject
                    {
                        ["id"] = f.Id.ToString("N"),
                        ["rev"] = f.Rev,
                    });
            }
            var capsArr = new JsonArray();
            foreach (var c in Caps)
                capsArr.Add(c);
            await Frame.WriteAsync(stream, new JsonObject
            {
                ["t"] = "sync_begin",
                ["knowledge"] = knowledge,
                ["file_knowledge"] = fileKnowledge,
                ["caps"] = capsArr,
            }, ct);

            foreach (var dto in store.Export())
            {
                // Своё неизменённое не шлём: ревизия не выше общей.
                if (store.TryGet(dto.Id) is not null && dto.Rev <= store.GetSyncRev(dto.Id))
                    continue;
                await SendNoteAsync(store, stream, dto, ct);
                var ack = await Frame.ReadAsync(stream, ct); // applied
                pushed++;
                if (ack["result"]?.GetValue<string>() is "Inserted" or "FastForwarded" or "NoOp")
                    store.SetSyncRev(dto.Id, dto.Rev);
            }
            if (filesCapable)
            {
                var files = (IFileStore)store;
                foreach (var f in files.ExportFiles())
                {
                    if (files.TryGetFile(f.Id) is not null && f.Rev <= files.GetFileSyncRev(f.Id))
                        continue;
                    var ackResult = await SendFileDtoAsync(store, stream, f, ct);
                    pushed++;
                    if (ackResult is "Inserted" or "FastForwarded" or "NoOp")
                        files.SetFileSyncRev(f.Id, f.Rev);
                }
            }
            await Frame.WriteAsync(stream, new JsonObject { ["t"] = "sync_end" }, ct);

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
                if (type == "query_has_hash")
                {
                    await AnswerHasHashAsync(store, stream, msg, ct);
                    continue;
                }
                if (type == "file_register")
                {
                    var (result, conflict) = await ApplyFileRegisterAsync(store, stream, msg, pending, ct);
                    DiscardPending(pending);
                    pulled++;
                    if (result == ApplyResult.Conflict)
                        conflicts++;
                    _ = conflict;
                    continue;
                }
                if (type == "note_upsert")
                {
                    var dto = SyncJson.FromNode(msg["note"]!);
                    var (result, _) = store.Apply(dto,
                        sha => ReadPendingBytes(pending, sha));
                    DiscardPending(pending);
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
                // Неизвестный тип — тихий игнор с Warning.
                ServerLog.Line($"warning: unknown frame {type}, ignored");
            }
            return new SyncSessionResult(pushed, pulled, conflicts);
        }
        finally
        {
            DiscardPending(pending);
        }
    }

    // Отправка одного standalone-файла: query -> [bytes] -> register -> applied.
    // Возвращает result из квитанции.
    private static async Task<string?> SendFileDtoAsync(
        ISyncStore store, Stream stream, SyncFileDto dto, CancellationToken ct)
    {
        await Frame.WriteAsync(stream, new JsonObject
        {
            ["t"] = "query_has_hash",
            ["sha256"] = dto.Sha256,
        }, ct);
        var resp = await Frame.ReadAsync(stream, ct);
        if (resp["t"]?.GetValue<string>() != "hash_response")
            throw new IOException("Ожидался hash_response.");
        if (resp["has"]?.GetValue<bool>() != true)
            await SendRawFileAsync(store, stream, dto, ct);
        await Frame.WriteAsync(stream, new JsonObject
        {
            ["t"] = "file_register",
            ["file"] = SyncJson.ToFileNode(dto),
        }, ct);
        var ack = await Frame.ReadAsync(stream, ct); // applied
        return ack["result"]?.GetValue<string>();
    }

    // Отправка байтов файла чанками (стриминг с диска, без RAM-буфера).
    private static async Task SendRawFileAsync(
        ISyncStore store, Stream stream, SyncFileDto dto, CancellationToken ct)
    {
        var path = FileIo.ResolveStoragePath(store.FilesDirectory, dto.Sha256);
        using var fs = File.OpenRead(path);
        await Frame.WriteAsync(stream, new JsonObject
        {
            ["t"] = "file_begin",
            ["sha"] = dto.Sha256,
            ["name"] = dto.Name,
            ["mime"] = dto.Mime,
            ["size"] = fs.Length,
        }, ct);
        var buf = new byte[64 * 1024];
        int n;
        while ((n = await fs.ReadAsync(buf.AsMemory(), ct)) > 0)
        {
            await Frame.WriteAsync(stream, new JsonObject
            {
                ["t"] = "file_chunk",
                ["sha"] = dto.Sha256,
                ["data_b64"] = Convert.ToBase64String(buf, 0, n),
            }, ct);
        }
        await Frame.WriteAsync(stream, new JsonObject
        {
            ["t"] = "file_end",
            ["sha"] = dto.Sha256,
        }, ct);
    }

    private static async Task AnswerHasHashAsync(
        ISyncStore store, Stream stream, JsonObject msg, CancellationToken ct)
    {
        var sha = msg["sha256"]?.GetValue<string>() ?? "";
        bool has = false;
        if (FeatureFlags.EnableSeparateFiles && store is IFileStore files)
        {
            var norm = sha.ToLowerInvariant();
            has = files.HasLiveReferencesToSha(norm)
                || File.Exists(Path.Combine(store.FilesDirectory, norm));
        }
        await Frame.WriteAsync(stream, new JsonObject
        {
            ["t"] = "hash_response",
            ["sha256"] = sha,
            ["has"] = has,
        }, ct);
    }

    // Применение file_register. Возвращает (result, conflict).
    private static async Task<(ApplyResult Result, ConflictInfo? Conflict)> ApplyFileRegisterAsync(
        ISyncStore store, Stream stream, JsonObject msg,
        Dictionary<string, PendingFile> pending,
        CancellationToken ct)
    {
        var dto = SyncJson.FromFileNode(msg["file"]!);
        if (!FeatureFlags.EnableSeparateFiles || store is not IFileStore files)
        {
            // Фича выключена: честный NoOp-ack, чтобы не рвать фрейминг.
            ServerLog.Line("warning: file_register ignored (disabled)");
            await Frame.WriteAsync(stream, new JsonObject
            {
                ["t"] = "applied",
                ["id"] = dto.Id.ToString("N"),
                ["result"] = ApplyResult.NoOp.ToString(),
            }, ct);
            DiscardPending(pending);
            return (ApplyResult.NoOp, null);
        }
        var (result, conflict) = files.ApplyFile(dto,
            sha => ReadPendingBytes(pending, sha));
        DiscardPending(pending);
        ServerLog.Line($"applied file rev={dto.Rev} -> {result}");
        var ack = new JsonObject
        {
            ["t"] = "applied",
            ["id"] = dto.Id.ToString("N"),
            ["result"] = result.ToString(),
        };
        if (conflict is not null)
            ack["copyId"] = conflict.CopyId.ToString("N");
        await Frame.WriteAsync(stream, ack, ct);
        return (result, conflict);
    }

    private static byte[]? ReadPendingBytes(
        Dictionary<string, PendingFile> pending, string sha)
    {
        foreach (var kv in pending)
        {
            if (string.Equals(kv.Key, sha, StringComparison.OrdinalIgnoreCase)
                && File.Exists(kv.Value.TmpPath))
                return File.ReadAllBytes(kv.Value.TmpPath);
        }
        return null;
    }

    private static void DiscardPending(Dictionary<string, PendingFile> pending)
    {
        foreach (var kv in pending)
        {
            try
            {
                if (File.Exists(kv.Value.TmpPath))
                    File.Delete(kv.Value.TmpPath);
            }
            catch { }
        }
        pending.Clear();
    }

    private static Task SendErr(Stream stream, string reason, CancellationToken ct) =>
        Frame.WriteAsync(stream, new JsonObject
        {
            ["t"] = "hello_err",
            ["reason"] = reason,
        }, ct);

    public static void BufferFileChunk(
        JsonObject msg, Dictionary<string, PendingFile> pending)
    {
        var type = msg["t"]!.GetValue<string>();
        var sha = msg["sha"]!.GetValue<string>();
        if (type == "file_begin")
        {
            long size = msg["size"]!.GetValue<long>();
            if (size < 0 || size > AttachmentIo.MaxAttachmentBytes)
                throw new IOException($"Недопустимый размер файла: {size}.");
            var tmp = Path.Combine(Path.GetTempPath(),
                "syncnote-" + Guid.NewGuid().ToString("N") + ".part");
            // Предочистка возможного мусора с тем же sha.
            if (pending.TryGetValue(sha, out var old) && File.Exists(old.TmpPath))
                try { File.Delete(old.TmpPath); } catch { }
            pending[sha] = new PendingFile
            {
                Name = msg["name"]!.GetValue<string>(),
                Mime = msg["mime"]!.GetValue<string>(),
                TmpPath = tmp,
                Size = 0,
            };
            File.Create(tmp).Dispose();
            return;
        }
        if (!pending.TryGetValue(sha, out var cur))
            throw new IOException("Чанк без file_begin.");
        if (type == "file_chunk")
        {
            var part = Convert.FromBase64String(msg["data_b64"]!.GetValue<string>());
            if (cur.Size + part.Length > AttachmentIo.MaxAttachmentBytes)
                throw new IOException("Файл превышает лимит.");
            using (var fs = new FileStream(cur.TmpPath, FileMode.Append, FileAccess.Write))
                fs.Write(part, 0, part.Length);
            cur.Size += part.Length;
            return;
        }
        // file_end: потоковая сверка sha256 временного файла.
        using (var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256))
        using (var fs = File.OpenRead(cur.TmpPath))
        {
            var buf = new byte[1024 * 1024];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                hash.AppendData(buf, 0, n);
            var hex = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (hex != sha.ToLowerInvariant())
            {
                try { File.Delete(cur.TmpPath); } catch { }
                pending.Remove(sha);
                throw new IOException("sha256 файла не сошлось.");
            }
        }
    }

    public static async Task SendNoteAsync(
        ISyncStore store, Stream stream, SyncNoteDto dto, CancellationToken ct)
    {
        var bySha = store.GetAttachments(dto.Id).ToDictionary(a => a.Sha256);
        foreach (var meta in dto.Attachments)
        {
            if (!bySha.TryGetValue(meta.Sha256, out var att))
                throw new IOException($"Нет локального файла {meta.FileName}.");
            var bytes = await File.ReadAllBytesAsync(
                Path.Combine(store.FilesDirectory, att.StoredName), ct);
            ServerLog.Line($"send file bytes={bytes.Length}");
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
