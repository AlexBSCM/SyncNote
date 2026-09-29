package com.syncnote.v2.domain

import java.io.File
import java.io.InputStream

// Seam-ы движка синхронизации — паритет C# INoteRepo/IFileRepo/IFileIo/ISyncStateStore.
// Реализации: SQL/FileIo (data/), тесты: in-memory фейки.
interface NoteRepo {
    fun getById(id: String): NoteEntry?
    fun getAll(includeDeleted: Boolean = false): List<NoteEntry>
    fun create(note: NoteEntry): String
    fun update(note: NoteEntry)
    fun softDelete(id: String)
    fun insertFull(e: NoteEntry)
    fun updateFull(e: NoteEntry)
}

interface FileRepo {
    fun getById(id: String): FileEntry?
    fun getAll(includeDeleted: Boolean = false): List<FileEntry>
    fun addFile(metadata: FileEntry, sourcePath: String): String
    fun softDelete(id: String)
    fun insertFull(e: FileEntry)
    fun updateFull(e: FileEntry)
}

interface FileIo {
    fun import(sourcePath: String): Pair<String, Long>
    fun existsInStorage(sha256: String): Boolean
    fun openRead(sha256: String): InputStream
    fun deleteFromStorage(sha256: String)
    fun getStoragePath(sha256: String): File
}

interface SyncState {
    fun getSyncRev(entityType: String, entityId: String): Long
    fun setSyncRev(entityType: String, entityId: String, rev: Long)
    fun noteSeenConflict(originalId: String, rev: Long, contentHash: String): Boolean
    fun hasLiveFileRef(sha256: String): Boolean
}
