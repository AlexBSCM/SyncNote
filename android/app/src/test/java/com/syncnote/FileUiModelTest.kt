package com.syncnote

import org.junit.Assert.*
import org.junit.Test
import java.util.TimeZone

// Чистые JVM-тесты маппинга FileEntry -> FileUiModel (без Android-рантайма):
// статусы, иконки, формат размера. R-константы инлайнятся компилятором.
class FileUiModelTest {
    private fun entry(
        name: String = "a.jpg",
        mime: String = "image/jpeg",
        size: Long = 1024,
        rev: Long = 1
    ) = FileEntry(name = name, mime = mime, sizeBytes = size,
        sha256 = "ab".repeat(32), storedName = "ab".repeat(32),
        rev = rev, updatedAt = 0L, authorDeviceId = "dev")

    @Test fun modified_jpg_mapsToWarnBadgeAndImageIcon() {
        val m = FileUiModel.from(entry(rev = 2), syncRev = 0)
        assertEquals(FileBadge.Modified, m.badge)
        assertTrue(m.isModified)
        assertFalse(m.isConflict)
        assertEquals("🖼️", m.iconEmoji)
        assertEquals(R.color.file_icon_image_bg, m.iconBgRes)
        assertEquals(R.color.file_icon_image_fg, m.iconFgRes)
        assertEquals(R.string.files_badge_modified, m.badge.textRes)
        assertEquals(R.color.files_warn_bg, m.badge.bgRes)
        assertEquals(R.color.files_warn_fg, m.badge.fgRes)
    }

    @Test fun synced_pdf_mapsToOkBadge() {
        val m = FileUiModel.from(
            entry(name = "c.pdf", mime = "application/pdf", rev = 1), syncRev = 1)
        assertEquals(FileBadge.Synced, m.badge)
        assertFalse(m.isModified)
        assertEquals("📕", m.iconEmoji)
        assertEquals(R.color.file_icon_pdf_bg, m.iconBgRes)
        assertEquals(R.string.files_badge_synced, m.badge.textRes)
    }

    @Test fun conflictSuffix_mapsToErrBadge() {
        val m = FileUiModel.from(
            entry(name = "a.jpg (конфликт)", rev = 5), syncRev = 1)
        assertEquals(FileBadge.Conflict, m.badge)
        assertTrue(m.isConflict)
        assertFalse(m.isModified)
        assertEquals(R.color.files_err_bg, m.badge.bgRes)
    }

    @Test fun icons_coverMimeTypes() {
        assertEquals("🎧", FileUiModel.iconFor("audio/mpeg").first)
        assertEquals("🎬", FileUiModel.iconFor("video/mp4").first)
        assertEquals("📦", FileUiModel.iconFor("application/zip").first)
        assertEquals("📄", FileUiModel.iconFor("text/plain").first)
        assertEquals("📎", FileUiModel.iconFor("application/octet-stream").first)
        val (emoji, bg, fg) = FileUiModel.iconFor("application/octet-stream")
        assertEquals("📎", emoji)
        assertEquals(R.color.file_icon_default_bg, bg)
        assertEquals(R.color.file_icon_default_fg, fg)
    }

    @Test fun formatSize_matchesDesktopBuckets() {
        assertEquals("0 Б", FileUiModel.formatSize(0))
        assertEquals("1023 Б", FileUiModel.formatSize(1023))
        assertEquals("1.0 КБ", FileUiModel.formatSize(1024))
        assertEquals("262.7 КБ", FileUiModel.formatSize(269022))
        assertEquals("1.0 МБ", FileUiModel.formatSize(1024 * 1024))
        assertEquals("90.0 МБ", FileUiModel.formatSize(90L * 1024 * 1024))
    }

    @Test fun metaLine_combinesSizeAndDate() {
        val tz = TimeZone.getDefault()
        try {
            TimeZone.setDefault(TimeZone.getTimeZone("UTC"))
            // 2026-09-28 12:51 UTC
            val millis = 1790599860000L
            val m = FileUiModel.from(entry(size = 269022).copy(updatedAt = millis), syncRev = 1)
            assertEquals("262.7 КБ • 28.09.2026 12:51", m.metaLine)
        } finally {
            TimeZone.setDefault(tz)
        }
    }
}
