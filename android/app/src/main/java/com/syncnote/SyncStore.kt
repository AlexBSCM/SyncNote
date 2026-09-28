package com.syncnote

import java.io.File

// Абстракция хранилища для движка (реализация — NotesRepository, в тестах — FakeStore).
interface SyncStore {
    fun tryGet(id: String): Note?
    fun getSyncRev(id: String): Long
    fun setSyncRev(id: String, rev: Long)
    fun exportAll(): List<SyncNoteDto>
    fun importFull(dto: SyncNoteDto, fileBytes: (String) -> ByteArray?)
    fun checklist(noteId: String): List<ChecklistItem>
    fun attachments(noteId: String): List<Attachment>
    fun deviceId(): String
    fun filesDir(): File
    fun noteSeenConflict(id: String, rev: Long, contentHash: String): Boolean
    fun getFiles(includeDeleted: Boolean = false): List<FileEntry>
    fun tryGetFile(id: String): FileEntry?
    fun insertFullFile(dto: SyncFileDto)
    fun updateFullFile(dto: SyncFileDto)
    fun addFile(name: String, mime: String, content: ByteArray): FileEntry
    fun deleteFile(id: String): Boolean
    fun getFileSyncRev(fileId: String): Long
    fun setFileSyncRev(fileId: String, rev: Long)
    fun hasLiveReferencesToSha(sha256: String): Boolean
    fun exportFiles(): List<SyncFileDto>
    fun applyFile(dto: SyncFileDto, fileBytes: (String) -> ByteArray?): Pair<ApplyResult, ConflictInfo?> {
        return SyncEngine.applyFile(this, dto, fileBytes)
    }
}
