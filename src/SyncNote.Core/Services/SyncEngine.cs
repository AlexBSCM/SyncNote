using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SyncNote.Core.Interfaces;
using SyncNote.Core.Models;
using SyncNote.Core.Models.Dtos;
using SyncNote.Core.Utils;

namespace SyncNote.Core.Services;

public sealed class SyncResult
{
    public int Pushed { get; set; }
    public int Pulled { get; set; }
    public int Conflicts { get; set; }
}

// Симметричный движок сессии (см. docs/protocol_v2.md).
// Идемпотентен: повторная сессия безопасна; отката распределённых
// транзакций нет по дизайну — вместо них построчное применение.
public sealed class SyncEngine(
    INoteRepo notes,
    IFileRepo files,
    IFileIo io,
    ISyncStateStore sync,
    string deviceId,
    string tempDir)
{
    public const int SchemaVersion = 1;
    public const string FilesCap = "files-v1";
    public const long MaxFileBytes = 100L * 1024 * 1024;
    public const string ConflictSuffix = " (конфликт)";
    private const int ChunkBytes = 64 * 1024;

    private Dictionary<string, long> _peerNotes = new();
    private Dictionary<string, long> _peerFiles = new();
    private bool _peerFilesOn;
    private readonly Dictionary<string, PendingBytes> _pending = new();

    private sealed class PendingBytes
    {
        public string Path = "";
        public long Declared;
        public long Written;
    }

    public async Task<SyncResult> RunSessionAsync(
        ITransport t, bool isInitiator, CancellationToken ct = default,
        string? token = null, string? expectedToken = null)
    {
        Directory.CreateDirectory(tempDir);
        var res = new SyncResult();
        try
        {
            await HelloPhaseAsync(t, isInitiator, ct, token, expectedToken);
            await KnowledgePhaseAsync(t, isInitiator, ct);
            // Строгое чередование (иначе обе стороны упрутся в чтение):
            // инициатор пушит первым, респондер — вторым.
            if (isInitiator)
            {
                await PushPhaseAsync(t, res, ct);
                await t.SendAsync(@"{""t"":""sync_end""}", ct);
                await ReceivePhaseAsync(t, res, ct);
            }
            else
            {
                await ReceivePhaseAsync(t, res, ct);
                await PushPhaseAsync(t, res, ct);
                await t.SendAsync(@"{""t"":""sync_end""}", ct);
            }
            return res;
        }
        finally
        {
            foreach (var p in _pending.Values)
                try { if (File.Exists(p.Path)) File.Delete(p.Path); } catch { }
            _pending.Clear();
        }
    }

    // ---- фазы ----

    private async Task HelloPhaseAsync(
        ITransport t, bool initiator, CancellationToken ct,
        string? token, string? expectedToken)
    {
        if (initiator)
        {
            var hello = new JsonObject
            {
                ["t"] = "hello",
                ["deviceId"] = deviceId,
                ["schemaVersion"] = SchemaVersion,
                ["caps"] = new JsonArray(FilesCap),
            };
            if (token is not null)
                hello["token"] = token; // токен в лог не пишем нигде
            await t.SendAsync(hello.ToJsonString(), ct);
            var g = Frame(await t.ReceiveAsync(ct));
            if (Type(g) == "hello_error")
                throw new HelloRejectedException(
                    g["reason"]?.GetValue<string>() ?? "отказ без причины");
            if (Type(g) != "hello_ok")
                throw new IOException("Ожидался hello_ok.");
            CheckVersion(g);
            _peerFilesOn = Caps(g).Contains(FilesCap);
        }
        else
        {
            var h = Frame(await t.ReceiveAsync(ct));
            if (Type(h) != "hello")
                throw new IOException("Ожидался hello.");
            if (h["schemaVersion"]?.GetValue<int>() != SchemaVersion)
            {
                await t.SendAsync(new JsonObject
                {
                    ["t"] = "hello_error",
                    ["reason"] = "version mismatch",
                }.ToJsonString(), ct);
                CheckVersion(h); // бросит понятный IOException
            }
            if (expectedToken is not null
                && h["token"]?.GetValue<string>() != expectedToken)
            {
                await t.SendAsync(new JsonObject
                {
                    ["t"] = "hello_error",
                    ["reason"] = "bad token",
                }.ToJsonString(), ct);
                throw new HelloRejectedException("bad token");
            }
            _peerFilesOn = Caps(h).Contains(FilesCap);
            await t.SendAsync(new JsonObject
            {
                ["t"] = "hello_ok",
                ["deviceId"] = deviceId,
                ["schemaVersion"] = SchemaVersion,
                ["caps"] = new JsonArray(FilesCap),
            }.ToJsonString(), ct);
        }
    }

    private static void CheckVersion(JsonObject f)
    {
        if (f["schemaVersion"]?.GetValue<int>() != SchemaVersion)
            throw new IOException(
                $"Несовпадение schemaVersion (peer={f["schemaVersion"]}, local={SchemaVersion}).");
    }

    private static HashSet<string> Caps(JsonObject f)
    {
        var s = new HashSet<string>();
        if (f["caps"] is JsonArray arr)
            foreach (var x in arr)
                if (x?.GetValue<string>() is string c)
                    s.Add(c);
        return s;
    }

    private async Task KnowledgePhaseAsync(ITransport t, bool initiator, CancellationToken ct)
    {
        if (initiator)
        {
            await t.SendAsync((await BeginFrameAsync()).ToJsonString(), ct);
            var peer = Frame(await t.ReceiveAsync(ct));
            Expect(peer, "sync_begin");
            ReadKnowledge(peer);
        }
        else
        {
            var peer = Frame(await t.ReceiveAsync(ct));
            Expect(peer, "sync_begin");
            ReadKnowledge(peer);
            await t.SendAsync((await BeginFrameAsync()).ToJsonString(), ct);
        }
    }

    private async Task<JsonObject> BeginFrameAsync()
    {
        var kn = new JsonArray();
        foreach (var n in await notes.GetAllAsync(includeDeleted: true))
            kn.Add(new JsonObject { ["id"] = n.Id, ["rev"] = n.Rev });
        var fk = new JsonArray();
        if (_peerFilesOn)
            foreach (var f in await files.GetAllAsync(includeDeleted: true))
                fk.Add(new JsonObject { ["id"] = f.Id, ["rev"] = f.Rev });
        return new JsonObject
        {
            ["t"] = "sync_begin",
            ["knowledge"] = kn,
            ["fileKnowledge"] = fk,
            ["caps"] = new JsonArray(FilesCap),
        };
    }

    private void ReadKnowledge(JsonObject f)
    {
        _peerNotes = Map(f["knowledge"] as JsonArray);
        _peerFiles = Map(f["fileKnowledge"] as JsonArray);
    }

    private static Dictionary<string, long> Map(JsonArray? arr)
    {
        var d = new Dictionary<string, long>();
        if (arr is null)
            return d;
        foreach (var x in arr)
        {
            if (x is JsonObject o
                && o["id"]?.GetValue<string>() is string id)
                d[id] = o["rev"]?.GetValue<long>() ?? 0;
        }
        return d;
    }

    private async Task PushPhaseAsync(ITransport t, SyncResult res, CancellationToken ct)
    {
        // Правило отправки: пропуск, только если запись уже синкана
        // (rev <= собственный syncRev) И (пир её знает ИЛИ это копия).
        // Копии обратно не катаем: они по построению синканы и нового
        // для пира не несут. Поздний пир получает всё старое.
        foreach (var n in await notes.GetAllAsync(includeDeleted: true))
        {
            long ownSync = sync.GetSyncRev("note", n.Id);
            bool isCopy = n.Title.EndsWith(ConflictSuffix, StringComparison.Ordinal);
            if (n.Rev <= ownSync
                && (n.Rev <= PeerRev(_peerNotes, n.Id) || isCopy))
                continue;
            var dto = new NoteDto
            {
                Id = n.Id, Title = n.Title, Body = n.Body, Rev = n.Rev,
                UpdatedAt = n.UpdatedAt, Author = n.AuthorDeviceId,
                IsDeleted = n.IsDeleted,
                BaseRev = sync.GetSyncRev("note", n.Id),
            };
            await t.SendAsync(new JsonObject
            {
                ["t"] = "note_upsert",
                ["note"] = NoteNode(dto),
            }.ToJsonString(), ct);
            string ack = Ack(await t.ReceiveAsync(ct), dto.Id);
            res.Pushed++;
            if (ack is "Inserted" or "FastForwarded" or "NoOp")
                sync.SetSyncRev("note", n.Id, n.Rev);
        }
        if (!_peerFilesOn)
            return;
        foreach (var f in await files.GetAllAsync(includeDeleted: true))
        {
            long ownSync = sync.GetSyncRev("file", f.Id);
            bool isCopy = f.Name.EndsWith(ConflictSuffix, StringComparison.Ordinal);
            if (f.Rev <= ownSync
                && (f.Rev <= PeerRev(_peerFiles, f.Id) || isCopy))
                continue;
            string ack = await PushFileAsync(t, f, ct);
            res.Pushed++;
            if (ack is "Inserted" or "FastForwarded" or "NoOp")
                sync.SetSyncRev("file", f.Id, f.Rev);
        }
    }

    private async Task<string> PushFileAsync(ITransport t, FileEntry f, CancellationToken ct)
    {
        var dto = new FileDto
        {
            Id = f.Id, Name = f.Name, Mime = f.Mime, Size = f.SizeBytes,
            Sha256 = f.Sha256, Rev = f.Rev, UpdatedAt = f.UpdatedAt,
            Author = f.AuthorDeviceId, IsDeleted = f.IsDeleted,
            BaseRev = sync.GetSyncRev("file", f.Id),
        };
        // Tombstone байтов не требует (protocol_v2.md §4).
        if (!dto.IsDeleted)
        {
            await t.SendAsync(new JsonObject
            {
                ["t"] = "query_has_hash",
                ["sha256"] = dto.Sha256,
            }.ToJsonString(), ct);
            var resp = Frame(await t.ReceiveAsync(ct));
            if (Type(resp) != "hash_response")
                throw new IOException("Ожидался hash_response.");
            if (!resp["has"]?.GetValue<bool>() ?? true)
                await SendBytesAsync(t, dto, ct);
        }
        await t.SendAsync(new JsonObject
        {
            ["t"] = "file_register",
            ["file"] = FileNode(dto),
        }.ToJsonString(), ct);
        return Ack(await t.ReceiveAsync(ct), dto.Id);
    }

    private async Task SendBytesAsync(ITransport t, FileDto dto, CancellationToken ct)
    {
        string path = io.GetStoragePath(dto.Sha256);
        if (!File.Exists(path))
            throw new IOException($"Нет локального файла {dto.Name}.");
        await t.SendAsync(new JsonObject
        {
            ["t"] = "file_begin",
            ["sha"] = dto.Sha256,
            ["name"] = dto.Name,
            ["mime"] = dto.Mime,
            ["size"] = new FileInfo(path).Length,
        }.ToJsonString(), ct);
        using var fs = io.OpenRead(dto.Sha256);
        var buf = new byte[ChunkBytes];
        int n;
        while ((n = await fs.ReadAsync(buf.AsMemory(), ct)) > 0)
        {
            await t.SendAsync(new JsonObject
            {
                ["t"] = "file_chunk",
                ["sha"] = dto.Sha256,
                ["data_b64"] = Convert.ToBase64String(buf, 0, n),
            }.ToJsonString(), ct);
        }
        await t.SendAsync(new JsonObject
        {
            ["t"] = "file_end",
            ["sha"] = dto.Sha256,
        }.ToJsonString(), ct);
    }

    private async Task ReceivePhaseAsync(ITransport t, SyncResult res, CancellationToken ct)
    {
        while (true)
        {
            var msg = Frame(await t.ReceiveAsync(ct));
            string type = Type(msg);
            if (type == "sync_end")
                return;
            switch (type)
            {
                case "note_upsert":
                {
                    var dto = ParseNote(msg["note"]!.AsObject());
                    var (result, copyId) = await ApplyNoteAsync(dto);
                    res.Pulled++;
                    if (result == "Conflict")
                        res.Conflicts++;
                    await t.SendAsync(AppliedFrame(dto.Id, result, copyId), ct);
                    break;
                }
                case "query_has_hash":
                {
                    string sha = msg["sha256"]?.GetValue<string>() ?? "";
                    string norm = sha.ToLowerInvariant();
                    bool has = sync.HasLiveFileRef(norm) || io.ExistsInStorage(norm);
                    await t.SendAsync(new JsonObject
                    {
                        ["t"] = "hash_response",
                        ["sha256"] = sha,
                        ["has"] = has,
                    }.ToJsonString(), ct);
                    break;
                }
                case "file_begin":
                case "file_chunk":
                case "file_end":
                    BufferChunk(msg);
                    break;
                case "file_register":
                {
                    var dto = ParseFile(msg["file"]!.AsObject());
                    var (result, copyId) = await ApplyFileAsync(dto);
                    res.Pulled++;
                    if (result == "Conflict")
                        res.Conflicts++;
                    await t.SendAsync(AppliedFrame(dto.Id, result, copyId), ct);
                    break;
                }
                default:
                    break; // неизвестные кадры — молча мимо
            }
        }
    }

    private void BufferChunk(JsonObject msg)
    {
        string type = Type(msg);
        string sha = (msg["sha"]?.GetValue<string>() ?? "").ToLowerInvariant();
        if (type == "file_begin")
        {
            long size = msg["size"]?.GetValue<long>() ?? -1;
            if (size < 0 || size > MaxFileBytes)
                throw new IOException($"Недопустимый размер файла: {size}.");
            DropPending(sha);
            string tmp = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".part");
            _pending[sha] = new PendingBytes { Path = tmp, Declared = size };
            return;
        }
        if (!_pending.TryGetValue(sha, out var p))
            throw new IOException("Чанк без file_begin.");
        if (type == "file_chunk")
        {
            byte[] part = Convert.FromBase64String(msg["data_b64"]?.GetValue<string>() ?? "");
            using (var fs = new FileStream(p.Path, FileMode.Append, FileAccess.Write, FileShare.None))
                fs.Write(part, 0, part.Length);
            p.Written += part.Length;
            if (p.Written > MaxFileBytes)
                throw new IOException("Файл превышает лимит.");
            return;
        }
        // file_end: сверяем размер и sha временного файла.
        if (p.Written != p.Declared)
            throw new IOException($"Размер файла не сошёлся ({p.Written}/{p.Declared}).");
        using (var fs = new FileStream(p.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            string got = HashUtils.ComputeSha256(fs);
            if (!got.Equals(sha, StringComparison.OrdinalIgnoreCase))
                throw new IOException("sha256 файла не сошлось.");
        }
    }

    private void DropPending(string sha)
    {
        if (_pending.TryGetValue(sha, out var p))
        {
            try { if (File.Exists(p.Path)) File.Delete(p.Path); } catch { }
            _pending.Remove(sha);
        }
    }

    private string TakePending(string sha)
    {
        if (!_pending.TryGetValue(sha, out var p))
            throw new IOException($"Нет байтов для {sha}.");
        _pending.Remove(sha);
        return p.Path;
    }

    // ---- применение ----

    private async Task<(string result, string? copyId)> ApplyNoteAsync(NoteDto dto)
    {
        var local = await notes.GetByIdAsync(dto.Id);
        long s = sync.GetSyncRev("note", dto.Id);
        if (local is null)
        {
            await notes.InsertFullAsync(new NoteEntry
            {
                Id = dto.Id, Title = dto.Title, Body = dto.Body, Rev = dto.Rev,
                UpdatedAt = dto.UpdatedAt, AuthorDeviceId = dto.Author,
                IsDeleted = dto.IsDeleted,
            });
            sync.SetSyncRev("note", dto.Id, dto.Rev);
            return ("Inserted", null);
        }
        if (dto.Rev > local.Rev)
        {
            // Fast-forward безопасен, только если локально нет
            // несинхронизированных правок (иначе затёрли бы их:
            // входящая версия их не содержит). Любое расхождение
            // при грязной локальной копии — конфликт, не перезапись.
            if (local.Rev == s)
            {
                await notes.UpdateFullAsync(new NoteEntry
                {
                    Id = dto.Id, Title = dto.Title, Body = dto.Body, Rev = dto.Rev,
                    UpdatedAt = dto.UpdatedAt, AuthorDeviceId = dto.Author,
                    IsDeleted = dto.IsDeleted,
                });
                sync.SetSyncRev("note", dto.Id, dto.Rev);
                return ("FastForwarded", null);
            }
            return await NoteConflictAsync(dto, s);
        }
        if (dto.Rev == local.Rev)
        {
            if (local.Title == dto.Title && local.Body == dto.Body
                && local.IsDeleted == dto.IsDeleted)
            {
                sync.SetSyncRev("note", dto.Id, Math.Max(s, dto.Rev));
                return ("NoOp", null);
            }
            // Расхождение при равной ревизии — всегда через копию
            // (внутри — seen-dedup, чтобы не плодить дубликаты).
            return await NoteConflictAsync(dto, s);
        }
        return ("NoOp", null);
    }

    private async Task<(string, string?)> NoteConflictAsync(NoteDto dto, long s)
    {
        string h = NoteHash(dto);
        if (sync.NoteSeenConflict(dto.Id, dto.Rev, h))
        {
            sync.SetSyncRev("note", dto.Id, Math.Max(s, dto.Rev));
            return ("NoOp", null);
        }
        var copy = new NoteEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = dto.Title + ConflictSuffix,
            Body = dto.Body,
            Rev = 1,
            UpdatedAt = UtcNow(),
            AuthorDeviceId = deviceId,
            IsDeleted = false,
        };
        await notes.InsertFullAsync(copy);
        sync.SetSyncRev("note", copy.Id, 1);
        return ("Conflict", copy.Id);
    }

    private async Task<(string result, string? copyId)> ApplyFileAsync(FileDto dto)
    {
        var local = await files.GetByIdAsync(dto.Id);
        long s = sync.GetSyncRev("file", dto.Id);
        string norm = dto.Sha256.ToLowerInvariant();
        if (local is null)
        {
            if (!dto.IsDeleted)
                await EnsureBytesAsync(dto, norm);
            await files.InsertFullAsync(ToEntry(dto));
            sync.SetSyncRev("file", dto.Id, dto.Rev);
            return ("Inserted", null);
        }
        if (dto.Rev > local.Rev)
        {
            // Fast-forward безопасен, только если локально нет
            // несинхронизированных правок (иначе затёрли бы их:
            // входящая версия их не содержит). Любое расхождение
            // при грязной локальной копии — конфликт, не перезапись.
            if (local.Rev == s)
            {
                if (!dto.IsDeleted)
                    await EnsureBytesAsync(dto, norm);
                await files.UpdateFullAsync(ToEntry(dto));
                sync.SetSyncRev("file", dto.Id, dto.Rev);
                return ("FastForwarded", null);
            }
            return await FileConflictAsync(dto, norm, s);
        }
        if (dto.Rev == local.Rev)
        {
            if (SameFile(local, dto))
            {
                sync.SetSyncRev("file", dto.Id, Math.Max(s, dto.Rev));
                return ("NoOp", null);
            }
            // Расхождение при равной ревизии — через копию (внутри seen-dedup).
            return await FileConflictAsync(dto, norm, s);
        }
        return ("NoOp", null);
    }

    private async Task EnsureBytesAsync(FileDto dto, string norm)
    {
        if (io.ExistsInStorage(norm))
            return;
        string tmp = TakePending(norm);
        try
        {
            var (got, _) = await io.ImportAsync(tmp);
            if (!got.Equals(norm, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"sha256 файла {dto.Name} не сошлось.");
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private async Task<(string, string?)> FileConflictAsync(FileDto dto, string norm, long s)
    {
        string h = FileHash(dto);
        if (sync.NoteSeenConflict(dto.Id, dto.Rev, h))
        {
            sync.SetSyncRev("file", dto.Id, Math.Max(s, dto.Rev));
            return ("NoOp", null);
        }
        if (!dto.IsDeleted)
            await EnsureBytesAsync(dto, norm);
        var copy = new FileEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = dto.Name + ConflictSuffix,
            Mime = dto.Mime,
            SizeBytes = dto.Size,
            Sha256 = dto.Sha256,
            StoredName = dto.Sha256.ToLowerInvariant(),
            Rev = 1,
            UpdatedAt = UtcNow(),
            AuthorDeviceId = deviceId,
            IsDeleted = false,
        };
        await files.InsertFullAsync(copy);
        sync.SetSyncRev("file", copy.Id, 1);
        return ("Conflict", copy.Id);
    }

    private static FileEntry ToEntry(FileDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        Mime = dto.Mime,
        SizeBytes = dto.Size,
        Sha256 = dto.Sha256,
        StoredName = dto.Sha256.ToLowerInvariant(),
        Rev = dto.Rev,
        UpdatedAt = dto.UpdatedAt,
        AuthorDeviceId = dto.Author,
        IsDeleted = dto.IsDeleted,
    };

    private static bool SameFile(FileEntry l, FileDto d) =>
        l.Name == d.Name && l.Mime == d.Mime && l.SizeBytes == d.Size
        && string.Equals(l.Sha256, d.Sha256, StringComparison.OrdinalIgnoreCase)
        && l.IsDeleted == d.IsDeleted;

    internal static string NoteHash(NoteDto dto) =>
        ShaHex(dto.Title + "\0" + dto.Body + "\0" + (dto.IsDeleted ? "1" : "0"));

    internal static string FileHash(FileDto dto) =>
        ShaHex(dto.Name + "\0" + dto.Mime + "\0" + dto.Size + "\0"
            + dto.Sha256 + "\0" + (dto.IsDeleted ? "1" : "0"));

    internal static string ShaHex(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static string UtcNow() =>
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'");

    // ---- кадры ----

    private static JsonObject Frame(string json) =>
        JsonNode.Parse(json)!.AsObject();

    private static string Type(JsonObject f) =>
        f["t"]?.GetValue<string>() ?? "";

    private static void Expect(JsonObject f, string t)
    {
        if (Type(f) != t)
            throw new IOException($"Ожидался {t}.");
    }

    private static string Ack(string json, string id)
    {
        var a = Frame(json);
        if (Type(a) != "applied" || a["id"]?.GetValue<string>() != id)
            throw new IOException("Ожидался applied.");
        return a["result"]?.GetValue<string>() ?? "NoOp";
    }

    private static string AppliedFrame(string id, string result, string? copyId)
    {
        var o = new JsonObject
        {
            ["t"] = "applied",
            ["id"] = id,
            ["result"] = result,
        };
        if (copyId is not null)
            o["copyId"] = copyId;
        return o.ToJsonString();
    }

    private static long PeerRev(Dictionary<string, long> m, string id) =>
        m.TryGetValue(id, out long r) ? r : 0;

    private static JsonObject NoteNode(NoteDto d) => new()
    {
        ["id"] = d.Id,
        ["title"] = d.Title,
        ["body"] = d.Body,
        ["rev"] = d.Rev,
        ["updatedAt"] = d.UpdatedAt,
        ["author"] = d.Author,
        ["isDeleted"] = d.IsDeleted,
        ["baseRev"] = d.BaseRev,
    };

    private static JsonObject FileNode(FileDto d) => new()
    {
        ["id"] = d.Id,
        ["name"] = d.Name,
        ["mime"] = d.Mime,
        ["size"] = d.Size,
        ["sha256"] = d.Sha256,
        ["rev"] = d.Rev,
        ["updatedAt"] = d.UpdatedAt,
        ["author"] = d.Author,
        ["isDeleted"] = d.IsDeleted,
        ["baseRev"] = d.BaseRev,
    };

    private static NoteDto ParseNote(JsonObject o) => new()
    {
        Id = o["id"]?.GetValue<string>() ?? "",
        Title = o["title"]?.GetValue<string>() ?? "",
        Body = o["body"]?.GetValue<string>() ?? "",
        Rev = o["rev"]?.GetValue<long>() ?? 0,
        UpdatedAt = o["updatedAt"]?.GetValue<string>() ?? "",
        Author = o["author"]?.GetValue<string>() ?? "",
        IsDeleted = o["isDeleted"]?.GetValue<bool>() ?? false,
        BaseRev = o["baseRev"]?.GetValue<long>() ?? 0,
    };

    private static FileDto ParseFile(JsonObject o) => new()
    {
        Id = o["id"]?.GetValue<string>() ?? "",
        Name = o["name"]?.GetValue<string>() ?? "",
        Mime = o["mime"]?.GetValue<string>() ?? "application/octet-stream",
        Size = o["size"]?.GetValue<long>() ?? 0,
        Sha256 = o["sha256"]?.GetValue<string>() ?? "",
        Rev = o["rev"]?.GetValue<long>() ?? 0,
        UpdatedAt = o["updatedAt"]?.GetValue<string>() ?? "",
        Author = o["author"]?.GetValue<string>() ?? "",
        IsDeleted = o["isDeleted"]?.GetValue<bool>() ?? false,
        BaseRev = o["baseRev"]?.GetValue<long>() ?? 0,
    };
}
