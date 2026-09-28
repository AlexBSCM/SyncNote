package com.syncnote

import java.io.File

// Порт SyncEngine C#: идемпотентное применение, fast-forward, копии конфликтов.
enum class ApplyResult { Inserted, FastForwarded, NoOp, Conflict }

data class ConflictInfo(val originalId: String, val copyId: String, val copyTitle: String)

object SyncEngine {
    const val COPY_SUFFIX = " (копия конфликта)"

    fun apply(
        store: SyncStore,
        dto: SyncNoteDto,
        fileBytes: (String) -> ByteArray? = { null }
    ): Pair<ApplyResult, ConflictInfo?> {
        val local = store.tryGet(dto.id)
        if (local == null) {
            store.importFull(dto, fileBytes)
            store.setSyncRev(dto.id, dto.rev)
            return ApplyResult.Inserted to null
        }
        val syncRev = store.getSyncRev(dto.id)
        if (dto.rev == local.rev) {
            if (sameContent(store, local, dto)) {
                store.setSyncRev(dto.id, maxOf(syncRev, dto.rev))
                return ApplyResult.NoOp to null
            }
            val hash = contentHash(dto)
            if (store.noteSeenConflict(dto.id, dto.rev, hash)) {
                store.setSyncRev(dto.id, maxOf(syncRev, dto.rev))
                return ApplyResult.NoOp to null
            }
            return makeCopy(store, dto, fileBytes)
        }
        if (dto.rev < local.rev) return ApplyResult.NoOp to null
        if (local.rev == syncRev || dto.baseRev >= syncRev) {
            store.importFull(dto, fileBytes)
            store.setSyncRev(dto.id, dto.rev)
            return ApplyResult.FastForwarded to null
        }
        val incomingHash = contentHash(dto)
        if (store.noteSeenConflict(dto.id, dto.rev, incomingHash))
            return ApplyResult.NoOp to null
        return makeCopy(store, dto, fileBytes)
    }

    fun contentHash(dto: SyncNoteDto): String {
        val sb = StringBuilder()
        sb.append(dto.title).append('\u0000').append(dto.body).append('\u0000')
            .append(if (dto.isDeleted) '1' else '0').append('\u0000')
        for (c in dto.checklist.sortedBy { it.position })
            sb.append(c.position).append(':').append(c.text).append(':')
                .append(if (c.isChecked) '1' else '0').append('\u0000')
        for (s in dto.attachments.map { it.sha256 }.sorted())
            sb.append(s).append('\u0000')
        val md = java.security.MessageDigest.getInstance("SHA-256")
        return md.digest(sb.toString().toByteArray(Charsets.UTF_8))
            .joinToString("") { "%02x".format(it) }
    }

    private fun makeCopy(
        store: SyncStore,
        dto: SyncNoteDto,
        fileBytes: (String) -> ByteArray?
    ): Pair<ApplyResult, ConflictInfo?> {
        val copy = dto.copy(
            id = java.util.UUID.randomUUID().toString().replace("-", ""),
            rev = 1, baseRev = 0,
            title = dto.title + COPY_SUFFIX,
            isDeleted = false)
        store.importFull(copy, fileBytes)
        store.setSyncRev(copy.id, 1)
        return ApplyResult.Conflict to ConflictInfo(dto.id, copy.id, copy.title)
    }

    // --- Отдельные файлы (этап F): зеркало apply для SyncFileDto. ---
    // Независимый путь: методы заметок здесь не вызываются.

