package com.syncnote

import android.content.ContentValues
import android.content.Context
import java.io.File

// CRUD + поиск (регистронезависимо, через norm-колонки).
class NotesRepository(ctx: Context) : SyncStore {
    private val db = NotesDbHelper(ctx)
    // Раньше ошибочно использовался подкаталог files/files — чиним с переездом.
    private val filesRoot: File = ctx.filesDir.also { migrateFilesDir(it) }
    override fun filesDir(): File = filesRoot
    override fun deviceId(): String = db.deviceId()

    init {
        migrateAttachmentsToSha()
        sweepOrphanFiles()
    }

    // Миграция вложений на content-addressed хранение (stored_name == sha256):
    // файлы со старыми именами {id}_{name} переименовываем, строки обновляем.
    // Построчная изоляция ошибок; физически отсутствующие пропускаем —
    // их байты придут повторной синхронизацией.
    private fun migrateAttachmentsToSha() {
        try {
            val rows = mutableListOf<Triple<String, String, String>>()
            db.readableDatabase.rawQuery(
                "SELECT id, stored_name, sha256 FROM attachments", null).use { c ->
                while (c.moveToNext())
                    rows += Triple(c.getString(0), c.getString(1), c.getString(2).lowercase())
            }
            for ((id, stored, sha) in rows) {
                try {
                    if (stored == sha) continue
                    val src = File(filesRoot, stored)
                    val dst = File(filesRoot, sha)
                    if (!dst.exists() && src.exists()) src.renameTo(dst)
                    if (src.exists() && dst.exists() && sha256file(src) == sha) {
                        try { src.delete() } catch (_: Exception) { }
                    }
                    if (dst.exists()) {
                        db.writableDatabase.execSQL(
                            "UPDATE attachments SET stored_name=? WHERE id=?",
                            arrayOf(sha, id))
                    }
                } catch (_: Exception) { }
            }
        } catch (_: Exception) { }
    }

    private fun sha256file(f: File): String {
        val md = java.security.MessageDigest.getInstance("SHA-256")
        f.inputStream().buffered().use { src ->
            val buf = ByteArray(1024 * 1024)
            while (true) {
                val n = src.read(buf)
                if (n <= 0) break
                md.update(buf, 0, n)
            }
        }
        return md.digest().joinToString("") { "%02x".format(it) }
    }

    // Удаляем файлы без метаданных (обрывы, дубли после слияния id).
    // Учитываем ОБЕ подсистемы: вложения (attachments) и отдельные файлы
    // (files, ключ — stored_name/sha). Иначе старт приложения стирал бы
    // байты синхронизированных файлов, оставляя строки-сироты без тела.
    // Безопасность: ЛЮБАЯ ошибка чтения БД = полный отказ от чистки
    // (частичный known приводил к удалению живых файлов при занятой БД),
    // свежие файлы (< 60 c) не трогаем — их могла только что записать
    // параллельная сессия/импорт после нашего снимка known.
    private fun sweepOrphanFiles() {
        try {
            val known = mutableSetOf<String>()
            db.readableDatabase.rawQuery("SELECT stored_name FROM attachments", null).use { c ->
                while (c.moveToNext()) known += c.getString(0)
            }
            db.readableDatabase.rawQuery("SELECT stored_name FROM files", null).use { c ->
                while (c.moveToNext()) known += c.getString(0)
            }
            val now = System.currentTimeMillis()
            filesRoot.listFiles()?.forEach { f ->
                if (f.isFile && f.name !in known && !f.name.endsWith(".tmp") &&
                    now - f.lastModified() > 60_000
                ) {
                    try { f.delete() } catch (_: Exception) { }
                }
            }
        } catch (_: Exception) {
            // Неполное состояние — ничего не удаляем.
        }
    }

    fun list(): List<Note> = query(null)

    fun listAll(): List<Note> = list()

    fun deleteNotes(ids: List<String>): Int {
        var n = 0
        for (id in ids) if (delete(id)) n++
        return n
    }

    fun search(q: String): List<Note> =
        if (q.isBlank()) list() else query(q.lowercase())

