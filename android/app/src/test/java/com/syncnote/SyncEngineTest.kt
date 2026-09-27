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
}
