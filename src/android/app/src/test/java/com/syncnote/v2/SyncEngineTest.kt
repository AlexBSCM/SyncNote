package com.syncnote.v2.domain

import com.syncnote.v2.data.HashUtils
import com.syncnote.v2.network.AndroidTcpTransport
import org.junit.Assert.*
import org.junit.Test
import java.io.File
import java.io.InputStream
import java.net.ServerSocket
import java.nio.file.Files

// Сквозные сессии движка поверх TCP-loopback с in-memory фейками репозиториев
// (SQLite недоступен на JVM; SQL-имплементации проверяются на железе).
// Дублируют C# сценарии A–D + tombstone.
class SyncEngineTest {
    private class FakeNotes : NoteRepo {
        val map = mutableMapOf<String, NoteEntry>()
        override fun getById(id: String) = map[id]
        override fun getAll(includeDeleted: Boolean) =
            map.values.filter { includeDeleted || !it.isDeleted }
                .sortedByDescending { it.updatedAt }
        override fun create(note: NoteEntry): String {
            val id = note.id.ifBlank { java.util.UUID.randomUUID().toString() }
            map[id] = note.copy(id = id, rev = 1)
            return id
        }
        override fun update(note: NoteEntry) {
            val cur = map[note.id] ?: throw NoSuchElementException(note.id)
            if (cur.title == note.title && cur.body == note.body) return
            if (cur.rev != note.rev) throw IllegalStateException("stale rev")
            map[note.id] = note.copy(rev = cur.rev + 1)
        }
        override fun softDelete(id: String) {
            map[id]?.let { map[id] = it.copy(isDeleted = true, rev = it.rev + 1) }
        }
        override fun insertFull(e: NoteEntry) { map[e.id] = e }
        override fun updateFull(e: NoteEntry) { map[e.id] = e }
    }

    private class FakeFiles(val io: FakeIo) : FileRepo {
        val map = mutableMapOf<String, FileEntry>()
        override fun getById(id: String) = map[id]
        override fun getAll(includeDeleted: Boolean) =
            map.values.filter { includeDeleted || !it.isDeleted }
                .sortedByDescending { it.updatedAt }
        override fun addFile(metadata: FileEntry, sourcePath: String): String {
            val (sha, size) = io.import(sourcePath)
            val id = java.util.UUID.randomUUID().toString()
            map[id] = metadata.copy(id = id, sizeBytes = size,
                sha256 = sha, storedName = sha, rev = 1)
            return id
        }
        override fun softDelete(id: String) {
            map[id]?.let { map[id] = it.copy(isDeleted = true, rev = it.rev + 1) }
        }
        override fun insertFull(e: FileEntry) { map[e.id] = e }
        override fun updateFull(e: FileEntry) { map[e.id] = e }
    }

    private class FakeIo(root: File) {
        private val dir = File(root, "files").also { it.mkdirs() }
        fun import(sourcePath: String): Pair<String, Long> {
            val bytes = File(sourcePath).readBytes()
            val sha = HashUtils.sha256Hex(bytes)
            val dest = File(dir, sha)
            if (!dest.exists()) dest.writeBytes(bytes)
            return sha to bytes.size.toLong()
        }
        fun asIface() = object : FileIo {
            override fun import(sourcePath: String) = this@FakeIo.import(sourcePath)
            override fun existsInStorage(sha256: String) =
                File(dir, sha256.lowercase()).exists()
            override fun openRead(sha256: String): InputStream =
                File(dir, sha256.lowercase()).inputStream()
            override fun deleteFromStorage(sha256: String) {
                File(dir, sha256.lowercase()).let { if (it.exists()) it.delete() }
            }
            override fun getStoragePath(sha256: String) = File(dir, sha256.lowercase())
        }
    }

    private class FakeSync : SyncState {
        val revs = mutableMapOf<String, Long>()
        val seen = mutableSetOf<String>()
        val live = mutableSetOf<String>()
        override fun getSyncRev(t: String, id: String) = revs["$t/$id"] ?: 0
        override fun setSyncRev(t: String, id: String, rev: Long) { revs["$t/$id"] = rev }
        override fun noteSeenConflict(id: String, rev: Long, hash: String): Boolean {
            val k = "$id/$rev/$hash"
            if (k in seen) return true
            seen += k
            return false
        }
        override fun hasLiveFileRef(sha256: String) = sha256.lowercase() in live
    }

