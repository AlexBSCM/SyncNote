package com.syncnote

import android.content.Context
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject

// Хранилище доверенных ПК (бэклог 1.c).
//
// Лежит в том же файле SharedPreferences("sync"), что и SyncAuto, — это
// даёт два важных свойства:
//   * миграция читает уже существующие host/port;
//   * SyncAuto.clearProfile() (кнопка «Забыть узел») делает edit().clear()
//     и потому заодно очищает список — интеграция не нужна.
//
// Схема БД не затрагивается.
object TrustedDevicesStore {
    private const val PREFS = "sync"
    private const val KEY_DEVICES = "trusted_devices_v2"
    private const val TAG = "TrustedDevices"

    private fun prefs(ctx: Context) =
        ctx.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    // Миграция из legacy-полей SyncAuto (host/port). Идемпотентна:
    // выполняется только пока нового ключа нет.
    private fun migrateIfNeeded(ctx: Context) {
        val p = prefs(ctx)
        if (p.contains(KEY_DEVICES)) return
        val legacy = SyncAuto.profile(ctx)
        val list = if (legacy == null) emptyList() else listOf(
            TrustedDevice(
                id = TrustedDevice.idFor(legacy.first, legacy.second),
                host = legacy.first,
                port = legacy.second,
                // Имя ПК в QR не передаётся, поэтому по умолчанию — адрес;
                // пользователь переименует долгим тапом на экране выбора.
                name = legacy.first,
                // lastUsed = 0 -> «есть устройство, но связи с ним ещё не было».
                // Так мигрированный профиль попадает на экран выбора, а не
                // молча прыгает в MainActivity без единой проверки.
                lastUsed = 0L))
        save(ctx, list)
        Log.i(TAG, "migrated legacy profile: ${list.size} device(s)")
    }

    fun getAll(ctx: Context): List<TrustedDevice> {
        return try {
            migrateIfNeeded(ctx)
            val raw = prefs(ctx).getString(KEY_DEVICES, null) ?: return emptyList()
            val arr = JSONArray(raw)
            (0 until arr.length()).mapNotNull { i ->
                val o = arr.optJSONObject(i) ?: return@mapNotNull null
                val host = o.optString("host").takeIf { it.isNotBlank() } ?: return@mapNotNull null
                TrustedDevice(
                    id = o.optString("id").ifBlank { TrustedDevice.idFor(host, o.optInt("port")) },
                    host = host,
                    port = o.optInt("port"),
                    name = o.optString("name").ifBlank { host },
                    lastUsed = o.optLong("lastUsed"),
                    token = o.optString("token").takeIf { it.isNotBlank() })
            }.sortedByDescending { it.lastUsed }
        } catch (e: Exception) {
            // Fail-safe UX: при любой ошибке считаем, что устройств нет,
            // и показываем экран выбора, а не падаем.
            Log.w(TAG, "read failed, treating as empty", e)
            emptyList()
        }
    }

    fun save(ctx: Context, list: List<TrustedDevice>) {
        val arr = JSONArray()
        for (d in list) {
            arr.put(JSONObject().apply {
                put("id", d.id)
                put("host", d.host)
                put("port", d.port)
                put("name", d.name)
                put("lastUsed", d.lastUsed)
                d.token?.let { put("token", it) }
            })
        }
        prefs(ctx).edit().putString(KEY_DEVICES, arr.toString()).apply()
    }

    fun addOrUpdate(ctx: Context, device: TrustedDevice) {
        val list = getAll(ctx).toMutableList()
        val i = list.indexOfFirst { it.id == device.id }
        if (i >= 0) list[i] = device else list.add(device)
        save(ctx, list)
    }

    fun touch(ctx: Context, id: String) {
        val list = getAll(ctx).toMutableList()
        val i = list.indexOfFirst { it.id == id }
        if (i >= 0) {
            list[i] = list[i].copy(lastUsed = System.currentTimeMillis())
            save(ctx, list)
        }
    }

    // Токен одноразовый: после первого успешного сопряжения он не нужен
    // и не должен храниться дальше.
    fun clearToken(ctx: Context, id: String) {
        val list = getAll(ctx).toMutableList()
        val i = list.indexOfFirst { it.id == id }
        if (i >= 0 && list[i].token != null) {
            list[i] = list[i].copy(token = null)
            save(ctx, list)
        }
    }

    fun rename(ctx: Context, id: String, newName: String) {
        val list = getAll(ctx).toMutableList()
        val i = list.indexOfFirst { it.id == id }
        if (i >= 0) {
            val clean = newName.trim().take(40)
            if (clean.isNotEmpty()) {
                list[i] = list[i].copy(name = clean)
                save(ctx, list)
            }
        }
    }

    fun remove(ctx: Context, id: String) {
        save(ctx, getAll(ctx).filterNot { it.id == id })
    }

    // Проверка для Smart Start Screen. «Подключено» = есть устройство, с
    // которым уже была успешная сессия (lastUsed > 0). Просто известное,
    // но ещё не проверенное устройство НЕ считается подключением — иначе
    // список на экране выбора был бы недостижим.
    // Любая ошибка -> false -> показываем экран выбора (fail-safe UX).
    fun hasActiveTrustedDevice(ctx: Context): Boolean =
        try { getAll(ctx).any { it.lastUsed > 0L } } catch (e: Exception) {
            Log.w(TAG, "check failed", e); false
        }
}
