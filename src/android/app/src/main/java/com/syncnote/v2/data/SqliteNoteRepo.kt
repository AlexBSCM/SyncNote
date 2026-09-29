package com.syncnote.v2.data

import android.content.ContentValues
import com.syncnote.v2.domain.NoteEntry
import com.syncnote.v2.domain.NoteRepo
import java.io.File

// Сырой SQL без ORM. Зеркало C# SqliteNoteRepo: та же семантика
// (no-op без изменений, optimistic locking по rev, tombstone).
class SqliteNoteRepo(private val dbFile: File) : NoteRepo {
    override fun getById(id: String): NoteEntry? {
        openDb(dbFile).use { db ->
            db.rawQuery(
                "SELECT id, title, body, rev, updated_at, author_device," +
                    " is_deleted FROM notes WHERE id=?",
                arrayOf(id)).use { c ->
                if (!c.moveToFirst()) return null
                return read(c)
            }
        }
    }

    override fun getAll(includeDeleted: Boolean): List<NoteEntry> {
        val out = mutableListOf<NoteEntry>()
        openDb(dbFile).use { db ->
            val sql = "SELECT id, title, body, rev, updated_at, author_device," +
                " is_deleted FROM notes " +
                (if (includeDeleted) "" else "WHERE is_deleted=0 ") +
                "ORDER BY updated_at DESC"
            db.rawQuery(sql, null).use { c ->
                while (c.moveToNext()) out += read(c)
            }
        }
        return out
    }

    override fun create(note: NoteEntry): String {
        // Синк передаёт готовый id; UI — пустой (тогда генерируем).
        val id = if (note.id.isBlank()) newId() else note.id
        val now = utcNow()
        openDb(dbFile).use { db ->
            val deviceId = ensureDeviceId(db)
            val v = ContentValues().apply {
                put("id", id)
                put("title", note.title)
                put("body", note.body)
                put("rev", 1)
                put("updated_at", now)
                put("author_device", deviceId)
                put("is_deleted", 0)
            }
            db.insertOrThrow("notes", null, v)
        }
        return id
    }

    override fun update(note: NoteEntry) {
        openDb(dbFile).use { db ->
            val cur = getById(note.id)
                ?: throw NoSuchElementException("Note ${note.id} not found.")
            if (cur.title == note.title && cur.body == note.body) return // no-op
            val deviceId = ensureDeviceId(db)
            val v = ContentValues().apply {
                put("title", note.title)
                put("body", note.body)
                put("rev", note.rev + 1)
                put("updated_at", utcNow())
                put("author_device", deviceId)
            }
            // Optimistic locking: пишем только если ревизия не ушла вперёд.
            val rows = db.update("notes", v, "id=? AND rev=? AND is_deleted=0",
                arrayOf(note.id, note.rev.toString()))
            if (rows == 0) throw IllegalStateException(
                "Note ${note.id} changed concurrently (expected rev ${note.rev}).")
        }
    }

    override fun softDelete(id: String) {
        openDb(dbFile).use { db ->
            val v = ContentValues().apply {
                put("is_deleted", 1)
                put("updated_at", utcNow())
            }
            // rev = rev + 1 нельзя выразить через ContentValues —
            // сырой SQL допустим и здесь.
            db.execSQL(
                "UPDATE notes SET is_deleted=1, rev=rev+1, updated_at=? " +
                    "WHERE id=? AND is_deleted=0",
                arrayOf(v.getAsString("updated_at"), id))
        }
    }

    override fun insertFull(e: NoteEntry) {
        openDb(dbFile).use { db ->
            val v = ContentValues().apply {
                put("id", e.id)
                put("title", e.title)
                put("body", e.body)
                put("rev", e.rev)
                put("updated_at", e.updatedAt)
                put("author_device", e.authorDeviceId)
                put("is_deleted", if (e.isDeleted) 1 else 0)
            }
            db.insertOrThrow("notes", null, v)
        }
    }

    override fun updateFull(e: NoteEntry) {
        openDb(dbFile).use { db ->
            val v = ContentValues().apply {
                put("title", e.title)
                put("body", e.body)
                put("rev", e.rev)
                put("updated_at", e.updatedAt)
                put("author_device", e.authorDeviceId)
                put("is_deleted", if (e.isDeleted) 1 else 0)
            }
            db.update("notes", v, "id=?", arrayOf(e.id))
        }
    }

    private fun read(c: android.database.Cursor): NoteEntry = NoteEntry(
        id = c.getString(0),
        title = c.getString(1),
        body = c.getString(2),
        rev = c.getLong(3),
        updatedAt = c.getString(4),
        authorDeviceId = c.getString(5),
        isDeleted = c.getInt(6) != 0
    )
}
