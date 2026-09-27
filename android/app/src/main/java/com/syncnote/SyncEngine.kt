package com.syncnote

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
            return makeCopy(store, dto, fileBytes)
        }
        if (dto.rev < local.rev) return ApplyResult.NoOp to null
        if (local.rev == syncRev || dto.baseRev >= syncRev) {
            store.importFull(dto, fileBytes)
            store.setSyncRev(dto.id, dto.rev)
            return ApplyResult.FastForwarded to null
        }
        return makeCopy(store, dto, fileBytes)
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
