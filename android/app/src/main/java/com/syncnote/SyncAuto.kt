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
    private const val KEY_HOSTS = "hosts"

    fun saveProfile(ctx: Context, host: String, port: Int, hosts: List<String> = emptyList()) {
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putString(KEY_HOST, host).putInt(KEY_PORT, port)
            .putString(KEY_HOSTS, (hosts + host).distinct().joinToString(","))
            .apply()
    }

    fun profile(ctx: Context): Pair<String, Int>? {
        val p = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val host = p.getString(KEY_HOST, null)
        val port = p.getInt(KEY_PORT, 0)
        if (host.isNullOrBlank() || port <= 0) return null
        return host to port
    }

    fun profileHosts(ctx: Context): List<String> {
        val p = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        return (p.getString(KEY_HOSTS, null)?.split(",") ?: emptyList()) +
            listOfNotNull(p.getString(KEY_HOST, null))
    }

    // Тихий автосинх: успех молча, ошибка — короткий тост, данные целы.
    // quiet=true: вообще без тостов (для фонового опроса).
    fun trigger(ctx: Context, quiet: Boolean = false) {
        val (host, port) = profile(ctx) ?: return
        val hosts = (profileHosts(ctx) + host).distinct().filter { it.isNotBlank() }
        Thread {
            try {
                val repo = NotesRepository(ctx.applicationContext)
                val session = SyncSession(host, port, repo.deviceId(),
                    android.os.Build.MODEL, null)
                val r = session.tryHosts(repo, null, hosts)
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
