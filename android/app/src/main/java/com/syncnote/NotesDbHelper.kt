package com.syncnote

import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper

// Локальное хранилище (зеркало схемы Core: notes + norms, checklist,
// attachments, syncstate, seen_conflicts, keyvalue). Файлы — в filesDir.
class NotesDbHelper(ctx: Context) :
    SQLiteOpenHelper(ctx, "notes.db", null, 8) {

    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL("CREATE TABLE keyvalue(key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        db.execSQL("""CREATE TABLE notes(
            id TEXT PRIMARY KEY, rev INTEGER NOT NULL,
            title TEXT NOT NULL, body TEXT NOT NULL,
            updated_at INTEGER NOT NULL, author_device TEXT NOT NULL,
            is_deleted INTEGER NOT NULL DEFAULT 0,
            title_norm TEXT NOT NULL DEFAULT '', body_norm TEXT NOT NULL DEFAULT '')""")
        db.execSQL("""CREATE TABLE checklist_items(
            id TEXT PRIMARY KEY, note_id TEXT NOT NULL REFERENCES notes(id),
            position INTEGER NOT NULL, text TEXT NOT NULL,
            is_checked INTEGER NOT NULL DEFAULT 0)""")
        db.execSQL("CREATE INDEX idx_checklist_note ON checklist_items(note_id, position)")
        db.execSQL("""CREATE TABLE attachments(
            id TEXT PRIMARY KEY, note_id TEXT NOT NULL REFERENCES notes(id),
            file_name TEXT NOT NULL, mime TEXT NOT NULL,
            size INTEGER NOT NULL, sha256 TEXT NOT NULL, stored_name TEXT NOT NULL)""")
        db.execSQL("CREATE INDEX idx_attachments_note ON attachments(note_id)")
        db.execSQL("CREATE TABLE syncstate(note_id TEXT PRIMARY KEY, sync_rev INTEGER NOT NULL DEFAULT 0)")
        db.execSQL("""CREATE TABLE seen_conflicts(
            note_id TEXT NOT NULL, rev INTEGER NOT NULL,
            content_hash TEXT NOT NULL,
            PRIMARY KEY (note_id, rev, content_hash))""")
        db.execSQL("""CREATE TABLE files(
            id TEXT PRIMARY KEY, name TEXT NOT NULL,
            mime TEXT NOT NULL DEFAULT '', size INTEGER NOT NULL DEFAULT 0,
            sha256 TEXT NOT NULL, stored_name TEXT NOT NULL,
            rev INTEGER NOT NULL DEFAULT 1,
            updated_at INTEGER NOT NULL,
            author_device TEXT NOT NULL,
            is_deleted INTEGER NOT NULL DEFAULT 0)""")
        db.execSQL("CREATE INDEX idx_files_sha ON files(sha256)")
        db.execSQL("""CREATE TABLE file_syncstate(
            file_id TEXT PRIMARY KEY, sync_rev INTEGER NOT NULL DEFAULT 0)""")
        db.execSQL("INSERT INTO keyvalue(key, value) VALUES('schema_version', '8')")
        ensureDeviceId(db)
    }

    override fun onUpgrade(db: SQLiteDatabase, old: Int, current: Int) {
        if (old < 6) {
            // v5 -> v6: канонические id без дефисов (см. ниже v6->v7 для seen).
            v5to6(db)
        }
        if (old < 7) {
            db.execSQL("""CREATE TABLE IF NOT EXISTS seen_conflicts(
                note_id TEXT NOT NULL, rev INTEGER NOT NULL,
                content_hash TEXT NOT NULL,
                PRIMARY KEY (note_id, rev, content_hash))""")
        }
        if (old < 8) {
            // v7 -> v8: отдельные файлы + состояние их синхронизации.
            // Существующие таблицы не трогаем; всё IF NOT EXISTS.
            db.execSQL("""CREATE TABLE IF NOT EXISTS files(
                id TEXT PRIMARY KEY, name TEXT NOT NULL,
                mime TEXT NOT NULL DEFAULT '', size INTEGER NOT NULL DEFAULT 0,
                sha256 TEXT NOT NULL, stored_name TEXT NOT NULL,
                rev INTEGER NOT NULL DEFAULT 1,
                updated_at INTEGER NOT NULL,
                author_device TEXT NOT NULL,
                is_deleted INTEGER NOT NULL DEFAULT 0)""")
            db.execSQL("CREATE INDEX IF NOT EXISTS idx_files_sha ON files(sha256)")
            db.execSQL("""CREATE TABLE IF NOT EXISTS file_syncstate(
                file_id TEXT PRIMARY KEY, sync_rev INTEGER NOT NULL DEFAULT 0)""")
        }
        // Метка версии в keyvalue нигде не читается (источник истины —
        // user_version SQLiteOpenHelper), но держим её честной для диагностики.
        db.execSQL("INSERT OR REPLACE INTO keyvalue(key, value) VALUES('schema_version', '8')")
    }

    private fun v5to6(db: SQLiteDatabase) {
        // v5 -> v6: канонические id без дефисов. Дубли (dashed + N-форма
        // одной заметки) сливаем, одиночные dashed конвертируем.
        db.beginTransaction()
        try {
                db.execSQL("""UPDATE checklist_items SET note_id = REPLACE(note_id, '-', '')
                    WHERE note_id LIKE '%-%'""")
                db.execSQL("""UPDATE attachments SET note_id = REPLACE(note_id, '-', '')
                    WHERE note_id LIKE '%-%'""")
                db.execSQL("""DELETE FROM syncstate WHERE note_id LIKE '%-%' AND
                    REPLACE(note_id, '-', '') IN
                    (SELECT note_id FROM syncstate WHERE note_id NOT LIKE '%-%')""")
                db.execSQL("""UPDATE syncstate SET note_id = REPLACE(note_id, '-', '')
                    WHERE note_id LIKE '%-%'""")
                // Дубли checklist/attachments после слияния: чистим точные повторы.
                db.execSQL("""DELETE FROM checklist_items WHERE rowid NOT IN (
                    SELECT MIN(rowid) FROM checklist_items
                    GROUP BY note_id, position, text, is_checked)""")
                db.execSQL("""DELETE FROM attachments WHERE rowid NOT IN (
                    SELECT MIN(rowid) FROM attachments GROUP BY note_id, sha256)""")
                db.execSQL("""DELETE FROM notes WHERE id LIKE '%-%' AND
                    REPLACE(id, '-', '') IN (SELECT id FROM notes WHERE id NOT LIKE '%-%')""")
                db.execSQL("UPDATE notes SET id = REPLACE(id, '-', '') WHERE id LIKE '%-%'")
                db.setTransactionSuccessful()
            } finally {
                db.endTransaction()
            }
    }

    private fun ensureDeviceId(db: SQLiteDatabase) {
        db.rawQuery("SELECT value FROM keyvalue WHERE key='device_id'", null).use { c ->
            if (c.count == 0) {
                val id = java.util.UUID.randomUUID().toString().replace("-", "")
                db.execSQL("INSERT INTO keyvalue(key, value) VALUES('device_id', ?)",
                    arrayOf(id))
            }
        }
    }

    fun deviceId(): String {
        readableDatabase.rawQuery(
            "SELECT value FROM keyvalue WHERE key='device_id'", null).use { c ->
            c.moveToFirst()
            return c.getString(0)
        }
    }
}
