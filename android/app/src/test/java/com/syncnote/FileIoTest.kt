package com.syncnote

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.File
import java.security.MessageDigest

// Чистые JVM-тесты FileIo (без Android-рантайма): дедуп, лимит,
// целостность, чистка temp, orphan-логика.
class FileIoTest {
    private fun sha(b: ByteArray): String {
        val md = MessageDigest.getInstance("SHA-256")
        return md.digest(b).joinToString("") { "%02x".format(it) }
    }

    @Test fun importTwice_dedupsToSingleFile() {
        val base = createTempDir("fio")
        val filesDir = File(base, "files")
        val payload = byteArrayOf(1, 2, 3, 4, 5)

        val r1 = FileIo.importUri(filesDir, { ByteArrayInputStream(payload) }, "a.bin")
        val r2 = FileIo.importUri(filesDir, { ByteArrayInputStream(payload) }, "b.bin")

        assertEquals(r1.sha256Hex, r2.sha256Hex)
        assertEquals(r1.sha256Hex, r1.storedName)
        assertEquals(5, r1.sizeBytes)
        assertEquals(sha(payload), r1.sha256Hex)
        assertEquals(1, filesDir.listFiles()!!.size)
        assertArrayEquals(payload, File(filesDir, r1.storedName).readBytes())
    }

    @Test fun importOverLimit_throwsBeforeCopy() {
        val filesDir = File(createTempDir("fio"), "files")
        // Поток длиннее лимита: первые чанки идут, затем исключение.
        // Генерируем лениво, чтобы не аллоцировать 100+ МБ.
        val big = object : java.io.InputStream() {
            var left = FileIo.MAX_FILE_SIZE_BYTES + 1
            override fun read(): Int = if (left-- > 0) 0 else -1
            override fun read(b: ByteArray, off: Int, len: Int): Int {
                if (left <= 0) return -1
                val n = minOf(len.toLong(), left).toInt()
                n.also { left -= n }
                return n
            }
        }
        try {
            FileIo.importUri(filesDir, { big }, "big.bin")
            fail("ожидалось исключение")
        } catch (e: FileTooLargeException) {
            assertEquals(FileIo.MAX_FILE_SIZE_BYTES, e.limit)
        }
        // Ничего не скопировано, temp подтёрт.
        assertEquals(0, filesDir.listFiles()?.size ?: 0)
    }

    @Test fun importMissingStream_throws() {
        val filesDir = File(createTempDir("fio"), "files")
        try {
            FileIo.importUri(filesDir, { null }, "nope.bin")
            fail("ожидалось исключение")
        } catch (e: java.io.IOException) {
            // ok
        }
    }

    @Test fun resolveStoragePath_okAndMissing() {
        val base = createTempDir("fio")
        val filesDir = File(base, "files")
        val r = FileIo.importUri(filesDir, { ByteArrayInputStream(byteArrayOf(7)) }, "x.bin")
        assertEquals(File(filesDir, r.sha256Hex).absolutePath,
            FileIo.resolveStoragePath(filesDir, r.sha256Hex).absolutePath)
        try {
            FileIo.resolveStoragePath(filesDir, "a".repeat(64))
            fail("ожидалось исключение")
        } catch (e: java.io.FileNotFoundException) {
            // ok
        }
        try {
            FileIo.resolveStoragePath(filesDir, "../evil")
            fail("ожидалось исключение")
        } catch (e: IllegalArgumentException) {
            // ok
        }
    }

    @Test fun deleteIfOrphaned_respectsRefs() {
        val base = createTempDir("fio")
        val filesDir = File(base, "files")
        val r = FileIo.importUri(filesDir, { ByteArrayInputStream(byteArrayOf(1)) }, "y.bin")
        assertFalse(FileIo.deleteIfOrphaned(filesDir, r.sha256Hex) { true })
        assertTrue(File(filesDir, r.sha256Hex).exists())
        assertTrue(FileIo.deleteIfOrphaned(filesDir, r.sha256Hex) { false })
        assertFalse(File(filesDir, r.sha256Hex).exists())
        assertFalse(FileIo.deleteIfOrphaned(filesDir, r.sha256Hex) { false })
    }
}
