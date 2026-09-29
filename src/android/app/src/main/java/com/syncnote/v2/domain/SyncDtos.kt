package com.syncnote.v2.domain

// DTO провода. Имена полей JSON — байт-в-байт как C# NoteDto/FileDto.
// stored_name намеренно нет (получатель выводит имя из sha256).
data class NoteDto(
    val id: String = "",
    val title: String = "",
    val body: String = "",
    val rev: Long = 0,
    val updatedAt: String = "",
    val author: String = "",
    val isDeleted: Boolean = false,
    val baseRev: Long = 0
)

data class FileDto(
    val id: String = "",
    val name: String = "",
    val mime: String = "application/octet-stream",
    val size: Long = 0,
    val sha256: String = "",
    val rev: Long = 0,
    val updatedAt: String = "",
    val author: String = "",
    val isDeleted: Boolean = false,
    val baseRev: Long = 0
)
