package com.syncnote.v2.data

import java.security.MessageDigest

// Чистое хеширование без Android-зависимостей (JVM-тест HashTest).
// Зеркало C# SyncNote.Core.Utils.HashUtils.
object HashUtils {
    fun sha256Hex(data: ByteArray): String {
        val md = MessageDigest.getInstance("SHA-256")
        return md.digest(data).joinToString("") { "%02x".format(it) }
    }

    fun sha256Hex(stream: java.io.InputStream): String {
        val md = MessageDigest.getInstance("SHA-256")
        val buf = ByteArray(1024 * 1024)
        while (true) {
            val n = stream.read(buf)
            if (n <= 0) break
            md.update(buf, 0, n)
        }
        return md.digest().joinToString("") { "%02x".format(it) }
    }
}
