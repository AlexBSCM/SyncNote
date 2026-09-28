package com.syncnote

import java.io.File
import java.io.InputStream
import java.security.MessageDigest

// Ввод-вывод отдельных файлов (этап F.2), зеркало FileIo C#.
// - Дедуп: физическое имя == hex sha256 содержимого, повторный импорт
//   байты не копирует.
// - Стриминг: хеш инкрементальный чанками, контент целиком в RAM не грузим
//   (в отличие от старого readBytes()).
// - Атомарность: temp + rename. Лимит 100 МБ — до и во время чтения.
class FileTooLargeException(val fileName: String, val size: Long, val limit: Long) :
    java.io.IOException("Файл '$fileName' ($size байт) превышает лимит $limit байт.")

data class FileImportResult(
    val storedName: String,
    val sizeBytes: Long,
    val sha256Hex: String
)

object FileIo {
    const val MAX_FILE_SIZE_BYTES = 100L * 1024 * 1024
    private const val CHUNK_BYTES = 1024 * 1024

    // Импорт из открываемого потока (SAF-URI через contentResolver или
    // обычный файл): один проход — пишем во временный файл и хешируем
    // на лету, затем либо атомарно переименовываем, либо (дедуп) удаляем tmp.
    // Поток открывается внутри через openStream (может вызываться один раз).
    fun importUri(
        filesDir: File,
        openStream: () -> InputStream?,
        displayName: String
    ): FileImportResult {
        filesDir.mkdirs()
        val tmp = File.createTempFile("fileio-", ".part", filesDir)
        try {
            val md = MessageDigest.getInstance("SHA-256")
            var size = 0L
            (openStream() ?: throw java.io.IOException("Не удалось открыть файл.")).use { src ->
                tmp.outputStream().buffered().use { dst ->
                    val buf = ByteArray(CHUNK_BYTES)
                    while (true) {
                        val n = src.read(buf)
                        if (n <= 0) break
                        size += n
                        if (size > MAX_FILE_SIZE_BYTES) {
                            throw FileTooLargeException(displayName, size, MAX_FILE_SIZE_BYTES)
                        }
                        md.update(buf, 0, n)
                        dst.write(buf, 0, n)
                    }
                }
            }
            val hex = md.digest().joinToString("") { "%02x".format(it) }
            val dest = File(filesDir, hex)
            if (dest.exists()) {
                // Дедуп: байты уже есть, временный файл не нужен.
                tmp.delete()
                return FileImportResult(hex, size, hex)
            }
            if (!tmp.renameTo(dest)) {
                tmp.delete()
                throw java.io.IOException("Не удалось сохранить файл $displayName.")
            }
            return FileImportResult(hex, size, hex)
        } catch (e: Exception) {
            // Не оставляем повреждённый temp-файл.
            try { tmp.delete() } catch (_: Exception) { }
            throw e
        }
    }

    fun resolveStoragePath(filesDir: File, sha256Hex: String): File {
        val norm = sha256Hex.lowercase()
        if (norm.length != 64 || !norm.all { it in '0'..'9' || it in 'a'..'f' })
            throw IllegalArgumentException("Некорректный sha256: $sha256Hex")
        val f = File(filesDir, norm)
        if (!f.exists()) throw java.io.FileNotFoundException("Файл хранилища отсутствует: $norm")
        return f
    }

    fun deleteIfOrphaned(filesDir: File, sha256Hex: String, hasLiveRefs: (String) -> Boolean): Boolean {
        if (hasLiveRefs(sha256Hex)) return false
        val f = File(filesDir, sha256Hex.lowercase())
        if (!f.exists()) return false
        return f.delete()
    }
}
