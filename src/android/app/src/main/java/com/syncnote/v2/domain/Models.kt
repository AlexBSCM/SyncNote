package com.syncnote.v2.domain

// Строка таблицы notes. Чистая модель без логики отображения.
data class NoteEntry(
    val id: String = "",
    val title: String = "",
    val body: String = "",
    val rev: Long = 1,
    val updatedAt: String = "",
    val authorDeviceId: String = "",
    val isDeleted: Boolean = false
)

// Строка таблицы files. Чистая модель без логики отображения.
data class FileEntry(
    val id: String = "",
    val name: String = "",
    val mime: String = "application/octet-stream",
    val sizeBytes: Long = 0,
    val sha256: String = "",
    val storedName: String = "",
    val rev: Long = 1,
    val updatedAt: String = "",
    val authorDeviceId: String = "",
    val isDeleted: Boolean = false
)