    fun applyFile(
        store: SyncStore,
        dto: SyncFileDto,
        fileBytes: (String) -> ByteArray? = { null }
    ): Pair<ApplyResult, ConflictInfo?> {
        val local = store.tryGetFile(dto.id)
        if (local == null) {
            ensureFileBytes(store, dto, fileBytes)
            store.insertFullFile(dto)
            store.setFileSyncRev(dto.id, dto.rev)
            return ApplyResult.Inserted to null
        }
        val syncRev = store.getFileSyncRev(dto.id)
        if (dto.rev == local.rev) {
            if (sameFileContent(local, dto)) {
                store.setFileSyncRev(dto.id, maxOf(syncRev, dto.rev))
                return ApplyResult.NoOp to null
            }
            val hash = fileContentHash(dto)
            if (store.noteSeenConflict(dto.id, dto.rev, hash)) {
                store.setFileSyncRev(dto.id, maxOf(syncRev, dto.rev))
                return ApplyResult.NoOp to null
            }
            return makeFileConflictCopy(store, dto, fileBytes)
        }
        if (dto.rev < local.rev) return ApplyResult.NoOp to null
        if (local.rev == syncRev || dto.baseRev >= syncRev) {
            ensureFileBytes(store, dto, fileBytes)
            store.updateFullFile(dto)
            store.setFileSyncRev(dto.id, dto.rev)
            return ApplyResult.FastForwarded to null
        }
        val incomingHash = fileContentHash(dto)
        if (store.noteSeenConflict(dto.id, dto.rev, incomingHash))
            return ApplyResult.NoOp to null
        return makeFileConflictCopy(store, dto, fileBytes)
    }

    fun fileContentHash(dto: SyncFileDto): String {
        val s = listOf(dto.name, dto.mime, dto.size.toString(), dto.sha256,
            if (dto.isDeleted) "1" else "0").joinToString("\u0000")
        val md = java.security.MessageDigest.getInstance("SHA-256")
        return md.digest(s.toByteArray(Charsets.UTF_8))
            .joinToString("") { "%02x".format(it) }
    }

    private fun sameFileContent(local: FileEntry, dto: SyncFileDto): Boolean =
        local.name == dto.name && local.mime == dto.mime
            && local.sizeBytes == dto.size
            && local.sha256.equals(dto.sha256, ignoreCase = true)
            && local.isDeleted == dto.isDeleted

    private fun ensureFileBytes(
        store: SyncStore, dto: SyncFileDto, fileBytes: (String) -> ByteArray?
    ) {
        val norm = dto.sha256.lowercase()
        if (java.io.File(store.filesDir(), norm).exists()) return
        val bytes = fileBytes(norm)
            ?: throw IllegalStateException("Нет байтов файла (sha256 $norm).")
        val md = java.security.MessageDigest.getInstance("SHA-256")
        val actual = md.digest(bytes).joinToString("") { "%02x".format(it) }
        if (actual != norm)
            throw IllegalStateException("sha256 не сошлось для $norm.")
        val tmp = File.createTempFile("fileio-", ".part", store.filesDir())
        try {
            tmp.writeBytes(bytes)
            if (!tmp.renameTo(java.io.File(store.filesDir(), norm))) {
                tmp.delete()
                throw java.io.IOException("Не удалось сохранить файл ${dto.name}.")
            }
        } catch (e: Exception) {
            try { tmp.delete() } catch (_: Exception) { }
            throw e
        }
    }

    private fun makeFileConflictCopy(
        store: SyncStore,
        dto: SyncFileDto,
        fileBytes: (String) -> ByteArray?
    ): Pair<ApplyResult, ConflictInfo?> {
        ensureFileBytes(store, dto, fileBytes)
        val copy = dto.copy(
            id = java.util.UUID.randomUUID().toString().replace("-", ""),
            rev = 1, baseRev = 0,
            name = dto.name + " (конфликт)",
            isDeleted = false)
        store.insertFullFile(copy)
        store.setFileSyncRev(copy.id, 1)
        return ApplyResult.Conflict to ConflictInfo(dto.id, copy.id, copy.name)
    }

    private fun sameContent(store: SyncStore, local: Note, dto: SyncNoteDto): Boolean {
        if (local.title != dto.title || local.body != dto.body || local.isDeleted != dto.isDeleted)
            return false
        val items = store.checklist(local.id).sortedBy { it.position }
        val want = dto.checklist.sortedBy { it.position }
        if (items.size != want.size) return false
        for (k in items.indices)
            if (items[k].text != want[k].text || items[k].isChecked != want[k].isChecked)
                return false
        val haveFiles = store.attachments(local.id).map { it.sha256 }.sorted()
        val wantFiles = dto.attachments.map { it.sha256 }.sorted()
        return haveFiles == wantFiles
    }
}
