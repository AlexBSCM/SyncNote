package com.syncnote

import org.junit.Assert.*
import org.junit.Test
import java.io.File
import java.util.UUID

// Чистые JVM-тесты движка (без эмулятора): идемпотентность,
// fast-forward, конфликт с сохранением обеих версий, tombstone.
class SyncEngineTest {
    class FakeStore : SyncStore {
        val notes = mutableMapOf<String, Note>()
        val items = mutableMapOf<String, MutableList<ChecklistItem>>()
        val files = mutableMapOf<String, MutableList<Attachment>>()
        val syncs = mutableMapOf<String, Long>()
        val seen = mutableSetOf<Triple<String, Long, String>>()
        val sepFiles = mutableMapOf<String, FileEntry>()
        val fileSyncs = mutableMapOf<String, Long>()
        val physicalFiles = mutableSetOf<String>()
        val dir: File = createTempDir("fakefiles")

        override fun tryGet(id: String) = notes[id]
        override fun getSyncRev(id: String) = syncs[id] ?: 0
        override fun setSyncRev(id: String, rev: Long) { syncs[id] = rev }
        override fun noteSeenConflict(id: String, rev: Long, h: String): Boolean {
            val k = Triple(id, rev, h)
            if (k in seen) return true
            seen += k
            return false
        }
        override fun checklist(noteId: String) = items[noteId] ?: emptyList()
        override fun attachments(noteId: String) = files[noteId] ?: emptyList()
        override fun deviceId() = "fake-device"
        override fun filesDir() = dir

        override fun exportAll() = notes.values.map { n ->
            SyncNoteDto(id = n.id, rev = n.rev, baseRev = getSyncRev(n.id),
                title = n.title, body = n.body, updatedAt = n.updatedAt,
                author = n.authorDeviceId, isDeleted = n.isDeleted)
        }

        override fun importFull(dto: SyncNoteDto, fileBytes: (String) -> ByteArray?) {
            notes[dto.id] = Note(id = dto.id, rev = dto.rev, title = dto.title,
                body = dto.body, updatedAt = dto.updatedAt,
                authorDeviceId = dto.author, isDeleted = dto.isDeleted)
            for (meta in dto.attachments) {
                fileBytes(meta.sha256)
                    ?: throw IllegalStateException("Нет байтов файла ${meta.fileName}.")
            }
        }

        fun add(title: String, body: String): Note {
            val n = Note(title = title, body = body)
            notes[n.id] = n
            return n
        }

        fun edit(id: String, title: String) {
            val n = notes[id]!!
            n.title = title
            n.rev += 1
        }

