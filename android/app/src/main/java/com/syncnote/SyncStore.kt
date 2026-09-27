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
}
