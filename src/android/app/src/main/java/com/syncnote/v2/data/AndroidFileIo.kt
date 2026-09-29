package com.syncnote.v2.data

import java.io.File
import java.io.FileInputStream
import java.io.InputStream

class FileTooLargeException(name: String, size: Long, max: Long) :
    IllegalStateException("Файл $name ($size байт) превышает лимит $max байт.")

// Content-addressed хранилище: filesDir/files/{sha256_hex}.
// Зеркало C# WindowsFileIo: дедуп, tmp+rename, валидация sha.
class AndroidFileIo(filesDir: File) {
    private val root = File(filesDir, "files").also { it.mkdirs() }

    fun import(sourcePath: String): Pair<String, Long> {
        val src = File(sourcePath)
        if (src.length() > MAX_BYTES)
            throw FileTooLargeException(src.name, src.length(), MAX_BYTES)
        val sha = FileInputStream(src).use { HashUtils.sha256Hex(it) }
        val dest = File(root, sha)
        if (!dest.exists()) {
            val tmp = File.createTempFile("fileio-", ".part", root)
            try {
                src.inputStream().use { inp ->
                    tmp.outputStream().buffered().use { out ->
                        val buf = ByteArray(1024 * 1024)
                        var size = 0L
                        while (true) {
                            val n = inp.read(buf)
                            if (n <= 0) break
                            size += n
                            if (size > MAX_BYTES) {
                                throw FileTooLargeException(src.name, size, MAX_BYTES)
                            }
                            out.write(buf, 0, n)
                        }
                    }
                }
                if (!tmp.renameTo(dest)) {
                    tmp.delete()
                    throw java.io.IOException("Не удалось сохранить файл ${src.name}.")
                }
            } catch (e: Exception) {
                try { tmp.delete() } catch (_: Exception) { }
                throw e
            }
        }
        return sha to dest.length()
    }

    fun existsInStorage(sha256: String): Boolean = try {
        File(root, norm(sha256)).exists()
    } catch (_: Exception) {
        false
    }

    fun openRead(sha256: String): InputStream =
        FileInputStream(File(root, norm(sha256)))

    // Без проверки ссылок: вызывать после проверки живых записей в БД.
    fun deleteFromStorage(sha256: String) {
        try {
            val f = File(root, norm(sha256))
            if (f.exists()) f.delete()
        } catch (_: Exception) { }
    }

    fun getStoragePath(sha256: String): File = File(root, norm(sha256))

    private fun norm(sha256: String): String {
        val s = sha256.lowercase()
        require(s.length == 64 && s.all { it in '0'..'9' || it in 'a'..'f' }) {
            "Некорректный sha256: $sha256."
        }
        return s
    }

    companion object {
        const val MAX_BYTES = 100L * 1024 * 1024
    }
}
