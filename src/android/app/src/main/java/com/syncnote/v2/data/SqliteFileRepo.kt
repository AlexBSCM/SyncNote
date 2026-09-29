package com.syncnote.v2.data

import android.content.ContentValues
import com.syncnote.v2.domain.FileEntry
import java.io.File

// Строки таблицы files. Байты — через AndroidFileIo (content-addressed).
// Зеркало C# SqliteFileRepo, включая чистку сироты при упавшем INSERT.
class SqliteFileRepo(
    private val dbFile: File,
    private val io: AndroidFileIo
) {
    fun getById(id: String): FileEntry? {
        openDb(dbFile).use { db ->
            db.rawQuery(
                "SELECT id, name, mime, size, sha256, stored_name, rev," +
                    " updated_at, author_device, is_deleted" +
                    " FROM files WHERE id=?", arrayOf(id)).use { c ->
                if (!c.moveToFirst()) return null
                return read(c)
            }
        }
    }

    fun getAll(includeDeleted: Boolean = false): List<FileEntry> {
        val out = mutableListOf<FileEntry>()
        openDb(dbFile).use { db ->
            val sql = "SELECT id, name, mime, size, sha256, stored_name, rev," +
                " updated_at, author_device, is_deleted FROM files " +
                (if (includeDeleted) "" else "WHERE is_deleted=0 ") +
                "ORDER BY updated_at DESC"
            db.rawQuery(sql, null).use { c ->
                while (c.moveToNext()) out += read(c)
            }
        }
        return out
    }

    fun addFile(metadata: FileEntry, sourcePath: String): String {
        val (sha, size) = io.import(sourcePath)
        val id = newId()
        val now = utcNow()
        try {
            openDb(dbFile).use { db ->
                val deviceId = ensureDeviceId(db)
                val v = ContentValues().apply {
                    put("id", id)
                    put("name", metadata.name)
                    put("mime", metadata.mime)
                    put("size", size)
                    put("sha256", sha)
                    put("stored_name", sha)
                    put("rev", 1)
                    put("updated_at", now)
                    put("author_device", deviceId)
                    put("is_deleted", 0)
                }
                db.insertOrThrow("files", null, v)
            }
            return id
        } catch (e: Exception) {
            if (!hasLiveSha(sha)) {
                try { io.deleteFromStorage(sha) } catch (_: Exception) { }
            }
            throw e
        }
    }

    fun softDelete(id: String) {
        openDb(dbFile).use { db ->
            db.execSQL(
                "UPDATE files SET is_deleted=1, rev=rev+1, updated_at=? " +
                    "WHERE id=? AND is_deleted=0",
                arrayOf(utcNow(), id))
        }
    }

    private fun hasLiveSha(sha: String): Boolean {
        openDb(dbFile).use { db ->
            db.rawQuery(
                "SELECT 1 FROM files WHERE sha256=? AND is_deleted=0 LIMIT 1",
                arrayOf(sha.lowercase())).use { c ->
                return c.count > 0
            }
        }
    }

    private fun read(c: android.database.Cursor): FileEntry = FileEntry(
        id = c.getString(0),
        name = c.getString(1),
        mime = c.getString(2),
        sizeBytes = c.getLong(3),
        sha256 = c.getString(4),
        storedName = c.getString(5),
        rev = c.getLong(6),
        updatedAt = c.getString(7),
        authorDeviceId = c.getString(8),
        isDeleted = c.getInt(9) != 0
    )
}
