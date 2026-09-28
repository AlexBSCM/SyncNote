package com.syncnote

// DTO отдельного файла для протокола (JSON-совместимо).
// BaseRev — ревизия отправителя (его file_syncstate для этого файла).
data class SyncFileDto(
    var id: String = "",
    var rev: Long = 0,
    var baseRev: Long = 0,
    var name: String = "",
    var mime: String = "application/octet-stream",
    var size: Long = 0,
    var sha256: String = "",
    var updatedAt: Long = 0,
    var author: String = "",
    var isDeleted: Boolean = false
)

// Строка таблицы files.
data class FileEntry(
    val id: String = java.util.UUID.randomUUID().toString().replace("-", ""),
    var name: String = "",
    var mime: String = "application/octet-stream",
    var sizeBytes: Long = 0,
    var sha256: String = "",
    var storedName: String = "",
    var rev: Long = 1,
    var updatedAt: Long = System.currentTimeMillis(),
    var authorDeviceId: String = "",
    var isDeleted: Boolean = false
)
