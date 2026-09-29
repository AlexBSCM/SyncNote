package com.syncnote.v2.data

import android.content.Context
import android.util.Log
import com.syncnote.v2.domain.QrPayload
import com.syncnote.v2.domain.SyncEngine
import com.syncnote.v2.domain.SyncResult
import com.syncnote.v2.network.AndroidTcpTransport
import java.io.File

// Однаshot-синхронизация по QR-пейлоаду на реальных SQL-репозиториях.
// Сеть — на фоновом потоке (блокирующих вызовов UI нет).
// Колбэк вызывается в том же фоновом потоке; маршалинг на UI — дело вызывающего.
// Токен в логи не попадает (см. также движок: ошибки без значений).
class SyncRunner(private val appCtx: Context) {
    fun syncWithQr(payloadJson: String, cb: (Result<SyncResult>) -> Unit) {
        Thread {
            try {
                val p = QrPayload.parse(payloadJson)
                    ?: throw IllegalArgumentException("Некорректный QR-код.")
                val dbFile = appCtx.getDatabasePath(AndroidDbInitializer.DB_NAME)
                val notes = SqliteNoteRepo(dbFile)
                val io = AndroidFileIo(appCtx.filesDir)
                val files = SqliteFileRepo(dbFile, io)
                val sync = SqlSyncStateStore(dbFile)
                val h = openDb(dbFile)
                val deviceId = try {
                    ensureDeviceId(h)
                } finally {
                    h.close()
                }
                val eng = SyncEngine(notes, files, io, sync, deviceId,
                    File(appCtx.cacheDir, "synctmp"))
                AndroidTcpTransport.connect(p.ip, p.port).use { t ->
                    val r = eng.runSession(t, true, token = p.token)
                    Log.i("SyncNoteV2",
                        "sync ok pushed=${r.pushed} pulled=${r.pulled} " +
                            "conflicts=${r.conflicts}")
                    cb(Result.success(r))
                }
            } catch (e: Exception) {
                Log.i("SyncNoteV2", "sync failed: ${e.javaClass.simpleName}")
                cb(Result.failure(e))
            }
        }.start()
    }
}
