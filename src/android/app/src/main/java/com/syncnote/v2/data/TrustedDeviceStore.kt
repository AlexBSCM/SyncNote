package com.syncnote.v2.data

import android.content.ContentValues
import android.database.sqlite.SQLiteDatabase

// Последнее устройство, с которым была успешная синхронизация.
// Хранится в keyvalue (отдельной таблицы в едином schema.sql нет,
// токены одноразовые — храним только адрес для подстановки в ручной ввод).
// lastSeen — UTC ISO-8601, как updatedAt в движке.
data class TrustedDevice(val ip: String, val port: Int, val lastSeen: String)

object TrustedDeviceStore {
    const val KEY_IP = "trusted_device_ip"
    const val KEY_PORT = "trusted_device_port"
    const val KEY_SEEN = "trusted_device_last_seen"

    fun save(db: SQLiteDatabase, device: TrustedDevice) {
        put(db, KEY_IP, device.ip)
        put(db, KEY_PORT, device.port.toString())
        put(db, KEY_SEEN, device.lastSeen)
    }

    fun get(db: SQLiteDatabase): TrustedDevice? {
        val ip = getStr(db, KEY_IP)?.trim().orEmpty()
        val port = getStr(db, KEY_PORT)?.toIntOrNull() ?: 0
        if (ip.isEmpty() || port !in 1..65535) return null
        return TrustedDevice(ip, port, getStr(db, KEY_SEEN).orEmpty())
    }

    fun clear(db: SQLiteDatabase) {
        db.delete("keyvalue", "key IN (?,?,?)",
            arrayOf(KEY_IP, KEY_PORT, KEY_SEEN))
    }

    private fun put(db: SQLiteDatabase, key: String, value: String) {
        val cv = ContentValues().apply {
            put("key", key)
            put("value", value)
        }
        db.insertWithOnConflict("keyvalue", null, cv,
            SQLiteDatabase.CONFLICT_REPLACE)
    }

    private fun getStr(db: SQLiteDatabase, key: String): String? {
        db.rawQuery("SELECT value FROM keyvalue WHERE key=?",
            arrayOf(key)).use { c ->
            if (c.moveToFirst()) return c.getString(0)
            return null
        }
    }
}