        override fun getFiles(includeDeleted: Boolean) =
            sepFiles.values.filter { includeDeleted || !it.isDeleted }
        override fun tryGetFile(id: String) = sepFiles[id]
        override fun insertFullFile(dto: SyncFileDto) {
            sepFiles[dto.id] = FileEntry(id = dto.id, name = dto.name,
                mime = dto.mime, sizeBytes = dto.size, sha256 = dto.sha256,
                storedName = dto.sha256, rev = dto.rev, updatedAt = dto.updatedAt,
                authorDeviceId = dto.author, isDeleted = dto.isDeleted)
            physicalFiles += dto.sha256.lowercase()
        }
        override fun updateFullFile(dto: SyncFileDto) = insertFullFile(dto)
        override fun addFile(name: String, mime: String, content: ByteArray): FileEntry {
            if (!com.syncnote.FeatureFlags.enableSeparateFiles)
                throw IllegalStateException("flag off")
            val md = java.security.MessageDigest.getInstance("SHA-256")
            val hex = md.digest(content).joinToString("") { "%02x".format(it) }
            val e = FileEntry(name = name, mime = mime,
                sizeBytes = content.size.toLong(), sha256 = hex, storedName = hex,
                rev = 1, authorDeviceId = deviceId())
            sepFiles[e.id] = e
            physicalFiles += hex
            return e
        }
        override fun deleteFile(id: String): Boolean {
            if (!com.syncnote.FeatureFlags.enableSeparateFiles)
                throw IllegalStateException("flag off")
            val e = sepFiles[id] ?: return false
            if (e.isDeleted) return false
            sepFiles[id] = e.copy(isDeleted = true, rev = e.rev + 1)
            return true
        }
        override fun getFileSyncRev(fileId: String) = fileSyncs[fileId] ?: 0
        override fun setFileSyncRev(fileId: String, rev: Long) { fileSyncs[fileId] = rev }
        override fun hasLiveReferencesToSha(sha256: String) =
            sepFiles.values.any { it.sha256.equals(sha256, ignoreCase = true) && !it.isDeleted }
        override fun sweepOrphanedFiles(): Int {
            if (!com.syncnote.FeatureFlags.enableSeparateFiles) return 0
            var removed = 0
            val tombShas = sepFiles.values
                .filter { it.isDeleted }
                .map { it.sha256.lowercase() }
                .toSet()
            for (sha in tombShas) {
                if (hasLiveReferencesToSha(sha)) continue
                if (physicalFiles.remove(sha)) removed++
            }
            return removed
        }
        override fun exportFiles() = sepFiles.values.map { e ->
            SyncFileDto(id = e.id, rev = e.rev, baseRev = getFileSyncRev(e.id),
                name = e.name, mime = e.mime, size = e.sizeBytes,
                sha256 = e.sha256, updatedAt = e.updatedAt,
                author = e.authorDeviceId, isDeleted = e.isDeleted)
        }
    }

    @Test fun insert_thenReapply_noOp() {
        val a = FakeStore()
        val b = FakeStore()
        val n = a.add("Привет", "мир")
        val dto = a.exportAll().first { it.id == n.id }

        val (r1, _) = SyncEngine.apply(b, dto)
        assertEquals(ApplyResult.Inserted, r1)
        assertEquals("Привет", b.notes[n.id]!!.title)

        val (r2, _) = SyncEngine.apply(b, dto)
        assertEquals(ApplyResult.NoOp, r2)
    }

    @Test fun sequentialEdits_fastForward() {
        val a = FakeStore()
        val b = FakeStore()
        val n = a.add("Заг", "тело")
        SyncEngine.apply(b, a.exportAll().first { it.id == n.id })

        b.edit(n.id, "Заг v2")
        val dto = b.exportAll().first { it.id == n.id }
        val (r, _) = SyncEngine.apply(a, dto)
        assertEquals(ApplyResult.FastForwarded, r)
        assertEquals("Заг v2", a.notes[n.id]!!.title)
    }

    @Test fun divergentEdits_keepBoth() {
        val a = FakeStore()
        val b = FakeStore()
        val n = a.add("Общая", "база")
        SyncEngine.apply(b, a.exportAll().first { it.id == n.id })

        a.edit(n.id, "Версия A")
        b.edit(n.id, "Версия B")
        val dtoA = a.exportAll().first { it.id == n.id }
        val (r, conflict) = SyncEngine.apply(b, dtoA)

        assertEquals(ApplyResult.Conflict, r)
        assertNotNull(conflict)
        assertEquals("Версия B", b.notes[n.id]!!.title)
        val copy = b.notes[conflict!!.copyId]!!
        assertTrue(copy.title.endsWith(SyncEngine.COPY_SUFFIX))
        assertEquals(2, b.notes.size)
    }

    @Test fun conflictRetry_noDuplicate() {
        val a = FakeStore()
        val b = FakeStore()
        val n = a.add("Общая", "база")
        SyncEngine.apply(b, a.exportAll().first { it.id == n.id })

        a.edit(n.id, "Версия A")
        b.edit(n.id, "Версия B")
        val dtoA = a.exportAll().first { it.id == n.id }
        val (r1, c1) = SyncEngine.apply(b, dtoA)
        assertEquals(ApplyResult.Conflict, r1)
        assertNotNull(c1)
        assertEquals(2, b.notes.size)

        val (r2, c2) = SyncEngine.apply(b, dtoA)
        assertEquals(ApplyResult.NoOp, r2)
        assertNull(c2)
        assertEquals(2, b.notes.size)
    }

