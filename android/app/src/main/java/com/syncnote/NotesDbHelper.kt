package com.syncnote

import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper

// Локальное хранилище (зеркало схемы Core v5: notes + norms, checklist,
// attachments, syncstate, keyvalue). Файлы — в filesDir.
class NotesDbHelper(ctx: Context) :
    SQLiteOpenHelper(ctx, "notes.db", null, 5) {

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
        db.execSQL("INSERT INTO keyvalue(key, value) VALUES('schema_version', '5')")
        ensureDeviceId(db)
    }

    override fun onUpgrade(db: SQLiteDatabase, old: Int, current: Int) {
        // Этап 1: база только создаётся (v5). Миграции — по мере роста схемы.
        onCreate(db)
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
