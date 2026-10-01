package com.syncnote

import android.app.Application
import android.os.Build
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.LiveData
import androidx.lifecycle.MutableLiveData
import java.util.concurrent.atomic.AtomicBoolean

// Логика Smart Start Screen (бэклог 1.c).
// Заметок/файлов/конфликтов не касается: только список доверенных ПК,
// подключение к выбранному и переименование.
class SyncHomeViewModel(app: Application) : AndroidViewModel(app) {

    private val ctx = app.applicationContext

    private val _devices = MutableLiveData<List<TrustedDevice>>(emptyList())
    val devices: LiveData<List<TrustedDevice>> = _devices

    // Id устройства в состоянии подключения; null — ничего не происходит.
    // Флаг не даёт запустить вторую попытку поверх первой.
    private val _connectingId = MutableLiveData<String?>(null)
    val connectingId: LiveData<String?> = _connectingId

    private val busy = AtomicBoolean(false)

    fun load() {
        _devices.value = TrustedDevicesStore.getAll(ctx)
    }

    fun rename(id: String, newName: String) {
        TrustedDevicesStore.rename(ctx, id, newName)
        load()
    }

    fun remove(id: String) {
        TrustedDevicesStore.remove(ctx, id)
        load()
    }

    fun isBusy(): Boolean = busy.get()

    // Подключение к ПК и синхронизация. Работает в отдельном потоке,
    // результат возвращает в главный поток.
    fun connectAndSync(
        id: String,
        onSuccess: (SyncSessionResult) -> Unit,
        onError: (String) -> Unit
    ) {
        if (!busy.compareAndSet(false, true)) return
        val device = _devices.value?.firstOrNull { it.id == id } ?: run {
            busy.set(false); onError("Устройство не найдено"); return
        }
        _connectingId.postValue(id)

        Thread {
            var result: SyncSessionResult? = null
            var error: String? = null
            try {
                val repo = NotesRepository(ctx)
                // Реальный API SyncSession: конструктор + run(store) на один хост.
                // token обязателен для первого сопряжения: без него сервер
                // отвечает «нет доверия: нужен токен».
                val session = SyncSession(
                    device.host, device.port, repo.deviceId(), Build.MODEL, device.token)
                // cacheDir обязателен: при null SyncSession берёт
                // java.io.tmpdir, который на Android равен /data/local/tmp —
                // недоступную приложению папку. Тогда приём файлов падает с
                // FileNotFoundException. SyncActivity здесь передаёт cacheDir.
                result = session.run(repo, ctx.cacheDir)
            } catch (e: Exception) {
                error = e.message ?: e.javaClass.simpleName
            }
            busy.set(false)
            android.os.Handler(android.os.Looper.getMainLooper()).post {
                _connectingId.value = null
                val r = result
                if (r != null) {
                    TrustedDevicesStore.touch(ctx, id)
                    // Токен израсходован — дальше устройство доверенное.
                    TrustedDevicesStore.clearToken(ctx, id)
                    // Поддерживаем автосинх SyncAuto в согласованном состоянии.
                    SyncAuto.saveProfile(ctx, device.host, device.port,
                        TrustedDevicesStore.getAll(ctx).map { it.host })
                    load()
                    onSuccess(r)
                } else {
                    onError(error ?: "неизвестная ошибка")
                }
            }
        }.apply { isDaemon = true }.start()
    }

    // Регистрация нового устройства после сканирования QR.
    // lastUsed = 0: «известно, но не проверено». Успешный connectAndSync
    // проставит его через touch(), и только тогда запуск пойдёт сразу в Main.
    fun addDevice(host: String, port: Int, name: String? = null, token: String? = null) {
        val id = TrustedDevice.idFor(host, port)
        val existing = TrustedDevicesStore.getAll(ctx).firstOrNull { it.id == id }
        TrustedDevicesStore.addOrUpdate(ctx, TrustedDevice(
            id = id,
            host = host,
            port = port,
            name = name?.takeIf { it.isNotBlank() } ?: existing?.name ?: host,
            lastUsed = 0L,
            token = token?.takeIf { it.isNotBlank() } ?: existing?.token))
        SyncAuto.saveProfile(ctx, host, port, TrustedDevicesStore.getAll(ctx).map { it.host })
        load()
    }
}