    @Test fun unknownIdWithFiles_missingBytes_throws() {
        val b = FakeStore()
        val dto = SyncNoteDto(id = UUID.randomUUID().toString(), rev = 1,
            title = "Ф", body = "",
            attachments = listOf(AttachmentMetaDto(fileName = "f.bin", sha256 = "abc")))
        try {
            SyncEngine.apply(b, dto) { null }
            fail("ожидалось исключение")
        } catch (e: IllegalStateException) {
            assertTrue(e.message!!.contains("f.bin"))
        }
    }

    private fun sha(s: String): String {
        val md = java.security.MessageDigest.getInstance("SHA-256")
        return md.digest(s.toByteArray()).joinToString("") { "%02x".format(it) }
    }

    private fun fileDto(id: String, rev: Long, name: String, content: String) =
        SyncFileDto(id = id, rev = rev, baseRev = 0, name = name,
            mime = "text/plain", size = content.length.toLong(),
            sha256 = sha(content), updatedAt = 1, author = "dev", isDeleted = false)

    private fun fileBytes(vararg pairs: Pair<String, String>): (String) -> ByteArray? {
        // Ключи — sha контента.
        val bySha = pairs.associate { (_, content) ->
            val b = content.toByteArray()
            sha(content) to b
        }
        return { sha -> bySha[sha] }
    }

    @Test fun fileInsert_reapply_noOp() {
        val b = FakeStore()
        val dto = fileDto(UUID.randomUUID().toString(), 1, "f.txt", "aaa")
        val (r1, _) = SyncEngine.applyFile(b, dto, fileBytes("k" to "aaa"))
        assertEquals(ApplyResult.Inserted, r1)
        assertEquals("f.txt", b.sepFiles[dto.id]!!.name)

        val (r2, c2) = SyncEngine.applyFile(b, dto, fileBytes("k" to "aaa"))
        assertEquals(ApplyResult.NoOp, r2)
        assertNull(c2)
    }

    @Test fun fileDivergent_keepBoth_noDupOnRetry() {
        val b = FakeStore()
        val id = UUID.randomUUID().toString()
        val fb = fileBytes("k" to "aaa")
        val (ri, _) = SyncEngine.applyFile(b, fileDto(id, 1, "f.txt", "aaa"), fb)
        assertEquals(ApplyResult.Inserted, ri)

        // Локальная правка: rev 2 при syncRev 1. Входит A rev 2 base 1.
        b.sepFiles[id] = b.sepFiles[id]!!.copy(name = "Версия B", rev = 2)
        val dtoA = fileDto(id, 2, "Версия A", "aaa").copy(baseRev = 1)
        val (r1, c1) = SyncEngine.applyFile(b, dtoA, fb)
        assertEquals(ApplyResult.Conflict, r1)
        assertNotNull(c1)
        assertTrue(c1!!.copyTitle.endsWith(" (конфликт)"))
        assertEquals(2, b.sepFiles.size)

        val (r2, c2) = SyncEngine.applyFile(b, dtoA, fb)
        assertEquals(ApplyResult.NoOp, r2)
        assertNull(c2)
        assertEquals(2, b.sepFiles.size)
    }

    @Test fun fileDelete_propagates_asTombstone() {
        val b = FakeStore()
        val id = UUID.randomUUID().toString()
        val fb = fileBytes("k" to "aaa")
        SyncEngine.applyFile(b, fileDto(id, 1, "f.txt", "aaa"), fb)
        val tomb = fileDto(id, 2, "f.txt", "aaa").copy(isDeleted = true)
        val (r, _) = SyncEngine.applyFile(b, tomb, fb)
        assertEquals(ApplyResult.FastForwarded, r)
        assertTrue(b.sepFiles[id]!!.isDeleted)
        assertEquals(0, b.getFiles().size)
        assertEquals(1, b.getFiles(includeDeleted = true).size)
    }

