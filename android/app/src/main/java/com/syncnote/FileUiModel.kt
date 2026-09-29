package com.syncnote

import androidx.annotation.ColorRes
import androidx.annotation.StringRes
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

// Бейдж статуса синхронизации. Текст — из strings.xml (приёмка запрещает
// хардкод строк), цвета — из colors.xml. Резолвится в адаптере через Context.
enum class FileBadge(
    @StringRes val textRes: Int,
    @ColorRes val bgRes: Int,
    @ColorRes val fgRes: Int
) {
    Modified(R.string.files_badge_modified, R.color.files_warn_bg, R.color.files_warn_fg),
    Synced(R.string.files_badge_synced, R.color.files_ok_bg, R.color.files_ok_fg),
    Conflict(R.string.files_badge_conflict, R.color.files_err_bg, R.color.files_err);
}

// Строка списка файлов для RecyclerView. Чистый маппинг из FileEntry,
// без Android-фреймворка внутри (кроме R-констант) — покрыт JVM-тестом.
// Иконка — эмодзи как на десктопе (без бинарных ассетов);
// цвета подложки — из ресурсов, hex в коде нет.
data class FileUiModel(
    val id: String,
    val name: String,
    val mime: String,
    /** "262.7 КБ • 28.09.2026 12:51" */
    val metaLine: String,
    val iconEmoji: String,
    @ColorRes val iconBgRes: Int,
    @ColorRes val iconFgRes: Int,
    val badge: FileBadge,
    val isConflict: Boolean,
    val isModified: Boolean
) {
    companion object {
        /** Суффикс копии конфликта — тот же, что кладёт SyncEngine (обе платформы). */
        const val CONFLICT_SUFFIX = " (конфликт)"

        fun from(e: FileEntry, syncRev: Long): FileUiModel {
            val conflict = e.name.endsWith(CONFLICT_SUFFIX)
            val modified = !conflict && e.rev > syncRev
            val badge = when {
                conflict -> FileBadge.Conflict
                modified -> FileBadge.Modified
                else -> FileBadge.Synced
            }
            val (emoji, bg, fg) = iconFor(e.mime)
            return FileUiModel(
                id = e.id,
                name = e.name,
                mime = e.mime,
                metaLine = "${formatSize(e.sizeBytes)} • ${formatDate(e.updatedAt)}",
                iconEmoji = emoji,
                iconBgRes = bg,
                iconFgRes = fg,
                badge = badge,
                isConflict = conflict,
                isModified = modified
            )
        }

        fun iconFor(mime: String): Triple<String, Int, Int> = when {
            mime.startsWith("image/") -> Triple("🖼️", R.color.file_icon_image_bg, R.color.file_icon_image_fg)
            mime.startsWith("audio/") -> Triple("🎧", R.color.file_icon_audio_bg, R.color.file_icon_audio_fg)
            mime.startsWith("video/") -> Triple("🎬", R.color.file_icon_audio_bg, R.color.file_icon_audio_fg)
            mime == "application/pdf" -> Triple("📕", R.color.file_icon_pdf_bg, R.color.file_icon_pdf_fg)
            mime == "application/zip" -> Triple("📦", R.color.file_icon_audio_bg, R.color.file_icon_audio_fg)
            mime.startsWith("text/") -> Triple("📄", R.color.file_icon_default_bg, R.color.file_icon_default_fg)
            else -> Triple("📎", R.color.file_icon_default_bg, R.color.file_icon_default_fg)
        }

        /** Как десктопный FormattedSize, но детерминированная локаль (тестируемо). */
        fun formatSize(bytes: Long): String = when {
            bytes < 1024 -> "$bytes Б"
            bytes < 1024 * 1024 -> String.format(Locale.US, "%.1f КБ", bytes / 1024.0)
            else -> String.format(Locale.US, "%.1f МБ", bytes / (1024.0 * 1024))
        }

        fun formatDate(millis: Long): String =
            SimpleDateFormat("dd.MM.yyyy HH:mm", Locale.getDefault()).format(Date(millis))
    }
}
