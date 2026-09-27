using Microsoft.Data.Sqlite;

namespace SyncNote.Core;

// SQLite-хранилище (WAL + транзакции на запись). Схема v1:
// notes(id, rev, title, body, updated_at, author_device, is_deleted),
// keyvalue(key, value) — schema_version и device_id.
public sealed class SqliteNoteStore : ISyncStore, IDisposable
{
    private const int SchemaVersion = 5;
    private readonly SqliteConnection _db;
    private readonly string _deviceId;
    private readonly string _filesDir;
    private bool _disposed;

    public SqliteNoteStore(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _db = new SqliteConnection($"Data Source={filePath}");
        _db.Open();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }
        Migrate();
        _deviceId = GetOrCreateDeviceId();
        _filesDir = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".", "files");
        Directory.CreateDirectory(_filesDir);
    }

    public string FilesDirectory => _filesDir;

    public string DeviceId => _deviceId;

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncNote", "notes.db");

    private void Migrate()
    {
        using var tx = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS keyvalue(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS notes(
                id TEXT PRIMARY KEY,
                rev INTEGER NOT NULL,
                title TEXT NOT NULL,
                body TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                author_device TEXT NOT NULL,
                is_deleted INTEGER NOT NULL DEFAULT 0);
            """;
        cmd.ExecuteNonQuery();
        var versionText = GetValue(cmd, "schema_version");
        var version = versionText is not null && int.TryParse(versionText, out var v) ? v : 0;
        if (version == 0)
        {
            // Свежая установка: полная схема v5 сразу.
            cmd.CommandText = """
                ALTER TABLE notes ADD COLUMN title_norm TEXT NOT NULL DEFAULT '';
                ALTER TABLE notes ADD COLUMN body_norm TEXT NOT NULL DEFAULT '';
                CREATE TABLE IF NOT EXISTS checklist_items(
                    id TEXT PRIMARY KEY,
                    note_id TEXT NOT NULL REFERENCES notes(id),
                    position INTEGER NOT NULL,
                    text TEXT NOT NULL,
                    is_checked INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX IF NOT EXISTS idx_checklist_note
                    ON checklist_items(note_id, position);
                CREATE TABLE IF NOT EXISTS attachments(
                    id TEXT PRIMARY KEY,
                    note_id TEXT NOT NULL REFERENCES notes(id),
                    file_name TEXT NOT NULL,
                    mime TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    sha256 TEXT NOT NULL,
                    stored_name TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS idx_attachments_note
                    ON attachments(note_id);
                CREATE TABLE IF NOT EXISTS syncstate(
                    note_id TEXT PRIMARY KEY,
                    sync_rev INTEGER NOT NULL DEFAULT 0);
                """;
            cmd.ExecuteNonQuery();
            SetValue(cmd, "schema_version", "5");
        }
        else if (version == 1)
        {
            // v1 -> v2: нормализованные копии для регистронезависимого поиска
            // (SQLite LIKE складывает регистр только для ASCII).
            // ALTER + бэкфилл средствами .NET в той же транзакции — атомарно.
            cmd.CommandText = """
                ALTER TABLE notes ADD COLUMN title_norm TEXT NOT NULL DEFAULT '';
                ALTER TABLE notes ADD COLUMN body_norm TEXT NOT NULL DEFAULT '';
                """;
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT id, title, body FROM notes;";
            var rows = new List<(string Id, string Title, string Body)>();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                    rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            foreach (var (id, title, body) in rows)
            {
                cmd.CommandText = "UPDATE notes SET title_norm = $t, body_norm = $b WHERE id = $id;";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$t", Norm(title));
                cmd.Parameters.AddWithValue("$b", Norm(body));
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            SetValue(cmd, "schema_version", "2");
            version = 2;
        }
        if (version == 2)
        {
            // v2 -> v3: пункты чек-листа.
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS checklist_items(
                    id TEXT PRIMARY KEY,
                    note_id TEXT NOT NULL REFERENCES notes(id),
                    position INTEGER NOT NULL,
                    text TEXT NOT NULL,
                    is_checked INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX IF NOT EXISTS idx_checklist_note
                    ON checklist_items(note_id, position);
                """;
            cmd.ExecuteNonQuery();
            SetValue(cmd, "schema_version", "3");
            version = 3;
        }
        if (version == 3)
        {
            // v3 -> v4: вложения (метаданные; файлы — в каталоге files/).
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS attachments(
                    id TEXT PRIMARY KEY,
                    note_id TEXT NOT NULL REFERENCES notes(id),
                    file_name TEXT NOT NULL,
                    mime TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    sha256 TEXT NOT NULL,
                    stored_name TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS idx_attachments_note
                    ON attachments(note_id);
                """;
            cmd.ExecuteNonQuery();
            SetValue(cmd, "schema_version", "4");
            version = 4;
        }
        if (version == 4)
        {
            // v4 -> v5: состояние синхронизации (последняя общая ревизия).
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS syncstate(
                    note_id TEXT PRIMARY KEY,
                    sync_rev INTEGER NOT NULL DEFAULT 0);
                """;
            cmd.ExecuteNonQuery();
            SetValue(cmd, "schema_version", "5");
        }
        tx.Commit();
    }

    private static string Norm(string s) => s.ToLowerInvariant();

    private string GetOrCreateDeviceId()
    {
        using var cmd = _db.CreateCommand();
        var existing = GetValue(cmd, "device_id");
        if (existing is not null)
            return existing;
        var created = Guid.NewGuid().ToString("N");
        SetValue(cmd, "device_id", created);
        return created;
    }

    private static string? GetValue(SqliteCommand cmd, string key)
    {
        cmd.CommandText = "SELECT value FROM keyvalue WHERE key = $k;";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void SetValue(SqliteCommand cmd, string key, string value)
    {
        cmd.CommandText = "INSERT INTO keyvalue(key, value) VALUES($k, $v) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Note> List() => Query(null);

    public IReadOnlyList<Note> Search(string query) =>
        string.IsNullOrWhiteSpace(query) ? List() : Query(query);

    private List<Note> Query(string? filter)
    {
        using var cmd = _db.CreateCommand();
        if (filter is null)
        {
            cmd.CommandText = "SELECT id, rev, title, body, updated_at, author_device " +
                "FROM notes WHERE is_deleted = 0 ORDER BY updated_at DESC;";
        }
        else
        {
            cmd.CommandText = "SELECT id, rev, title, body, updated_at, author_device " +
                "FROM notes WHERE is_deleted = 0 " +
                "AND (title_norm LIKE $q ESCAPE '\\' OR body_norm LIKE $q ESCAPE '\\') " +
                "ORDER BY updated_at DESC;";
            cmd.Parameters.AddWithValue("$q", "%" + EscapeLike(Norm(filter)) + "%");
        }
        var result = new List<Note>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new Note
            {
                Id = Guid.Parse(reader.GetString(0)),
                Rev = reader.GetInt64(1),
                Title = reader.GetString(2),
                Body = reader.GetString(3),
                UpdatedAt = DateTime.Parse(reader.GetString(4),
                    null, System.Globalization.DateTimeStyles.RoundtripKind),
                AuthorDeviceId = reader.GetString(5),
            });
        }
        return result;
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public Note Add(string title, string body)
    {
        var note = new Note
        {
            Title = title,
            Body = body,
            UpdatedAt = DateTime.UtcNow,
            Rev = 1,
            AuthorDeviceId = _deviceId,
        };
        using var tx = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "INSERT INTO notes(id, rev, title, body, updated_at, author_device, is_deleted, title_norm, body_norm) " +
            "VALUES($id, 1, $t, $b, $u, $d, 0, $tn, $bn);";
        cmd.Parameters.AddWithValue("$id", note.Id.ToString("N"));
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$b", body);
        cmd.Parameters.AddWithValue("$u", note.UpdatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$d", _deviceId);
        cmd.Parameters.AddWithValue("$tn", Norm(title));
        cmd.Parameters.AddWithValue("$bn", Norm(body));
        cmd.ExecuteNonQuery();
        tx.Commit();
        return note;
    }

    public void Update(Note note)
    {
        using var tx = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "UPDATE notes SET rev = rev + 1, title = $t, body = $b, " +
            "updated_at = $u, author_device = $d, title_norm = $tn, body_norm = $bn " +
            "WHERE id = $id AND is_deleted = 0;";
        var now = DateTime.UtcNow;
        cmd.Parameters.AddWithValue("$t", note.Title);
        cmd.Parameters.AddWithValue("$b", note.Body);
        cmd.Parameters.AddWithValue("$u", now.ToString("o"));
        cmd.Parameters.AddWithValue("$d", _deviceId);
        cmd.Parameters.AddWithValue("$tn", Norm(note.Title));
        cmd.Parameters.AddWithValue("$bn", Norm(note.Body));
        cmd.Parameters.AddWithValue("$id", note.Id.ToString("N"));
        var rows = cmd.ExecuteNonQuery();
        tx.Commit();
        if (rows == 0)
            throw new KeyNotFoundException($"Note {note.Id} not found.");
        note.UpdatedAt = now;
        note.Rev += 1;
        note.AuthorDeviceId = _deviceId;
    }

    public bool Delete(Guid id)
    {
        using var tx = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "UPDATE notes SET is_deleted = 1, rev = rev + 1, " +
            "updated_at = $u WHERE id = $id AND is_deleted = 0;";
        cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id.ToString("N"));
        var rows = cmd.ExecuteNonQuery();
        tx.Commit();
        return rows > 0;
    }

    public IReadOnlyList<ChecklistItem> GetChecklist(Guid noteId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, note_id, position, text, is_checked " +
            "FROM checklist_items WHERE note_id = $n ORDER BY position;";
        cmd.Parameters.AddWithValue("$n", noteId.ToString("N"));
        var result = new List<ChecklistItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ChecklistItem
            {
                Id = Guid.Parse(reader.GetString(0)),
                NoteId = Guid.Parse(reader.GetString(1)),
                Position = reader.GetInt32(2),
                Text = reader.GetString(3),
                IsChecked = reader.GetInt64(4) != 0,
            });
        }
        return result;
    }

    public ChecklistItem AddChecklistItem(Guid noteId, string text)
    {
        using var tx = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "SELECT COALESCE(MAX(position), -1) + 1 FROM checklist_items WHERE note_id = $n;";
        cmd.Parameters.AddWithValue("$n", noteId.ToString("N"));
        var position = Convert.ToInt32(cmd.ExecuteScalar());
        var item = new ChecklistItem { NoteId = noteId, Position = position, Text = text };
        cmd.CommandText = "INSERT INTO checklist_items(id, note_id, position, text, is_checked) " +
            "VALUES($id, $n, $p, $t, 0);";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$id", item.Id.ToString("N"));
        cmd.Parameters.AddWithValue("$n", noteId.ToString("N"));
        cmd.Parameters.AddWithValue("$p", position);
        cmd.Parameters.AddWithValue("$t", text);
        cmd.ExecuteNonQuery();
        tx.Commit();
        TouchNote(cmd, noteId);
        return item;
    }

    public void UpdateChecklistItem(ChecklistItem item)
    {
        using var tx = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "UPDATE checklist_items SET position = $p, text = $t, is_checked = $c " +
            "WHERE id = $id;";
        cmd.Parameters.AddWithValue("$p", item.Position);
        cmd.Parameters.AddWithValue("$t", item.Text);
        cmd.Parameters.AddWithValue("$c", item.IsChecked ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", item.Id.ToString("N"));
        var rows = cmd.ExecuteNonQuery();
        tx.Commit();
        if (rows == 0)
            throw new KeyNotFoundException($"Checklist item {item.Id} not found.");
        TouchNote(cmd, item.NoteId);
    }

    public bool DeleteChecklistItem(Guid itemId)
    {
        Guid noteId = Guid.Empty;
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT note_id FROM checklist_items WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", itemId.ToString("N"));
            var raw = cmd.ExecuteScalar() as string;
            if (raw is null)
                return false;
            noteId = Guid.Parse(raw);
        }
        using var tx = _db.BeginTransaction();
        using var del = _db.CreateCommand();
        del.Transaction = (SqliteTransaction)tx;
        del.CommandText = "DELETE FROM checklist_items WHERE id = $id;";
        del.Parameters.AddWithValue("$id", itemId.ToString("N"));
        del.ExecuteNonQuery();
        tx.Commit();
        TouchNote(del, noteId);
        return true;
    }

    public IReadOnlyList<Attachment> GetAttachments(Guid noteId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, note_id, file_name, mime, size, sha256, stored_name " +
            "FROM attachments WHERE note_id = $n ORDER BY file_name;";
        cmd.Parameters.AddWithValue("$n", noteId.ToString("N"));
        var result = new List<Attachment>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new Attachment
            {
                Id = Guid.Parse(reader.GetString(0)),
                NoteId = Guid.Parse(reader.GetString(1)),
                FileName = reader.GetString(2),
                MimeType = reader.GetString(3),
                SizeBytes = reader.GetInt64(4),
                Sha256 = reader.GetString(5),
                StoredName = reader.GetString(6),
            });
        }
        return result;
    }

    public Attachment AddAttachment(Guid noteId, string sourcePath)
    {
        var att = new Attachment { NoteId = noteId };
        // Сначала файл (атомарно), затем метаданные в транзакции.
        // При падении между ними остаётся файл-сирота — см. тест.
        var (storedName, size, sha) = AttachmentIo.CopyIn(_filesDir, att.Id, sourcePath);
        att.FileName = Path.GetFileName(sourcePath);
        att.MimeType = AttachmentIo.MimeByExtension(att.FileName);
        att.SizeBytes = size;
        att.Sha256 = sha;
        att.StoredName = storedName;
        using var tx = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "INSERT INTO attachments(id, note_id, file_name, mime, size, sha256, stored_name) " +
            "VALUES($id, $n, $f, $m, $s, $h, $sn);";
        cmd.Parameters.AddWithValue("$id", att.Id.ToString("N"));
        cmd.Parameters.AddWithValue("$n", noteId.ToString("N"));
        cmd.Parameters.AddWithValue("$f", att.FileName);
        cmd.Parameters.AddWithValue("$m", att.MimeType);
        cmd.Parameters.AddWithValue("$s", size);
        cmd.Parameters.AddWithValue("$h", sha);
        cmd.Parameters.AddWithValue("$sn", storedName);
        cmd.ExecuteNonQuery();
        tx.Commit();
        TouchNote(cmd, noteId);
        return att;
    }

    public bool DeleteAttachment(Guid attachmentId)
    {
        string? storedName = null;
        Guid noteId = Guid.Empty;
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT note_id, stored_name FROM attachments WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", attachmentId.ToString("N"));
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return false;
            noteId = Guid.Parse(reader.GetString(0));
            storedName = reader.GetString(1);
        }
        using var tx = _db.BeginTransaction();
        using var del = _db.CreateCommand();
        del.Transaction = (SqliteTransaction)tx;
        del.CommandText = "DELETE FROM attachments WHERE id = $id;";
        del.Parameters.AddWithValue("$id", attachmentId.ToString("N"));
        del.ExecuteNonQuery();
        tx.Commit();
        try { File.Delete(Path.Combine(_filesDir, storedName)); } catch { }
        TouchNote(del, noteId);
        return true;
    }

    // Любое изменение чек-листа — новая ревизия заметки (нужно для синхронизации).
    private void TouchNote(SqliteCommand cmd, Guid noteId)
    {
        cmd.Transaction = null;
        cmd.CommandText = "UPDATE notes SET rev = rev + 1, updated_at = $u WHERE id = $id;";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", noteId.ToString("N"));
        cmd.ExecuteNonQuery();
    }

    public Note? TryGet(Guid id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, rev, title, body, updated_at, author_device, is_deleted " +
            "FROM notes WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("N"));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new Note
        {
            Id = Guid.Parse(reader.GetString(0)),
            Rev = reader.GetInt64(1),
            Title = reader.GetString(2),
            Body = reader.GetString(3),
            UpdatedAt = DateTime.Parse(reader.GetString(4),
                null, System.Globalization.DateTimeStyles.RoundtripKind),
            AuthorDeviceId = reader.GetString(5),
            IsDeleted = reader.GetInt64(6) != 0,
        };
    }

    public long GetSyncRev(Guid noteId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT sync_rev FROM syncstate WHERE note_id = $n;";
        cmd.Parameters.AddWithValue("$n", noteId.ToString("N"));
        var raw = cmd.ExecuteScalar();
        return raw is long rev ? rev : 0;
    }

    public void SetSyncRev(Guid noteId, long rev)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO syncstate(note_id, sync_rev) VALUES($n, $r) " +
            "ON CONFLICT(note_id) DO UPDATE SET sync_rev = excluded.sync_rev;";
        cmd.Parameters.AddWithValue("$n", noteId.ToString("N"));
        cmd.Parameters.AddWithValue("$r", rev);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SyncNoteDto> Export()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id FROM notes;";
        var ids = new List<Guid>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                ids.Add(Guid.Parse(reader.GetString(0)));
        var result = new List<SyncNoteDto>();
        foreach (var id in ids)
        {
            var note = TryGet(id);
            if (note is null)
                continue;
            result.Add(new SyncNoteDto
            {
                Id = note.Id,
                Rev = note.Rev,
                BaseRev = GetSyncRev(id),
                Title = note.Title,
                Body = note.Body,
                UpdatedAt = note.UpdatedAt,
                Author = note.AuthorDeviceId,
                IsDeleted = note.IsDeleted,
                Checklist = GetChecklist(id).Select(i => new ChecklistItemDto
                {
                    Position = i.Position,
                    Text = i.Text,
                    IsChecked = i.IsChecked,
                }).ToList(),
                Attachments = GetAttachments(id).Select(a => new AttachmentMetaDto
                {
                    FileName = a.FileName,
                    MimeType = a.MimeType,
                    SizeBytes = a.SizeBytes,
                    Sha256 = a.Sha256,
                }).ToList(),
            });
        }
        return result;
    }

    public Attachment ImportAttachment(Guid noteId, string fileName, string mime, byte[] content)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"syncnote-imp-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(tmp, content);
            var att = AddAttachment(noteId, tmp);
            att.FileName = fileName;
            att.MimeType = mime;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE attachments SET file_name = $f, mime = $m WHERE id = $id;";
            cmd.Parameters.AddWithValue("$f", fileName);
            cmd.Parameters.AddWithValue("$m", mime);
            cmd.Parameters.AddWithValue("$id", att.Id.ToString("N"));
            cmd.ExecuteNonQuery();
            return att;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    public void ImportFull(SyncNoteDto dto, Func<string, byte[]?> fileBytes)
    {
        using var tx = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "INSERT INTO notes(id, rev, title, body, updated_at, author_device, is_deleted, title_norm, body_norm) " +
            "VALUES($id, $r, $t, $b, $u, $a, $del, $tn, $bn) " +
            "ON CONFLICT(id) DO UPDATE SET rev = excluded.rev, title = excluded.title, " +
            "body = excluded.body, updated_at = excluded.updated_at, " +
            "author_device = excluded.author_device, is_deleted = excluded.is_deleted, " +
            "title_norm = excluded.title_norm, body_norm = excluded.body_norm;";
        cmd.Parameters.AddWithValue("$id", dto.Id.ToString("N"));
        cmd.Parameters.AddWithValue("$r", dto.Rev);
        cmd.Parameters.AddWithValue("$t", dto.Title);
        cmd.Parameters.AddWithValue("$b", dto.Body);
        cmd.Parameters.AddWithValue("$u", dto.UpdatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$a", dto.Author);
        cmd.Parameters.AddWithValue("$del", dto.IsDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$tn", Norm(dto.Title));
        cmd.Parameters.AddWithValue("$bn", Norm(dto.Body));
        cmd.ExecuteNonQuery();
        cmd.CommandText = "DELETE FROM checklist_items WHERE note_id = $n;";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$n", dto.Id.ToString("N"));
        cmd.ExecuteNonQuery();
        foreach (var item in dto.Checklist.OrderBy(c => c.Position))
        {
            cmd.CommandText = "INSERT INTO checklist_items(id, note_id, position, text, is_checked) " +
                "VALUES($id, $n, $p, $t, $c);";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$n", dto.Id.ToString("N"));
            cmd.Parameters.AddWithValue("$p", item.Position);
            cmd.Parameters.AddWithValue("$t", item.Text);
            cmd.Parameters.AddWithValue("$c", item.IsChecked ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();

        var want = dto.Attachments.Select(a => a.Sha256).ToHashSet();
        foreach (var old in GetAttachments(dto.Id))
            if (!want.Contains(old.Sha256))
                DeleteAttachment(old.Id);
        var have = GetAttachments(dto.Id).Select(a => a.Sha256).ToHashSet();
        foreach (var meta in dto.Attachments)
        {
            if (have.Contains(meta.Sha256))
                continue;
            var bytes = fileBytes(meta.Sha256)
                ?? throw new InvalidOperationException(
                    $"Нет байтов файла {meta.FileName} (sha256 {meta.Sha256}).");
            ImportAttachment(dto.Id, meta.FileName, meta.MimeType, bytes);
        }

        // Нейтрализуем bump ревизий от TouchNote: ревизия — из DTO.
        using var fix = _db.CreateCommand();
        fix.CommandText = "UPDATE notes SET rev = $r, author_device = $a, updated_at = $u WHERE id = $id;";
        fix.Parameters.AddWithValue("$r", dto.Rev);
        fix.Parameters.AddWithValue("$a", dto.Author);
        fix.Parameters.AddWithValue("$u", dto.UpdatedAt.ToString("o"));
        fix.Parameters.AddWithValue("$id", dto.Id.ToString("N"));
        fix.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _db.Dispose();
            _disposed = true;
        }
    }
}