    @Test fun fileMissingBytes_throws() {
        val b = FakeStore()
        val dto = fileDto(UUID.randomUUID().toString(), 1, "f.txt", "zzz")
        try {
            SyncEngine.applyFile(b, dto) { null }
            fail("ожидалось исключение")
        } catch (e: IllegalStateException) {
            assertTrue(e.message!!.contains(dto.sha256))
        }
    }

    @Test fun fileCrud_addGetDelete_tombstone() {
        val s = FakeStore()
        val e = s.addFile("d.txt", "text/plain", "hi".toByteArray())
        assertEquals(1, e.rev)
        assertFalse(e.isDeleted)
        assertEquals(1, s.getFiles().size)
        assertTrue(s.deleteFile(e.id))
        assertEquals(0, s.getFiles().size)
        assertEquals(1, s.getFiles(includeDeleted = true).size)
        assertTrue(s.tryGetFile(e.id)!!.isDeleted)
        assertFalse(s.deleteFile(UUID.randomUUID().toString()))
    }

    @Test fun fileExport_includesTombstones_withBaseRev() {
        val s = FakeStore()
        val e = s.addFile("e.txt", "text/plain", "x".toByteArray())
        s.deleteFile(e.id)
        val all = s.exportFiles()
        assertEquals(1, all.size)
        assertTrue(all[0].isDeleted)
    }

    @Test fun fileDisabledFlag_blocksNewPaths() {
        val s = FakeStore()
        com.syncnote.FeatureFlags.enableSeparateFiles = false
        try {
            try {
                s.addFile("x", "text/plain", byteArrayOf(1))
                fail("ожидалось исключение")
            } catch (e: IllegalStateException) { }
            try {
                s.deleteFile("y")
                fail("ожидалось исключение")
            } catch (e: IllegalStateException) { }
        } finally {
            com.syncnote.FeatureFlags.enableSeparateFiles = true
        }
    }

    @Test fun purge_removesOrphaned_keepsRows() {
        val s = FakeStore()
        val e = s.addFile("p.bin", "application/octet-stream", byteArrayOf(3, 4))
        assertTrue(s.physicalFiles.contains(e.sha256))
        s.deleteFile(e.id)
        assertEquals(1, s.sweepOrphanedFiles())
        assertFalse(s.physicalFiles.contains(e.sha256))
        // Строка-tombstone осталась.
        assertTrue(s.tryGetFile(e.id)!!.isDeleted)
        assertEquals(0, s.sweepOrphanedFiles())
    }

    @Test fun purge_keepsPhysical_whenOtherLiveRefExists() {
        val s = FakeStore()
        val a = s.addFile("a.bin", "application/octet-stream", byteArrayOf(5))
        // Вторая строка на тот же контент (дедуп): имитируем напрямую.
        val b = s.addFile("b.bin", "application/octet-stream", byteArrayOf(5))
        assertEquals(a.sha256, b.sha256)
        s.deleteFile(a.id)
        assertEquals(0, s.sweepOrphanedFiles())
        assertTrue(s.physicalFiles.contains(a.sha256))
        s.deleteFile(b.id)
        assertEquals(1, s.sweepOrphanedFiles())
        assertFalse(s.physicalFiles.contains(a.sha256))
    }

    @Test fun purge_flagDisabled_noOp() {
        val s = FakeStore()
        val e = s.addFile("z.bin", "application/octet-stream", byteArrayOf(6))
        s.deleteFile(e.id)
        com.syncnote.FeatureFlags.enableSeparateFiles = false
        try {
            assertEquals(0, s.sweepOrphanedFiles())
            assertTrue(s.physicalFiles.contains(e.sha256))
        } finally {
            com.syncnote.FeatureFlags.enableSeparateFiles = true
        }
    }
}
