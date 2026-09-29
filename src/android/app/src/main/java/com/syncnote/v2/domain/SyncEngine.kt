package com.syncnote.v2.domain

import com.syncnote.v2.data.HashUtils
import com.syncnote.v2.network.FrameTransport
import com.syncnote.v2.network.SyncNetworkException
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.util.Base64
import java.util.UUID

data class SyncResult(var pushed: Int = 0, var pulled: Int = 0, var conflicts: Int = 0)

// Симметричный движок сессии — построчный порт C# SyncEngine
// (см. docs/protocol_v2.md). Та же фазовость, те же правила
// fast-forward/конфликтов/дедупа, tombstone без байтов.
class SyncEngine(
    private val notes: NoteRepo,
    private val files: FileRepo,
    private val io: FileIo,
    private val sync: SyncState,
    private val deviceId: String,
    private val tempDir: File
) {
    companion object {
        const val SCHEMA_VERSION = 1
        const val FILES_CAP = "files-v1"
        const val MAX_FILE_BYTES = 100L * 1024 * 1024
        const val CONFLICT_SUFFIX = " (конфликт)"
        const val CHUNK_BYTES = 64 * 1024

        fun noteHash(d: NoteDto): String = shaHex(
            "${d.title}\u0000${d.body}\u0000${if (d.isDeleted) "1" else "0"}")

        fun fileHash(d: FileDto): String = shaHex(
            "${d.name}\u0000${d.mime}\u0000${d.size}\u0000" +
                "${d.sha256}\u0000${if (d.isDeleted) "1" else "0"}")

        private fun shaHex(s: String): String =
            HashUtils.sha256Hex(s.toByteArray(Charsets.UTF_8))

        fun utcNow(): String = java.time.Instant.now()
            .truncatedTo(java.time.temporal.ChronoUnit.SECONDS).toString()
    }

    private var peerNotes: Map<String, Long> = emptyMap()
    private var peerFiles: Map<String, Long> = emptyMap()
    private var peerFilesOn = false
    private data class PendingBytes(var path: String, var declared: Long, var written: Long = 0)
    private val pending = mutableMapOf<String, PendingBytes>()

    fun runSession(t: FrameTransport, isInitiator: Boolean): SyncResult {
        tempDir.mkdirs()
        val res = SyncResult()
        try {
            helloPhase(t, isInitiator)
            knowledgePhase(t, isInitiator)
            if (isInitiator) {
                pushPhase(t, res)
                t.send(obj("t" to "sync_end").toString())
                receivePhase(t, res)
            } else {
                receivePhase(t, res)
                pushPhase(t, res)
                t.send(obj("t" to "sync_end").toString())
            }
            return res
        } finally {
            for (p in pending.values) {
                try { File(p.path).let { if (it.exists()) it.delete() } } catch (_: Exception) { }
            }
            pending.clear()
        }
    }

    // ---- фазы ----

    private fun helloPhase(t: FrameTransport, initiator: Boolean) {
        if (initiator) {
            t.send(obj("t" to "hello", "deviceId" to deviceId,
                "schemaVersion" to SCHEMA_VERSION, "caps" to arr(FILES_CAP)).toString())
            val g = frame(t.receive() ?: throw SyncNetworkException("Пир закрылся на hello."))
            if (g.type() != "hello_ok") throw IOException("Ожидался hello_ok.")
            checkVersion(g)
            peerFilesOn = caps(g).contains(FILES_CAP)
        } else {
            val h = frame(t.receive() ?: throw SyncNetworkException("Пир закрылся на hello."))
            if (h.type() != "hello") throw IOException("Ожидался hello.")
            checkVersion(h)
            peerFilesOn = caps(h).contains(FILES_CAP)
            t.send(obj("t" to "hello_ok", "deviceId" to deviceId,
                "schemaVersion" to SCHEMA_VERSION, "caps" to arr(FILES_CAP)).toString())
        }
    }

    private fun checkVersion(f: JSONObject) {
        if (f.optInt("schemaVersion", -1) != SCHEMA_VERSION)
            throw IOException("Несовпадение schemaVersion (peer=${f.optInt("schemaVersion", -1)}).")
    }

    private fun caps(f: JSONObject): Set<String> {
        val s = mutableSetOf<String>()
        val a = f.optJSONArray("caps") ?: return s
        for (i in 0 until a.length()) s += a.optString(i)
        return s
    }

    private fun knowledgePhase(t: FrameTransport, initiator: Boolean) {
        if (initiator) {
            t.send(beginFrame().toString())
            val peer = frame(t.receive() ?: throw SyncNetworkException("Пир закрылся."))
            expect(peer, "sync_begin")
            readKnowledge(peer)
        } else {
            val peer = frame(t.receive() ?: throw SyncNetworkException("Пир закрылся."))
            expect(peer, "sync_begin")
            readKnowledge(peer)
            t.send(beginFrame().toString())
        }
    }

    private fun beginFrame(): JSONObject {
        val kn = JSONArray()
        for (n in notes.getAll(includeDeleted = true))
            kn.put(JSONObject().put("id", n.id).put("rev", n.rev))
        val fk = JSONArray()
        if (peerFilesOn)
            for (f in files.getAll(includeDeleted = true))
                fk.put(JSONObject().put("id", f.id).put("rev", f.rev))
        return obj("t" to "sync_begin", "knowledge" to kn,
            "fileKnowledge" to fk, "caps" to arr(FILES_CAP))
    }

    private fun readKnowledge(f: JSONObject) {
        peerNotes = map(f.optJSONArray("knowledge"))
        peerFiles = map(f.optJSONArray("fileKnowledge"))
    }

    private fun map(a: JSONArray?): Map<String, Long> {
        val d = mutableMapOf<String, Long>()
        if (a == null) return d
        for (i in 0 until a.length()) {
            val o = a.optJSONObject(i) ?: continue
            d[o.optString("id")] = o.optLong("rev", 0)
        }
        return d
    }

    private fun pushPhase(t: FrameTransport, res: SyncResult) {
        for (n in notes.getAll(includeDeleted = true)) {
            val ownSync = sync.getSyncRev("note", n.id)
            val isCopy = n.title.endsWith(CONFLICT_SUFFIX)
            if (n.rev <= ownSync && (n.rev <= peerNotes[n.id] ?: 0 || isCopy))
                continue
            val dto = NoteDto(n.id, n.title, n.body, n.rev,
                n.updatedAt, n.authorDeviceId, n.isDeleted, ownSync)
            t.send(obj("t" to "note_upsert", "note" to noteNode(dto)).toString())
            val ack = ack(t.receive() ?: throw SyncNetworkException("Пир закрылся."), dto.id)
            res.pushed++
            if (ack == "Inserted" || ack == "FastForwarded" || ack == "NoOp")
                sync.setSyncRev("note", n.id, n.rev)
        }
        if (!peerFilesOn) return
        for (f in files.getAll(includeDeleted = true)) {
            val ownSync = sync.getSyncRev("file", f.id)
            val isCopy = f.name.endsWith(CONFLICT_SUFFIX)
            if (f.rev <= ownSync && (f.rev <= peerFiles[f.id] ?: 0 || isCopy))
                continue
            val ack = pushFile(t, f)
            res.pushed++
            if (ack == "Inserted" || ack == "FastForwarded" || ack == "NoOp")
                sync.setSyncRev("file", f.id, f.rev)
        }
    }

    private fun pushFile(t: FrameTransport, f: FileEntry): String {
        val dto = FileDto(f.id, f.name, f.mime, f.sizeBytes, f.sha256, f.rev,
            f.updatedAt, f.authorDeviceId, f.isDeleted,
            sync.getSyncRev("file", f.id))
        if (!dto.isDeleted) {
            t.send(obj("t" to "query_has_hash", "sha256" to dto.sha256).toString())
            val resp = frame(t.receive() ?: throw SyncNetworkException("Пир закрылся."))
            if (resp.type() != "hash_response")
                throw IOException("Ожидался hash_response.")
            if (!resp.optBoolean("has", true)) sendBytes(t, dto)
        }
        t.send(obj("t" to "file_register", "file" to fileNode(dto)).toString())
        return ack(t.receive() ?: throw SyncNetworkException("Пир закрылся."), dto.id)
    }

    private fun sendBytes(t: FrameTransport, dto: FileDto) {
        val path = io.getStoragePath(dto.sha256)
        if (!path.exists()) throw IOException("Нет локального файла ${dto.name}.")
        t.send(obj("t" to "file_begin", "sha" to dto.sha256, "name" to dto.name,
            "mime" to dto.mime, "size" to path.length()).toString())
        path.inputStream().buffered().use { fs ->
            val buf = ByteArray(CHUNK_BYTES)
            while (true) {
                val n = fs.read(buf)
                if (n <= 0) break
                t.send(obj("t" to "file_chunk", "sha" to dto.sha256,
                    "data_b64" to Base64.getEncoder().encodeToString(buf.copyOf(n))).toString())
            }
        }
        t.send(obj("t" to "file_end", "sha" to dto.sha256).toString())
    }

    private fun receivePhase(t: FrameTransport, res: SyncResult) {
        while (true) {
            val msg = frame(t.receive() ?: throw SyncNetworkException("Пир закрылся."));
            when (msg.type()) {
                "sync_end" -> return
                "note_upsert" -> {
                    val dto = parseNote(msg.getJSONObject("note"))
                    val (result, copyId) = applyNote(dto)
                    res.pulled++
                    if (result == "Conflict") res.conflicts++
                    t.send(appliedFrame(dto.id, result, copyId))
                }
                "query_has_hash" -> {
                    val sha = msg.optString("sha256", "")
                    val norm = sha.lowercase()
                    val has = sync.hasLiveFileRef(norm) || io.existsInStorage(norm)
                    t.send(obj("t" to "hash_response",
                        "sha256" to sha, "has" to has).toString())
                }
                "file_begin", "file_chunk", "file_end" -> bufferChunk(msg)
                "file_register" -> {
                    val dto = parseFile(msg.getJSONObject("file"))
                    val (result, copyId) = applyFile(dto)
                    res.pulled++
                    if (result == "Conflict") res.conflicts++
                    t.send(appliedFrame(dto.id, result, copyId))
                }
                else -> { } // неизвестные кадры — молча мимо
            }
        }
    }

    private fun bufferChunk(msg: JSONObject) {
        val type = msg.type()
        val sha = msg.optString("sha", "").lowercase()
        if (type == "file_begin") {
            val size = msg.optLong("size", -1)
            if (size < 0 || size > MAX_FILE_BYTES)
                throw IOException("Недопустимый размер файла: $size.")
            dropPending(sha)
            val tmp = File.createTempFile("sync-", ".part", tempDir)
            pending[sha] = PendingBytes(tmp.absolutePath, size)
            return
        }
        val p = pending[sha] ?: throw IOException("Чанк без file_begin.")
        if (type == "file_chunk") {
            val part = Base64.getDecoder().decode(msg.optString("data_b64", ""))
            File(p.path).appendBytes(part)
            p.written += part.size
            if (p.written > MAX_FILE_BYTES) throw IOException("Файл превышает лимит.")
            return
        }
        // file_end: сверяем размер и sha.
        if (p.written != p.declared)
            throw IOException("Размер файла не сошёлся (${p.written}/${p.declared}).")
        val got = HashUtils.sha256Hex(File(p.path).inputStream())
        if (!got.equals(sha, ignoreCase = true))
            throw IOException("sha256 файла не сошлось.")
    }

    private fun dropPending(sha: String) {
        pending.remove(sha)?.let {
            try { File(it.path).let { f -> if (f.exists()) f.delete() } } catch (_: Exception) { }
        }
    }

    private fun takePending(sha: String): String =
        (pending.remove(sha) ?: throw IOException("Нет байтов для $sha.")).path

    // ---- применение ----

    private fun applyNote(dto: NoteDto): Pair<String, String?> {
        val local = notes.getById(dto.id)
        val s = sync.getSyncRev("note", dto.id)
        if (local == null) {
            notes.insertFull(dto.toEntry())
            sync.setSyncRev("note", dto.id, dto.rev)
            return "Inserted" to null
        }
        if (dto.rev > local.rev) {
            if (local.rev == s) {
                notes.updateFull(dto.toEntry())
                sync.setSyncRev("note", dto.id, dto.rev)
                return "FastForwarded" to null
            }
            return noteConflict(dto, s)
        }
        if (dto.rev == local.rev) {
            if (local.title == dto.title && local.body == dto.body
                && local.isDeleted == dto.isDeleted) {
                sync.setSyncRev("note", dto.id, maxOf(s, dto.rev))
                return "NoOp" to null
            }
            return noteConflict(dto, s)
        }
        return "NoOp" to null
    }

    private fun noteConflict(dto: NoteDto, s: Long): Pair<String, String?> {
        val h = noteHash(dto)
        if (sync.noteSeenConflict(dto.id, dto.rev, h)) {
            sync.setSyncRev("note", dto.id, maxOf(s, dto.rev))
            return "NoOp" to null
        }
        val copy = NoteEntry(
            id = UUID.randomUUID().toString().replace("-", ""),
            title = dto.title + CONFLICT_SUFFIX, body = dto.body, rev = 1,
            updatedAt = utcNow(), authorDeviceId = deviceId, isDeleted = false)
        notes.insertFull(copy)
        sync.setSyncRev("note", copy.id, 1)
        return "Conflict" to copy.id
    }

    private fun applyFile(dto: FileDto): Pair<String, String?> {
        val local = files.getById(dto.id)
        val s = sync.getSyncRev("file", dto.id)
        val norm = dto.sha256.lowercase()
        if (local == null) {
            if (!dto.isDeleted) ensureBytes(dto, norm)
            files.insertFull(dto.toEntry())
            sync.setSyncRev("file", dto.id, dto.rev)
            return "Inserted" to null
        }
        if (dto.rev > local.rev) {
            if (local.rev == s) {
                if (!dto.isDeleted) ensureBytes(dto, norm)
                files.updateFull(dto.toEntry())
                sync.setSyncRev("file", dto.id, dto.rev)
                return "FastForwarded" to null
            }
            return fileConflict(dto, norm, s)
        }
        if (dto.rev == local.rev) {
            if (local.name == dto.name && local.mime == dto.mime
                && local.sizeBytes == dto.size
                && local.sha256.equals(dto.sha256, ignoreCase = true)
                && local.isDeleted == dto.isDeleted) {
                sync.setSyncRev("file", dto.id, maxOf(s, dto.rev))
                return "NoOp" to null
            }
            return fileConflict(dto, norm, s)
        }
        return "NoOp" to null
    }

    private fun ensureBytes(dto: FileDto, norm: String) {
        if (io.existsInStorage(norm)) return
        val tmp = takePending(norm)
        try {
            val (got, _) = io.import(tmp)
            if (!got.equals(norm, ignoreCase = true))
                throw IOException("sha256 файла ${dto.name} не сошлось.")
        } finally {
            try { File(tmp).let { if (it.exists()) it.delete() } } catch (_: Exception) { }
        }
    }

    private fun fileConflict(dto: FileDto, norm: String, s: Long): Pair<String, String?> {
        val h = fileHash(dto)
        if (sync.noteSeenConflict(dto.id, dto.rev, h)) {
            sync.setSyncRev("file", dto.id, maxOf(s, dto.rev))
            return "NoOp" to null
        }
        if (!dto.isDeleted) ensureBytes(dto, norm)
        val copy = FileEntry(
            id = UUID.randomUUID().toString().replace("-", ""),
            name = dto.name + CONFLICT_SUFFIX, mime = dto.mime, sizeBytes = dto.size,
            sha256 = dto.sha256, storedName = dto.sha256.lowercase(), rev = 1,
            updatedAt = utcNow(), authorDeviceId = deviceId, isDeleted = false)
        files.insertFull(copy)
        sync.setSyncRev("file", copy.id, 1)
        return "Conflict" to copy.id
    }

    // ---- кадры ----

    private fun obj(vararg kv: Pair<String, Any>): JSONObject {
        val o = JSONObject()
        for ((k, v) in kv) o.put(k, v)
        return o
    }

    private fun arr(vararg vs: String): JSONArray {
        val a = JSONArray()
        for (v in vs) a.put(v)
        return a
    }

    private fun frame(json: String): JSONObject = JSONObject(json)

    private fun JSONObject.type(): String = optString("t", "")

    private fun expect(f: JSONObject, t: String) {
        if (f.type() != t) throw IOException("Ожидался $t.")
    }

    private fun ack(json: String, id: String): String {
        val a = frame(json)
        if (a.type() != "applied" || a.optString("id") != id)
            throw IOException("Ожидался applied.")
        return a.optString("result", "NoOp")
    }

    private fun appliedFrame(id: String, result: String, copyId: String?): String {
        val o = obj("t" to "applied", "id" to id, "result" to result)
        if (copyId != null) o.put("copyId", copyId)
        return o.toString()
    }

    private fun peerRev(m: Map<String, Long>, id: String): Long = m[id] ?: 0

    private fun noteNode(d: NoteDto): JSONObject = obj(
        "id" to d.id, "title" to d.title, "body" to d.body, "rev" to d.rev,
        "updatedAt" to d.updatedAt, "author" to d.author,
        "isDeleted" to d.isDeleted, "baseRev" to d.baseRev)

    private fun fileNode(d: FileDto): JSONObject = obj(
        "id" to d.id, "name" to d.name, "mime" to d.mime, "size" to d.size,
        "sha256" to d.sha256, "rev" to d.rev, "updatedAt" to d.updatedAt,
        "author" to d.author, "isDeleted" to d.isDeleted, "baseRev" to d.baseRev)

    private fun parseNote(o: JSONObject) = NoteDto(
        id = o.optString("id"), title = o.optString("title"),
        body = o.optString("body"), rev = o.optLong("rev"),
        updatedAt = o.optString("updatedAt"), author = o.optString("author"),
        isDeleted = o.optBoolean("isDeleted"), baseRev = o.optLong("baseRev"))

    private fun parseFile(o: JSONObject) = FileDto(
        id = o.optString("id"), name = o.optString("name"),
        mime = o.optString("mime", "application/octet-stream"),
        size = o.optLong("size"), sha256 = o.optString("sha256"),
        rev = o.optLong("rev"), updatedAt = o.optString("updatedAt"),
        author = o.optString("author"), isDeleted = o.optBoolean("isDeleted"),
        baseRev = o.optLong("baseRev"))

    private fun NoteDto.toEntry() = NoteEntry(id, title, body, rev,
        updatedAt, author, isDeleted)

    private fun FileDto.toEntry() = FileEntry(id, name, mime, size, sha256,
        sha256.lowercase(), rev, updatedAt, author, isDeleted)
}