    private class Node(device: String) {
        val dir: File = Files.createTempDirectory("v2node").toFile()
        val notes = FakeNotes()
        val fio = FakeIo(dir)
        val files = FakeFiles(fio)
        val sync = FakeSync()
        val engine = SyncEngine(notes, files, fio.asIface(), sync, device, File(dir, "tmp"))

        fun syncWith(peer: Node): SyncResult {
            val server = ServerSocket(0)
            val port = server.localPort
            var serverErr: Throwable? = null
            var clientErr: Throwable? = null
            var clientRes: SyncResult? = null
            val st = Thread {
                try {
                    AndroidTcpTransport.wrap(server.accept()).use { t ->
                        peer.engine.runSession(t, false)
                    }
                } catch (e: Throwable) { serverErr = e } finally { server.close() }
            }
            val ct = Thread {
                try {
                    AndroidTcpTransport.connect("127.0.0.1", port).use { t ->
                        clientRes = engine.runSession(t, true)
                    }
                } catch (e: Throwable) { clientErr = e }
            }
            st.isDaemon = true
            ct.isDaemon = true
            st.start()
            ct.start()
            st.join(60000)
            ct.join(60000)
            serverErr?.let { throw AssertionError("server: $it") }
            clientErr?.let { throw AssertionError("client: $it") }
            return clientRes!!
        }
    }

    @Test fun noteSync_arrivesAtPeer() {
        val a = Node("a"); val b = Node("b")
        val id = a.notes.create(NoteEntry(title = "hi", body = "body"))
        val r = a.syncWith(b)
        assertEquals(1, r.pushed)
        val got = b.notes.getById(id)!!
        assertEquals("hi", got.title)
        assertEquals(1, got.rev)
    }

    @Test fun divergentEdits_keepBothCopiesOnce() {
        val a = Node("a"); val b = Node("b")
        val id = a.notes.create(NoteEntry(title = "t", body = "b"))
        a.syncWith(b)
        a.notes.update(a.notes.getById(id)!!.copy(title = "A"))
        b.notes.update(b.notes.getById(id)!!.copy(title = "B"))
        val r = a.syncWith(b)
        assertEquals(1, r.conflicts)
        assertEquals(2, a.notes.getAll(true).size)
        assertEquals(2, b.notes.getAll(true).size)
        assertTrue(a.notes.getAll().any { it.title.endsWith(" (конфликт)") })
        assertTrue(b.notes.getAll().any { it.title.endsWith(" (конфликт)") })
        a.syncWith(b)
        assertEquals(2, a.notes.getAll(true).size)
        assertEquals(2, b.notes.getAll(true).size)
    }

    @Test fun fileSync_streamsByteIdentical() {
        val a = Node("a"); val b = Node("b")
        val payload = ByteArray(3 * 1024 * 1024) { (it % 251).toByte() }
        val src = File(a.dir, "big.bin").also { it.writeBytes(payload) }
        val fid = a.files.addFile(
            FileEntry(name = "big.bin", mime = "application/octet-stream"),
            src.absolutePath)
        val r = a.syncWith(b)
        assertEquals(1, r.pushed)
        val got = b.files.getById(fid)!!
        assertEquals(payload.size.toLong(), got.sizeBytes)
        val want = HashUtils.sha256Hex(payload)
        assertEquals(want, got.sha256)
        // Байты материализованы и целы на приёмнике.
        assertEquals(want, HashUtils.sha256Hex(
            b.fio.asIface().openRead(got.sha256).readBytes()))
    }

    @Test fun tombstoneWithoutBytes_appliesCleanly() {
        val a = Node("a"); val b = Node("b")
        val src = File(a.dir, "x.bin").also { it.writeBytes(byteArrayOf(1, 2, 3)) }
        val fid = a.files.addFile(
            FileEntry(name = "x.bin", mime = "application/octet-stream"),
            src.absolutePath)
        a.files.softDelete(fid)
        // Байтов больше нет нигде — как после purge у позднего пира.
        a.files.map[fid]!!.let {
            a.fio.asIface().deleteFromStorage(it.sha256)
        }
        a.syncWith(b) // не должно упасть
        val got = b.files.getById(fid)!!
        assertTrue(got.isDeleted)
    }
}
