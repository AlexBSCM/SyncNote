package com.syncnote

import android.content.ContentValues
import android.content.Context
import java.io.File

// CRUD + поиск (регистронезависимо, через norm-колонки).
class NotesRepository(ctx: Context) : SyncStore {
    private val db = NotesDbHelper(ctx)
    private val filesRoot: File = File(ctx.filesDir, "files").apply { mkdirs() }
    override fun filesDir(): File = filesRoot
    override fun deviceId(): String = db.deviceId()

    fun list(): List<Note> = query(null)

    fun search(q: String): List<Note> =
        if (q.isBlank()) list() else query(q.lowercase())

    private fun query(norm: String?): List<Note> {
        val out = mutableListOf<Note>()
        val sql = if (norm == null)
            "SELECT id, rev, title, body, updated_at, author_device FROM notes WHERE is_deleted=0 ORDER BY updated_at DESC"
        else
            "SELECT id, rev, title, body, updated_at, author_device FROM notes WHERE is_deleted=0 " +
                "AND (title_norm LIKE ? ESCAPE '\\' OR body_norm LIKE ? ESCAPE '\\') ORDER BY updated_at DESC"
        val args = if (norm == null) null else arrayOf(
            "%${norm.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")}%",
            "%${norm.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")}%")
        db.readableDatabase.rawQuery(sql, args).use { c ->
            while (c.moveToNext()) {
                out += Note(
                    id = c.getString(0), rev = c.getLong(1),
                    title = c.getString(2), body = c.getString(3),
                    updatedAt = c.getLong(4), authorDeviceId = c.getString(5))
            }
        }
        return out
    }

    fun get(id: String): Note? {
        db.readableDatabase.rawQuery(
            "SELECT id, rev, title, body, updated_at, author_device, is_deleted FROM notes WHERE id=?",
            arrayOf(id)).use { c ->
            if (!c.moveToFirst()) return null
            return Note(id = c.getString(0), rev = c.getLong(1),
                title = c.getString(2), body = c.getString(3),
                updatedAt = c.getLong(4), authorDeviceId = c.getString(5),
                isDeleted = c.getInt(6) != 0)
        }
    }

    fun add(title: String, body: String): Note {
        val n = Note(title = title, body = body,
            updatedAt = System.currentTimeMillis(), authorDeviceId = deviceId())
        val v = ContentValues().apply {
            put("id", n.id); put("rev", 1); put("title", title); put("body", body)
            put("updated_at", n.updatedAt); put("author_device", n.authorDeviceId)
            put("is_deleted", 0)
            put("title_norm", title.lowercase()); put("body_norm", body.lowercase())
        }
        db.writableDatabase.insertOrThrow("notes", null, v)
        return n
    }

    fun update(note: Note) {
        note.rev += 1
        note.updatedAt = System.currentTimeMillis()
        note.authorDeviceId = deviceId()
        val v = ContentValues().apply {
            put("rev", note.rev); put("title", note.title); put("body", note.body)
            put("updated_at", note.updatedAt); put("author_device", note.authorDeviceId)
            put("title_norm", note.title.lowercase()); put("body_norm", note.body.lowercase())
        }
        val rows = db.writableDatabase.update("notes", v, "id=? AND is_deleted=0",
            arrayOf(note.id))
        if (rows == 0) throw NoSuchElementException("Note ${note.id} not found")
    }

    fun delete(id: String): Boolean {
        val v = ContentValues().apply {
            put("is_deleted", 1)
            put("updated_at", System.currentTimeMillis())
        }
        // Ревизию увеличиваем отдельным запросом (атомарно в транзакции).
        db.writableDatabase.beginTransaction()
        try {
            db.writableDatabase.execSQL(
                "UPDATE notes SET rev = rev + 1 WHERE id=? AND is_deleted=0", arrayOf(id))
            val rows = db.writableDatabase.update("notes", v, "id=? AND is_deleted=0",
                arrayOf(id))
            db.writableDatabase.setTransactionSuccessful()
            return rows > 0
        } finally {
            db.writableDatabase.endTransaction()
        }
    }

    override fun checklist(noteId: String): List<ChecklistItem> {
        val out = mutableListOf<ChecklistItem>()
        db.readableDatabase.rawQuery(
            "SELECT id, note_id, position, text, is_checked FROM checklist_items WHERE note_id=? ORDER BY position",
            arrayOf(noteId)).use { c ->
            while (c.moveToNext())
                out += ChecklistItem(id = c.getString(0), noteId = c.getString(1),
                    position = c.getInt(2), text = c.getString(3),
                    isChecked = c.getInt(4) != 0)
        }
        return out
    }

    override fun attachments(noteId: String): List<Attachment> {
        val out = mutableListOf<Attachment>()
        db.readableDatabase.rawQuery(
            "SELECT id, note_id, file_name, mime, size, sha256, stored_name FROM attachments WHERE note_id=? ORDER BY file_name",
            arrayOf(noteId)).use { c ->
            while (c.moveToNext())
                out += Attachment(id = c.getString(0), noteId = c.getString(1),
                    fileName = c.getString(2), mimeType = c.getString(3),
                    sizeBytes = c.getLong(4), sha256 = c.getString(5),
                    storedName = c.getString(6))
        }
        return out
    }

    fun addChecklistItem(noteId: String, text: String): ChecklistItem {
        var pos = 0
        db.readableDatabase.rawQuery(
            "SELECT COALESCE(MAX(position), -1) + 1 FROM checklist_items WHERE note_id=?",
            arrayOf(noteId)).use { c ->
            if (c.moveToFirst()) pos = c.getInt(0)
        }
        val item = ChecklistItem(noteId = noteId, position = pos, text = text)
        val v = android.content.ContentValues().apply {
            put("id", item.id); put("note_id", noteId); put("position", pos)
            put("text", text); put("is_checked", 0)
        }
        db.writableDatabase.insertOrThrow("checklist_items", null, v)
        touchNote(noteId)
        return item
    }

    fun updateChecklistItem(item: ChecklistItem) {
        val v = android.content.ContentValues().apply {
            put("position", item.position); put("text", item.text)
            put("is_checked", if (item.isChecked) 1 else 0)
        }
        val rows = db.writableDatabase.update("checklist_items", v, "id=?",
            arrayOf(item.id))
        if (rows == 0) throw NoSuchElementException("Checklist item ${item.id} not found")
        touchNote(item.noteId)
    }

    fun deleteChecklistItem(itemId: String, noteId: String): Boolean {
        val rows = db.writableDatabase.delete("checklist_items", "id=?", arrayOf(itemId))
        if (rows > 0) touchNote(noteId)
        return rows > 0
    }

    override fun tryGet(id: String): Note? = get(id)

    override fun getSyncRev(noteId: String): Long {
        db.readableDatabase.rawQuery(
            "SELECT sync_rev FROM syncstate WHERE note_id=?", arrayOf(noteId)).use { c ->
            if (!c.moveToFirst()) return 0
            return c.getLong(0)
        }
    }

    override fun setSyncRev(noteId: String, rev: Long) {
        db.writableDatabase.execSQL(
            "INSERT INTO syncstate(note_id, sync_rev) VALUES(?, ?) " +
                "ON CONFLICT(note_id) DO UPDATE SET sync_rev=excluded.sync_rev",
            arrayOf(noteId, rev))
    }

    override fun exportAll(): List<SyncNoteDto> {
        val ids = mutableListOf<String>()
        db.readableDatabase.rawQuery("SELECT id FROM notes", null).use { c ->
            while (c.moveToNext()) ids += c.getString(0)
        }
        return ids.mapNotNull { id ->
            val n = get(id) ?: return@mapNotNull null
            SyncNoteDto(id = n.id, rev = n.rev, baseRev = getSyncRev(id),
                title = n.title, body = n.body, updatedAt = n.updatedAt,
                author = n.authorDeviceId, isDeleted = n.isDeleted,
                checklist = checklist(id).map {
                    ChecklistItemDto(position = it.position, text = it.text,
                        isChecked = it.isChecked)
                },
                attachments = attachments(id).map {
                    AttachmentMetaDto(fileName = it.fileName, mimeType = it.mimeType,
                        sizeBytes = it.sizeBytes, sha256 = it.sha256)
                })
        }
    }

    override fun importFull(dto: SyncNoteDto, fileBytes: (String) -> ByteArray?) {
        val w = db.writableDatabase
        w.beginTransaction()
        try {
            w.execSQL("""INSERT INTO notes(id, rev, title, body, updated_at, author_device,
                is_deleted, title_norm, body_norm) VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(id) DO UPDATE SET rev=excluded.rev, title=excluded.title,
                body=excluded.body, updated_at=excluded.updated_at,
                author_device=excluded.author_device, is_deleted=excluded.is_deleted,
                title_norm=excluded.title_norm, body_norm=excluded.body_norm""",
                arrayOf(dto.id, dto.rev, dto.title, dto.body, dto.updatedAt, dto.author,
                    if (dto.isDeleted) 1 else 0,
                    dto.title.lowercase(), dto.body.lowercase()))
            w.execSQL("DELETE FROM checklist_items WHERE note_id=?", arrayOf(dto.id))
            for (c in dto.checklist.sortedBy { it.position }) {
                w.execSQL("""INSERT INTO checklist_items(id, note_id, position, text, is_checked)
                    VALUES(?, ?, ?, ?, ?)""",
                    arrayOf(java.util.UUID.randomUUID().toString().replace("-", ""),
                        dto.id, c.position, c.text, if (c.isChecked) 1 else 0))
            }
            w.setTransactionSuccessful()
        } finally {
            w.endTransaction()
        }
        val want = dto.attachments.map { it.sha256 }.toSet()
        for (old in attachments(dto.id))
            if (old.sha256 !in want) deleteAttachmentFile(old)
        val have = attachments(dto.id).map { it.sha256 }.toSet()
        for (meta in dto.attachments) {
            if (meta.sha256 in have) continue
            val bytes = fileBytes(meta.sha256)
                ?: throw IllegalStateException("Нет байтов файла ${meta.fileName}.")
            addAttachmentBytes(dto.id, meta.fileName, meta.mimeType, bytes)
        }
    }

    fun addAttachmentBytes(noteId: String, fileName: String, mime: String, content: ByteArray): Attachment {
        if (content.size > 100L * 1024 * 1024)
            throw IllegalArgumentException("Файл $fileName превышает лимит 100 МБ.")
        val att = Attachment(noteId = noteId, fileName = fileName, mimeType = mime,
            sizeBytes = content.size.toLong())
        att.sha256 = sha256hex(content)
        att.storedName = "${att.id}_${fileName.replace(Regex("[/\\\\?%*:|\"<>.]"), "_")}"
        val dest = File(filesRoot, att.storedName)
        val tmp = File(filesRoot, att.storedName + ".tmp")
        tmp.writeBytes(content)
        if (!tmp.renameTo(dest)) {
            tmp.delete()
            throw java.io.IOException("Не удалось сохранить вложение $fileName.")
        }
        val v = android.content.ContentValues().apply {
            put("id", att.id); put("note_id", noteId); put("file_name", fileName)
            put("mime", mime); put("size", att.sizeBytes); put("sha256", att.sha256)
            put("stored_name", att.storedName)
        }
        db.writableDatabase.insertOrThrow("attachments", null, v)
        touchNote(noteId)
        return att
    }

    private fun deleteAttachmentFile(att: Attachment) {
        db.writableDatabase.delete("attachments", "id=?", arrayOf(att.id))
        try { File(filesRoot, att.storedName).delete() } catch (_: Exception) { }
    }

    private fun touchNote(noteId: String) {
        db.writableDatabase.execSQL(
            "UPDATE notes SET rev = rev + 1, updated_at=? WHERE id=?",
            arrayOf(System.currentTimeMillis().toString(), noteId))
    }

    private fun sha256hex(bytes: ByteArray): String {
        val md = java.security.MessageDigest.getInstance("SHA-256")
        return md.digest(bytes).joinToString("") { "%02x".format(it) }
    }
}
