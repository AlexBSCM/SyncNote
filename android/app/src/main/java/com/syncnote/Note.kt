package com.syncnote

import java.util.UUID

// Минимальная модель (зеркало SyncNote.Core). Ревизии и sync будут на этапе sync.
data class Note(
    val id: String = UUID.randomUUID().toString().replace("-", ""),
    var rev: Long = 1,
    var title: String = "",
    var body: String = "",
    var updatedAt: Long = System.currentTimeMillis(),
    var authorDeviceId: String = "",
    var isDeleted: Boolean = false
)

data class ChecklistItem(
    val id: String = UUID.randomUUID().toString().replace("-", ""),
    val noteId: String = "",
    var position: Int = 0,
    var text: String = "",
    var isChecked: Boolean = false
)

data class Attachment(
    val id: String = UUID.randomUUID().toString().replace("-", ""),
    val noteId: String = "",
    var fileName: String = "",
    var mimeType: String = "application/octet-stream",
    var sizeBytes: Long = 0,
    var sha256: String = "",
    var storedName: String = ""
)
