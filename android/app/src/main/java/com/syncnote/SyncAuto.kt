package com.syncnote

import android.content.Context
import android.widget.Toast

// Запомненный узел + автоматический пуш при локальных изменениях.
// Токен одноразовый: после первого успеха устройство доверенное,
// дальше сессии идут без токена. Кнопка остаётся для ручного запуска.
object SyncAuto {
    private const val PREFS = "sync"
    private const val KEY_HOST = "host"
    private const val KEY_PORT = "port"

    fun saveProfile(ctx: Context, host: String, port: Int) {
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putString(KEY_HOST, host).putInt(KEY_PORT, port).apply()
    }

    fun profile(ctx: Context): Pair<String, Int>? {
        val p = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val host = p.getString(KEY_HOST, null)
        val port = p.getInt(KEY_PORT, 0)
        if (host.isNullOrBlank() || port <= 0) return null
        return host to port
    }

    // Тихий автосинх: успех молча, ошибка — короткий тост, данные целы.
    // quiet=true: вообще без тостов (для фонового опроса).
    fun trigger(ctx: Context, quiet: Boolean = false) {
        val (host, port) = profile(ctx) ?: return
        Thread {
            try {
                val repo = NotesRepository(ctx.applicationContext)
                val session = SyncSession(host, port, repo.deviceId(),
                    android.os.Build.MODEL, null)
                val r = session.run(repo)
                if (r.conflicts > 0 && !quiet) {
                    toast(ctx, "Синхронизировано, есть конфликты — откройте «Конфликты».")
                }
            } catch (e: Exception) {
                if (!quiet) toast(ctx, "Автосинхронизация не удалась: ${e.message}")
            }
        }.apply { isDaemon = true }.start()
    }

    private fun toast(ctx: Context, text: String) {
        val app = ctx.applicationContext
        android.os.Handler(app.mainLooper).post {
            Toast.makeText(app, text, Toast.LENGTH_LONG).show()
        }
    }
}
