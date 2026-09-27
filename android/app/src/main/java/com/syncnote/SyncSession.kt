package com.syncnote

import org.json.JSONArray
import org.json.JSONObject
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File
import java.net.Socket
import java.security.MessageDigest

data class SyncSessionResult(val pushed: Int, val pulled: Int, val conflicts: Int)

class HelloRejectedException(reason: String) : java.io.IOException("Сопряжение отклонено: $reason")

// Клиент сессии: зеркало SyncClient C#. Кадр: int32 BE длина + UTF-8 JSON.
class SyncSession(
    private val host: String,
    private val port: Int,
    private val deviceId: String,
    private val deviceName: String,
    private val token: String?
) {
    fun run(store: SyncStore, tmpDir: File? = null): SyncSessionResult {
        return tryHosts(store, tmpDir, listOf(host))
    }

    // Перебор узлов из QR (первый доступный). Таймаут соединения 8 секунд —
    // вместо вечного зависания сразу понятная ошибка.
    fun tryHosts(store: SyncStore, tmpDir: File? = null, hosts: List<String>): SyncSessionResult {
        var last: Exception? = null
        for (h in hosts) {
            try {
                Socket().use { sock ->
                    sock.connect(java.net.InetSocketAddress(h, port), 8000)
                    val def = tmpDir ?: File(System.getProperty("java.io.tmpdir") ?: "/data/local/tmp")
                    return runOverStreams(store,
                        DataInputStream(sock.getInputStream()),
                        DataOutputStream(sock.getOutputStream()), def)
                }
            } catch (e: HelloRejectedException) {
                throw e
            } catch (e: Exception) {
                last = e
            }
        }
        throw last ?: java.io.IOException("Нет доступных узлов.")
    }

    fun runOverStreams(
        store: SyncStore, inp: DataInputStream, out: DataOutputStream,
        tmpDir: File = File(System.getProperty("java.io.tmpdir") ?: "/data/local/tmp")
    ): SyncSessionResult {
        run {
            var pushed = 0
            var pulled = 0
            var conflicts = 0

            val hello = JSONObject()
                .put("t", "hello")
                .put("deviceId", deviceId)
                .put("deviceName", deviceName)
            if (token != null) hello.put("token", token)
            writeFrame(out, hello)
            val greet = readFrame(inp)
            if (greet.getString("t") != "hello_ok")
                throw HelloRejectedException(greet.optString("reason", "отказ без причины"))

            val knowledge = JSONArray()
            val all = store.exportAll()
            for (dto in all) knowledge.put(JSONObject()
                .put("id", dto.id).put("rev", dto.rev))
            writeFrame(out, JSONObject()
                .put("t", "sync_begin").put("knowledge", knowledge))

            for (dto in all) {
                if (store.tryGet(dto.id) != null && dto.rev <= store.getSyncRev(dto.id))
                    continue
                sendNote(store, out, dto)
                val ack = readFrame(inp) // applied
                pushed++
                if (ack.optString("result") in listOf("Inserted", "FastForwarded", "NoOp"))
                    store.setSyncRev(dto.id, dto.rev)
            }
            writeFrame(out, JSONObject().put("t", "sync_end"))

            val pending = mutableMapOf<String, PendingFile>()
            try {
            while (true) {
                val msg = readFrame(inp)
                when (msg.getString("t")) {
                    "sync_end" -> break
                    "file_begin", "file_chunk", "file_end" ->
                        bufferChunk(msg, pending, tmpDir)
                    "note_upsert" -> {
                        val dto = dtoFromJson(msg.getJSONObject("note"))
                        android.util.Log.i("SyncNote", "pull note rev=${dto.rev} files=${dto.attachments.size}")
                        val (result, _) = SyncEngine.apply(store, dto) { sha ->
                            pending[sha]?.file?.readBytes()
                        }
                        discardPending(pending)
                        pulled++
                        if (result == ApplyResult.Conflict) conflicts++
                        writeFrame(out, JSONObject()
                            .put("t", "applied")
                            .put("id", dto.id)
                            .put("result", result.name))
                    }
                    else -> throw java.io.IOException("Неожиданный тип ${msg.getString("t")}.")
                }
            }
            } finally {
                discardPending(pending)
            }
            return SyncSessionResult(pushed, pulled, conflicts)
        }
    }

    private data class PendingFile(
        val name: String, val mime: String, var file: File?, var size: Long = 0)

    private fun discardPending(pending: MutableMap<String, PendingFile>) {
        for ((_, p) in pending) {
            try { p.file?.delete() } catch (_: Exception) { }
        }
        pending.clear()
    }

    private fun bufferChunk(
        msg: JSONObject, pending: MutableMap<String, PendingFile>, tmpDir: File
    ) {
        val sha = msg.getString("sha")
        when (msg.getString("t")) {
            "file_begin" -> {
                val size = msg.getLong("size")
                if (size < 0 || size > 100L * 1024 * 1024)
                    throw java.io.IOException("Недопустимый размер файла: $size.")
                discardPending(pending.filterKeys { it == sha }
                    .toMutableMap().also { pending.remove(sha) })
                val tmp = File.createTempFile("sync-", ".part", tmpDir)
                pending[sha] = PendingFile(
                    msg.getString("name"), msg.getString("mime"), tmp, 0)
            }
            "file_chunk" -> {
                val cur = pending[sha] ?: throw java.io.IOException("Чанк без file_begin.")
                val part = android.util.Base64.decode(msg.getString("data_b64"), android.util.Base64.DEFAULT)
                cur.file!!.appendBytes(part)
                cur.size += part.size
                if (cur.size > 100L * 1024 * 1024)
                    throw java.io.IOException("Файл превышает лимит.")
            }
            "file_end" -> {
                val cur = pending[sha] ?: throw java.io.IOException("Чанк без file_begin.")
                val hex = sha256hex(cur.file!!.readBytes())
                if (!hex.equals(sha, ignoreCase = true))
                    throw java.io.IOException("sha256 файла не сошлось.")
            }
        }
    }

    private fun sendNote(store: SyncStore, out: DataOutputStream, dto: SyncNoteDto) {
        val bySha = store.attachments(dto.id).associateBy { it.sha256 }
        for (meta in dto.attachments) {
            val att = bySha[meta.sha256]
                ?: throw java.io.IOException("Нет локального файла ${meta.fileName}.")
            val bytes = File(store.filesDir(), att.storedName).readBytes()
            writeFrame(out, JSONObject()
                .put("t", "file_begin").put("sha", meta.sha256)
                .put("name", meta.fileName).put("mime", meta.mimeType)
                .put("size", bytes.size))
            val chunk = 64 * 1024
            var off = 0
            while (off < bytes.size) {
                val end = minOf(off + chunk, bytes.size)
                writeFrame(out, JSONObject()
                    .put("t", "file_chunk").put("sha", meta.sha256)
                    .put("data_b64", android.util.Base64.encodeToString(
                        bytes.copyOfRange(off, end), android.util.Base64.NO_WRAP)))
                off = end
            }
            writeFrame(out, JSONObject().put("t", "file_end").put("sha", meta.sha256))
        }
        writeFrame(out, JSONObject().put("t", "note_upsert").put("note", dtoToJson(dto)))
    }

    private fun writeFrame(out: DataOutputStream, obj: JSONObject) {
        val payload = obj.toString().toByteArray(Charsets.UTF_8)
        out.writeInt(payload.size)
        out.write(payload)
        out.flush()
    }

    private fun readFrame(inp: DataInputStream): JSONObject {
        val len = inp.readInt()
        if (len < 2 || len > 128 * 1024 * 1024)
            throw java.io.IOException("Некорректная длина кадра: $len.")
        val payload = ByteArray(len)
        inp.readFully(payload)
        return JSONObject(String(payload, Charsets.UTF_8))
    }

    private fun sha256hex(bytes: ByteArray): String {
        val md = MessageDigest.getInstance("SHA-256")
        return md.digest(bytes).joinToString("") { "%02x".format(it) }
    }

    private fun dtoToJson(d: SyncNoteDto): JSONObject {
        val check = JSONArray()
        for (c in d.checklist) check.put(JSONObject()
            .put("Position", c.position).put("Text", c.text).put("IsChecked", c.isChecked))
        val files = JSONArray()
        for (a in d.attachments) files.put(JSONObject()
            .put("FileName", a.fileName).put("MimeType", a.mimeType)
            .put("SizeBytes", a.sizeBytes).put("Sha256", a.sha256))
        return JSONObject()
            .put("Id", d.id).put("Rev", d.rev).put("BaseRev", d.baseRev)
            .put("Title", d.title).put("Body", d.body)
            .put("UpdatedAt", d.updatedAt).put("Author", d.author)
            .put("IsDeleted", d.isDeleted)
            .put("Checklist", check).put("Attachments", files)
    }

    private fun dtoFromJson(o: JSONObject): SyncNoteDto {
        // Каноническая форма id — без дефисов: иначе одна и та же заметка
        // с ПК (dashes) и с телефона дублируется.
        fun nid(s: String) = s.replace("-", "")
        val check = mutableListOf<ChecklistItemDto>()
        val ca = o.optJSONArray("Checklist") ?: JSONArray()
        for (i in 0 until ca.length()) {
            val c = ca.getJSONObject(i)
            check += ChecklistItemDto(position = c.getInt("Position"),
                text = c.getString("Text"), isChecked = c.getBoolean("IsChecked"))
        }
        val files = mutableListOf<AttachmentMetaDto>()
        val fa = o.optJSONArray("Attachments") ?: JSONArray()
        for (i in 0 until fa.length()) {
            val a = fa.getJSONObject(i)
            files += AttachmentMetaDto(fileName = a.getString("FileName"),
                mimeType = a.getString("MimeType"),
                sizeBytes = a.getLong("SizeBytes"), sha256 = a.getString("Sha256"))
        }
        return SyncNoteDto(id = nid(o.getString("Id")), rev = o.getLong("Rev"),
            baseRev = o.getLong("BaseRev"), title = o.getString("Title"),
            body = o.getString("Body"), updatedAt = o.getLong("UpdatedAt"),
            author = o.getString("Author"), isDeleted = o.getBoolean("IsDeleted"),
            checklist = check, attachments = files)
    }
}
