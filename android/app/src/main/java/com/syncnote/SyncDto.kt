package com.syncnote

// DTO сессии (зеркало protocol/pairing-and-sync.md и SyncNoteDto C#).
data class ChecklistItemDto(
    var position: Int = 0,
    var text: String = "",
    var isChecked: Boolean = false
)

data class AttachmentMetaDto(
    var fileName: String = "",
    var mimeType: String = "application/octet-stream",
    var sizeBytes: Long = 0,
    var sha256: String = ""
)

data class SyncNoteDto(
    var id: String = "",
    var rev: Long = 0,
    var baseRev: Long = 0,
    var title: String = "",
    var body: String = "",
    var updatedAt: Long = 0,
    var author: String = "",
    var isDeleted: Boolean = false,
    var checklist: List<ChecklistItemDto> = emptyList(),
    var attachments: List<AttachmentMetaDto> = emptyList()
)
