package com.syncnote.v2.network

import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder

// TCP-реализация FrameTransport. Байт-в-байт как C# TcpFrameTransport:
// [int32 BE длина][UTF-8 JSON], лимит кадра 128 МБ.
class AndroidTcpTransport private constructor(private val socket: Socket) : FrameTransport {
    companion object {
        const val MAX_FRAME_BYTES = 128 * 1024 * 1024
        const val CONNECT_TIMEOUT_MS = 8000
        const val READ_TIMEOUT_MS = 30000

        fun connect(host: String, port: Int): AndroidTcpTransport {
            val s = Socket()
            try {
                s.connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)
                s.soTimeout = READ_TIMEOUT_MS
                return AndroidTcpTransport(s)
            } catch (e: Exception) {
                try { s.close() } catch (_: Exception) { }
                throw SyncNetworkException("connect $host:$port: ${e.message}", e)
            }
        }

        fun wrap(socket: Socket): AndroidTcpTransport {
            socket.soTimeout = READ_TIMEOUT_MS
            return AndroidTcpTransport(socket)
        }
    }

    override fun send(json: String) {
        try {
            val body = json.toByteArray(Charsets.UTF_8)
            if (body.size > MAX_FRAME_BYTES)
                throw SyncNetworkException("Кадр слишком большой: ${body.size}.")
            val head = ByteBuffer.allocate(4)
                .order(ByteOrder.BIG_ENDIAN).putInt(body.size).array()
            val out = socket.getOutputStream()
            out.write(head)
            out.write(body)
            out.flush()
        } catch (e: SyncNetworkException) {
            throw e
        } catch (e: Exception) {
            throw SyncNetworkException("send: ${e.message}", e)
        }
    }

    override fun receive(): String? {
        try {
            val inp = socket.getInputStream()
            val head = ByteArray(4)
            val got = readUpTo(inp, head, 0, 4)
            if (got == 0) return null // чистое закрытие на границе кадра
            if (got < 4) throw SyncNetworkException("Заголовок усечён.")
            val len = ByteBuffer.wrap(head).order(ByteOrder.BIG_ENDIAN).int
            if (len < 0 || len > MAX_FRAME_BYTES)
                throw SyncNetworkException("Некорректная длина кадра: $len.")
            val body = ByteArray(len)
            if (readUpTo(inp, body, 0, len) < len)
                throw SyncNetworkException("Соединение закрыто в середине тела.")
            return String(body, Charsets.UTF_8)
        } catch (e: SyncNetworkException) {
            throw e
        } catch (e: java.net.SocketTimeoutException) {
            throw SyncNetworkException("Таймаут чтения.", e)
        } catch (e: Exception) {
            throw SyncNetworkException("receive: ${e.message}", e)
        }
    }

    // Читает до len байт; возвращает сколько реально прочитано (0..len).
    private fun readUpTo(
        inp: java.io.InputStream, buf: ByteArray, off: Int, len: Int
    ): Int {
        var pos = off
        val end = off + len
        while (pos < end) {
            val n = inp.read(buf, pos, end - pos)
            if (n == -1) break
            pos += n
        }
        return pos - off
    }

    override fun close() {
        try { socket.close() } catch (_: Exception) { }
    }
}
