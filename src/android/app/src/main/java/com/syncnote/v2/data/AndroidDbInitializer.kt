package com.syncnote.v2.data

import android.content.Context
import android.database.sqlite.SQLiteDatabase

// Инициализация БД на устройстве: схема из assets/schema_v1.sql
// (генерируется из docs/schema.sql задачей syncSchemaAsset),
// применение атомарно в транзакции, повторный запуск — no-op.
// Ошибка = исключение (fail-fast), молчаливых провалов нет.
object AndroidDbInitializer {
    const val DB_NAME = "syncnote_v2.db"
    const val TARGET_VERSION = 1

    fun initialize(context: Context, dbName: String = DB_NAME) {
        val dbFile = context.getDatabasePath(dbName)
        dbFile.parentFile?.mkdirs()
        val db = SQLiteDatabase.openOrCreateDatabase(dbFile, null)
        try {
            db.beginTransaction()
            try {
                if (getCurrentVersion(db) < TARGET_VERSION) {
                    val sql = context.assets.open("schema_v1.sql")
                        .bufferedReader().use { it.readText() }
                    for (stmt in SqlSplitter.split(sql)) db.execSQL(stmt)
                    db.execSQL(
                        "INSERT OR REPLACE INTO keyvalue (key, value)" +
                            " VALUES ('schema_version', '1')")
                }
                db.setTransactionSuccessful()
            } finally {
                db.endTransaction()
            }
        } finally {
            db.close()
        }
    }

    fun getCurrentVersion(context: Context, dbName: String = DB_NAME): Int {
        val db = SQLiteDatabase.openOrCreateDatabase(
            context.getDatabasePath(dbName), null)
        try {
            return getCurrentVersion(db)
        } finally {
            db.close()
        }
    }

    private fun getCurrentVersion(db: SQLiteDatabase): Int {
        try {
            db.rawQuery(
                "SELECT value FROM keyvalue WHERE key='schema_version'",
                null).use { c ->
                if (c.moveToFirst()) return c.getString(0).toIntOrNull() ?: 0
                return 0
            }
        } catch (e: android.database.sqlite.SQLiteException) {
            return 0 // нет таблицы — чистая база
        }
    }
}
