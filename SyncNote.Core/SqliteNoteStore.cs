using Microsoft.Data.Sqlite;

namespace SyncNote.Core;

// SQLite-хранилище (WAL + транзакции на запись). Схема v1:
// notes(id, rev, title, body, updated_at, author_device, is_deleted),
// keyvalue(key, value) — schema_version и device_id.
public sealed class SqliteNoteStore : INoteStore, IDisposable
{
    private const int SchemaVersion = 3;
    private readonly SqliteConnection _db;
    private readonly string _deviceId;
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
    }

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
            // Свежая установка: полная схема v3 сразу.
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
                """;
            cmd.ExecuteNonQuery();
            SetValue(cmd, "schema_version", "3");
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

    public void Dispose()
    {
        if (!_disposed)
        {
            _db.Dispose();
            _disposed = true;
        }
    }
}