    private fun query(norm: String?): List<Note> {
        val out = mutableListOf<Note>()
        val sql = if (norm == null)
            "SELECT id, rev, title, body, updated_at, author_device FROM notes WHERE is_deleted=0 ORDER BY updated_at DESC"
        else
            "SELECT id, rev, title, body, updated_at, author_device FROM notes WHERE is_deleted=0 " +
                "AND (title_norm LIKE ? ESCAPE '\\' OR body_norm LIKE ? ESCAPE '\\') ORDER BY updated_at DESC"
        val args = if (norm == null) null else arrayOf(
            "%${norm.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")}%",
            "%${norm.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")}%")
        db.readableDatabase.rawQuery(sql, args).use { c ->
            while (c.moveToNext()) {
                out += Note(
                    id = c.getString(0), rev = c.getLong(1),
                    title = c.getString(2), body = c.getString(3),
                    updatedAt = c.getLong(4), authorDeviceId = c.getString(5))
            }
        }
        return out
    }

    fun get(id: String): Note? {
        db.readableDatabase.rawQuery(
            "SELECT id, rev, title, body, updated_at, author_device, is_deleted FROM notes WHERE id=?",
            arrayOf(id)).use { c ->
            if (!c.moveToFirst()) return null
            return Note(id = c.getString(0), rev = c.getLong(1),
                title = c.getString(2), body = c.getString(3),
                updatedAt = c.getLong(4), authorDeviceId = c.getString(5),
                isDeleted = c.getInt(6) != 0)
        }
    }

    fun add(title: String, body: String): Note {
        val n = Note(title = title, body = body,
            updatedAt = System.currentTimeMillis(), authorDeviceId = deviceId())
        val v = ContentValues().apply {
            put("id", n.id); put("rev", 1); put("title", title); put("body", body)
            put("updated_at", n.updatedAt); put("author_device", n.authorDeviceId)
            put("is_deleted", 0)
            put("title_norm", title.lowercase()); put("body_norm", body.lowercase())
        }
        db.writableDatabase.insertOrThrow("notes", null, v)
        return n
    }

    fun update(note: Note) {
        note.rev += 1
        note.updatedAt = System.currentTimeMillis()
        note.authorDeviceId = deviceId()
        val v = ContentValues().apply {
            put("rev", note.rev); put("title", note.title); put("body", note.body)
            put("updated_at", note.updatedAt); put("author_device", note.authorDeviceId)
            put("title_norm", note.title.lowercase()); put("body_norm", note.body.lowercase())
        }
        val rows = db.writableDatabase.update("notes", v, "id=? AND is_deleted=0",
            arrayOf(note.id))
        if (rows == 0) throw NoSuchElementException("Note ${note.id} not found")
    }

    fun delete(id: String): Boolean {
        val v = ContentValues().apply {
            put("is_deleted", 1)
            put("updated_at", System.currentTimeMillis())
        }
        // Ревизию увеличиваем отдельным запросом (атомарно в транзакции).
        db.writableDatabase.beginTransaction()
        try {
            db.writableDatabase.execSQL(
                "UPDATE notes SET rev = rev + 1 WHERE id=? AND is_deleted=0", arrayOf(id))
            val rows = db.writableDatabase.update("notes", v, "id=? AND is_deleted=0",
                arrayOf(id))
            db.writableDatabase.setTransactionSuccessful()
            return rows > 0
        } finally {
            db.writableDatabase.endTransaction()
        }
    }

    override fun checklist(noteId: String): List<ChecklistItem> {
        val out = mutableListOf<ChecklistItem>()
        db.readableDatabase.rawQuery(
            "SELECT id, note_id, position, text, is_checked FROM checklist_items WHERE note_id=? ORDER BY position",
            arrayOf(noteId)).use { c ->
            while (c.moveToNext())
                out += ChecklistItem(id = c.getString(0), noteId = c.getString(1),
                    position = c.getInt(2), text = c.getString(3),
                    isChecked = c.getInt(4) != 0)
        }
        return out
    }

    override fun attachments(noteId: String): List<Attachment> {
        val out = mutableListOf<Attachment>()
        db.readableDatabase.rawQuery(
            "SELECT id, note_id, file_name, mime, size, sha256, stored_name FROM attachments WHERE note_id=? ORDER BY file_name",
            arrayOf(noteId)).use { c ->
            while (c.moveToNext())
                out += Attachment(id = c.getString(0), noteId = c.getString(1),
                    fileName = c.getString(2), mimeType = c.getString(3),
                    sizeBytes = c.getLong(4), sha256 = c.getString(5),
                    storedName = c.getString(6))
        }
        return out
    }

    fun addChecklistItem(noteId: String, text: String): ChecklistItem {
        var pos = 0
        db.readableDatabase.rawQuery(
            "SELECT COALESCE(MAX(position), -1) + 1 FROM checklist_items WHERE note_id=?",
            arrayOf(noteId)).use { c ->
            if (c.moveToFirst()) pos = c.getInt(0)
        }
        val item = ChecklistItem(noteId = noteId, position = pos, text = text)
        val v = android.content.ContentValues().apply {
            put("id", item.id); put("note_id", noteId); put("position", pos)
            put("text", text); put("is_checked", 0)
        }
        db.writableDatabase.insertOrThrow("checklist_items", null, v)
        touchNote(noteId)
        return item
    }

    fun updateChecklistItem(item: ChecklistItem) {
        val v = android.content.ContentValues().apply {
            put("position", item.position); put("text", item.text)
            put("is_checked", if (item.isChecked) 1 else 0)
        }
        val rows = db.writableDatabase.update("checklist_items", v, "id=?",
            arrayOf(item.id))
        if (rows == 0) throw NoSuchElementException("Checklist item ${item.id} not found")
        touchNote(item.noteId)
    }

    fun deleteChecklistItem(itemId: String, noteId: String): Boolean {
        val rows = db.writableDatabase.delete("checklist_items", "id=?", arrayOf(itemId))
        if (rows > 0) touchNote(noteId)
        return rows > 0
    }

    fun deleteAttachment(id: String): Boolean {
        val pair = db.readableDatabase.rawQuery(
            "SELECT note_id, stored_name FROM attachments WHERE id=?",
            arrayOf(id)).use { c ->
            if (!c.moveToFirst()) return false
            c.getString(0) to c.getString(1)
        }
        db.writableDatabase.delete("attachments", "id=?", arrayOf(id))
        try { File(filesRoot, pair.second).delete() } catch (_: Exception) { }
        touchNote(pair.first)
        return true
    }

    override fun tryGet(id: String): Note? = get(id)

    override fun getSyncRev(noteId: String): Long {
        db.readableDatabase.rawQuery(
            "SELECT sync_rev FROM syncstate WHERE note_id=?", arrayOf(noteId)).use { c ->
            if (!c.moveToFirst()) return 0
            return c.getLong(0)
        }
    }

    override fun setSyncRev(noteId: String, rev: Long) {
        db.writableDatabase.execSQL(
            "INSERT OR REPLACE INTO syncstate(note_id, sync_rev) VALUES(?, ?)",
            arrayOf(noteId, rev))
    }

    override fun noteSeenConflict(id: String, rev: Long, contentHash: String): Boolean {
        db.readableDatabase.rawQuery(
            "SELECT 1 FROM seen_conflicts WHERE note_id=? AND rev=? AND content_hash=?",
            arrayOf(id, rev.toString(), contentHash)).use { c ->
            if (c.count > 0) return true
        }
        db.writableDatabase.execSQL(
            "INSERT OR IGNORE INTO seen_conflicts(note_id, rev, content_hash) VALUES(?, ?, ?)",
            arrayOf(id, rev, contentHash))
        return false
    }

    private fun requireFilesEnabled() {
        if (!FeatureFlags.enableSeparateFiles)
            throw IllegalStateException("Подсистема отдельных файлов отключена флагом EnableSeparateFiles.")
    }

    override fun getFiles(includeDeleted: Boolean): List<FileEntry> {
        val out = mutableListOf<FileEntry>()
        val sql = "SELECT id, name, mime, size, sha256, stored_name, rev, updated_at, author_device, is_deleted " +
            "FROM files " + (if (includeDeleted) "" else "WHERE is_deleted=0 ") + "ORDER BY name"
        db.readableDatabase.rawQuery(sql, null).use { c ->
            while (c.moveToNext())
                out += FileEntry(id = c.getString(0), name = c.getString(1),
                    mime = c.getString(2), sizeBytes = c.getLong(3),
                    sha256 = c.getString(4), storedName = c.getString(5),
                    rev = c.getLong(6), updatedAt = c.getLong(7),
                    authorDeviceId = c.getString(8), isDeleted = c.getInt(9) != 0)
        }
        return out
    }

    override fun tryGetFile(id: String): FileEntry? {
        db.readableDatabase.rawQuery(
            "SELECT id, name, mime, size, sha256, stored_name, rev, updated_at, author_device, is_deleted " +
                "FROM files WHERE id=?", arrayOf(id)).use { c ->
            if (!c.moveToFirst()) return null
            return FileEntry(id = c.getString(0), name = c.getString(1),
                mime = c.getString(2), sizeBytes = c.getLong(3),
                sha256 = c.getString(4), storedName = c.getString(5),
                rev = c.getLong(6), updatedAt = c.getLong(7),
                authorDeviceId = c.getString(8), isDeleted = c.getInt(9) != 0)
        }
    }

    override fun addFile(name: String, mime: String, content: ByteArray): FileEntry {
        requireFilesEnabled()
        if (content.size > FileIo.MAX_FILE_SIZE_BYTES)
            throw FileTooLargeException(name, content.size.toLong(), FileIo.MAX_FILE_SIZE_BYTES)
        val md = java.security.MessageDigest.getInstance("SHA-256")
        val hex = md.digest(content).joinToString("") { "%02x".format(it) }
        val dest = File(filesRoot, hex)
        if (!dest.exists()) {
            val tmp = File.createTempFile("fileio-", ".part", filesRoot)
            try {
                tmp.writeBytes(content)
                if (!tmp.renameTo(dest)) {
                    tmp.delete()
                    throw java.io.IOException("Не удалось сохранить файл $name.")
                }
            } catch (e: Exception) {
                try { tmp.delete() } catch (_: Exception) { }
                throw e
            }
        }
        val entry = FileEntry(name = name, mime = mime,
            sizeBytes = content.size.toLong(), sha256 = hex, storedName = hex,
            rev = 1, updatedAt = System.currentTimeMillis(), authorDeviceId = deviceId())
        db.writableDatabase.execSQL(
            "INSERT INTO files(id, name, mime, size, sha256, stored_name, rev, updated_at, author_device, is_deleted) " +
                "VALUES(?, ?, ?, ?, ?, ?, 1, ?, ?, 0)",
            arrayOf(entry.id, name, mime, content.size.toLong(), hex, hex,
                entry.updatedAt, entry.authorDeviceId))
        return entry
    }

    override fun deleteFile(id: String): Boolean {
        requireFilesEnabled()
        db.writableDatabase.beginTransaction()
        try {
            db.writableDatabase.execSQL(
                "UPDATE files SET is_deleted=1, rev=rev+1, updated_at=? WHERE id=? AND is_deleted=0",
                arrayOf(System.currentTimeMillis().toString(), id))
            // affected-rows через changes()
            var rows = 0
            db.readableDatabase.rawQuery("SELECT changes()", null).use { c ->
                if (c.moveToFirst()) rows = c.getInt(0)
            }
            db.writableDatabase.setTransactionSuccessful()
            return rows > 0
        } finally {
            db.writableDatabase.endTransaction()
        }
    }

    override fun getFileSyncRev(fileId: String): Long {
        db.readableDatabase.rawQuery(
            "SELECT sync_rev FROM file_syncstate WHERE file_id=?", arrayOf(fileId)).use { c ->
            if (!c.moveToFirst()) return 0
            return c.getLong(0)
        }
    }

    override fun setFileSyncRev(fileId: String, rev: Long) {
        db.writableDatabase.execSQL(
            "INSERT OR REPLACE INTO file_syncstate(file_id, sync_rev) VALUES(?, ?)",
            arrayOf(fileId, rev))
    }

    override fun hasLiveReferencesToSha(sha256: String): Boolean {
        db.readableDatabase.rawQuery(
            "SELECT 1 FROM files WHERE sha256=? AND is_deleted=0 LIMIT 1",
            arrayOf(sha256.lowercase())).use { c ->
            return c.count > 0
        }
    }

    override fun sweepOrphanedFiles(): Int {
        if (!FeatureFlags.enableSeparateFiles) return 0
        data class Victim(val sha: String, val stored: String)
        val victims = mutableListOf<Victim>()
        db.readableDatabase.rawQuery(
            "SELECT sha256, stored_name FROM files WHERE is_deleted=1", null).use { c ->
            while (c.moveToNext()) victims += Victim(c.getString(0), c.getString(1))
        }
        var removed = 0
        for ((sha, stored) in victims) {
            // Живая ссылка осталась (другая строка с тем же sha) — не трогаем.
            if (hasLiveReferencesToSha(sha)) continue
            val f = File(filesRoot, stored)
            if (!f.exists()) continue
            try {
                if (f.delete()) removed++
            } catch (_: Exception) { }
        }
        return removed
    }

    override fun exportFiles(): List<SyncFileDto> {
        requireFilesEnabled()
        val ids = mutableListOf<String>()
        db.readableDatabase.rawQuery("SELECT id FROM files", null).use { c ->
            while (c.moveToNext()) ids += c.getString(0)
        }
        return ids.mapNotNull { id ->
            val e = tryGetFile(id) ?: return@mapNotNull null
            SyncFileDto(id = e.id, rev = e.rev, baseRev = getFileSyncRev(id),
                name = e.name, mime = e.mime, size = e.sizeBytes,
                sha256 = e.sha256, updatedAt = e.updatedAt,
                author = e.authorDeviceId, isDeleted = e.isDeleted)
        }
    }

    override fun applyFile(dto: SyncFileDto, fileBytes: (String) -> ByteArray?): Pair<ApplyResult, ConflictInfo?> {
        requireFilesEnabled()
        return SyncEngine.applyFile(this, dto, fileBytes)
    }

    override fun insertFullFile(dto: SyncFileDto) {
        val w = db.writableDatabase
        w.beginTransaction()
        try {
            w.execSQL("INSERT INTO files(id, name, mime, size, sha256, stored_name, rev, updated_at, author_device, is_deleted) " +
                "VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                arrayOf(dto.id, dto.name, dto.mime, dto.size, dto.sha256,
                    dto.sha256.lowercase(), dto.rev, dto.updatedAt, dto.author,
                    if (dto.isDeleted) 1 else 0))
            w.setTransactionSuccessful()
        } finally {
            w.endTransaction()
        }
    }

    override fun updateFullFile(dto: SyncFileDto) {
        val w = db.writableDatabase
        w.beginTransaction()
        try {
            w.execSQL("UPDATE files SET name=?, mime=?, size=?, sha256=?, stored_name=?, rev=?, updated_at=?, author_device=?, is_deleted=? " +
                "WHERE id=?",
                arrayOf(dto.name, dto.mime, dto.size, dto.sha256,
                    dto.sha256.lowercase(), dto.rev, dto.updatedAt, dto.author,
                    if (dto.isDeleted) 1 else 0, dto.id))
            w.setTransactionSuccessful()
        } finally {
            w.endTransaction()
        }
    }

    override fun exportAll(): List<SyncNoteDto> {
        val ids = mutableListOf<String>()
        db.readableDatabase.rawQuery("SELECT id FROM notes", null).use { c ->
            while (c.moveToNext()) ids += c.getString(0)
        }
        return ids.mapNotNull { id ->
            val n = get(id) ?: return@mapNotNull null
            SyncNoteDto(id = n.id, rev = n.rev, baseRev = getSyncRev(id),
                title = n.title, body = n.body, updatedAt = n.updatedAt,
                author = n.authorDeviceId, isDeleted = n.isDeleted,
                checklist = checklist(id).map {
                    ChecklistItemDto(position = it.position, text = it.text,
                        isChecked = it.isChecked)
                },
                attachments = attachments(id).map {
                    AttachmentMetaDto(fileName = it.fileName, mimeType = it.mimeType,
                        sizeBytes = it.sizeBytes, sha256 = it.sha256)
                })
        }
    }

    override fun importFull(dto: SyncNoteDto, fileBytes: (String) -> ByteArray?) {
        val w = db.writableDatabase
        w.beginTransaction()
        try {
            w.execSQL("""INSERT OR REPLACE INTO notes(id, rev, title, body, updated_at, author_device,
                is_deleted, title_norm, body_norm) VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                arrayOf(dto.id, dto.rev, dto.title, dto.body, dto.updatedAt, dto.author,
                    if (dto.isDeleted) 1 else 0,
                    dto.title.lowercase(), dto.body.lowercase()))
            w.execSQL("DELETE FROM checklist_items WHERE note_id=?", arrayOf(dto.id))
            for (c in dto.checklist.sortedBy { it.position }) {
                w.execSQL("""INSERT INTO checklist_items(id, note_id, position, text, is_checked)
                    VALUES(?, ?, ?, ?, ?)""",
                    arrayOf(java.util.UUID.randomUUID().toString().replace("-", ""),
                        dto.id, c.position, c.text, if (c.isChecked) 1 else 0))
            }
            w.setTransactionSuccessful()
        } finally {
            w.endTransaction()
        }
        val want = dto.attachments.map { it.sha256 }.toSet()
        for (old in attachments(dto.id))
            if (old.sha256 !in want) deleteAttachmentFile(old)
        val have = attachments(dto.id).map { it.sha256 }.toSet()
        for (meta in dto.attachments) {
            if (meta.sha256 in have) continue
            val bytes = fileBytes(meta.sha256)
                ?: throw IllegalStateException("Нет байтов файла ${meta.fileName}.")
            addAttachmentBytes(dto.id, meta.fileName, meta.mimeType, bytes)
        }
    }

    fun addAttachmentBytes(noteId: String, fileName: String, mime: String, content: ByteArray): Attachment {
        if (content.size > 100L * 1024 * 1024)
            throw IllegalArgumentException("Файл $fileName превышает лимит 100 МБ.")
        val att = Attachment(noteId = noteId, fileName = fileName, mimeType = mime,
            sizeBytes = content.size.toLong())
        att.sha256 = sha256hex(content)
        // Content-addressed: имя на диске == sha256, дедуп — не пишем повторно.
        att.storedName = att.sha256
        val dest = File(filesRoot, att.storedName)
        if (!dest.exists()) {
            val tmp = File.createTempFile("att-", ".part", filesRoot)
            try {
                tmp.writeBytes(content)
                if (!tmp.renameTo(dest)) {
                    tmp.delete()
                    throw java.io.IOException("Не удалось сохранить вложение $fileName.")
                }
            } catch (e: Exception) {
                try { tmp.delete() } catch (_: Exception) { }
                throw e
            }
        }
        val v = android.content.ContentValues().apply {
            put("id", att.id); put("note_id", noteId); put("file_name", fileName)
            put("mime", mime); put("size", att.sizeBytes); put("sha256", att.sha256)
            put("stored_name", att.storedName)
        }
        db.writableDatabase.insertOrThrow("attachments", null, v)
        touchNote(noteId)
        return att
    }

    private fun deleteAttachmentFile(att: Attachment) {
        db.writableDatabase.delete("attachments", "id=?", arrayOf(att.id))
        // Content-addressed: байты общие, файл удаляем только без живых ссылок.
        try {
            db.readableDatabase.rawQuery(
                "SELECT COUNT(*) FROM attachments WHERE sha256=?",
                arrayOf(att.sha256)).use { c ->
                c.moveToFirst()
                if (c.getLong(0) > 0) return
            }
        } catch (_: Exception) {
            return // состояние неизвестно — файл не трогаем
        }
        try { File(filesRoot, att.sha256).delete() } catch (_: Exception) { }
    }

    private fun touchNote(noteId: String) {
        db.writableDatabase.execSQL(
            "UPDATE notes SET rev = rev + 1, updated_at=? WHERE id=?",
            arrayOf(System.currentTimeMillis().toString(), noteId))
    }

    private fun sha256hex(bytes: ByteArray): String {
        val md = java.security.MessageDigest.getInstance("SHA-256")
        return md.digest(bytes).joinToString("") { "%02x".format(it) }
    }

    companion object {
        private fun migrateFilesDir(filesDir: File) {
            val nested = File(filesDir, "files")
            if (!nested.isDirectory) return
            nested.listFiles()?.forEach { f ->
                val dest = File(filesDir, f.name)
                if (!dest.exists()) f.renameTo(dest) else f.delete()
            }
            try { nested.delete() } catch (_: Exception) { }
        }
    }
}
