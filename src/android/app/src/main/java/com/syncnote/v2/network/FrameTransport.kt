package com.syncnote.v2.network

import java.io.EOFException
import java.io.IOException

// Ошибка транспорта (обрыв, усечение, таймаут). Ошибки протокола
// (sha, лимиты, версии) — отдельно, см. движок.
class SyncNetworkException(message: String, cause: Throwable? = null) :
    IOException(message, cause)

// Тупой JSON-канал: длина int32 BE + UTF-8. Паритет C# ITransport.
interface FrameTransport : AutoCloseable {
    fun send(json: String)
    // null — чистое закрытие пиром на границе кадра; в середине — исключение.
    fun receive(): String?
}
