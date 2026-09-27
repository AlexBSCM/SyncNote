package com.syncnote

import android.content.ContentValues
import android.content.Context
import java.io.File

// CRUD + поиск (регистронезависимо, через norm-колонки).
class NotesRepository(ctx: Context) {
    private val db = NotesDbHelper(ctx)
    val filesDir: File = File(ctx.filesDir, "files").apply { mkdirs() }
    fun deviceId(): String = db.deviceId()

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

    fun checklist(noteId: String): List<ChecklistItem> {
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

    fun attachments(noteId: String): List<Attachment> {
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
}
