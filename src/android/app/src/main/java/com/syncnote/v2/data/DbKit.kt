package com.syncnote.v2.data

import android.database.sqlite.SQLiteDatabase
import java.io.File
import java.time.Instant
import java.time.temporal.ChronoUnit
import java.util.UUID

// Общие мелочи репозиториев (не слой абстракции, просто чтобы не дублировать).
internal fun openDb(dbFile: File): SQLiteDatabase {
    dbFile.parentFile?.mkdirs()
    return SQLiteDatabase.openDatabase(
        dbFile.path, null, SQLiteDatabase.OPEN_READWRITE)
}

internal fun utcNow(): String =
    Instant.now().truncatedTo(ChronoUnit.SECONDS).toString()

internal fun newId(): String = UUID.randomUUID().toString().replace("-", "")

internal fun ensureDeviceId(db: SQLiteDatabase): String {
    db.rawQuery(
        "SELECT value FROM keyvalue WHERE key='device_id'", null).use { c ->
        if (c.moveToFirst()) {
            val v = c.getString(0)
            if (v.isNotBlank()) return v
        }
    }
    val id = newId()
    db.execSQL("INSERT OR REPLACE INTO keyvalue (key, value) VALUES ('device_id', ?)",
        arrayOf(id))
    return id
}
