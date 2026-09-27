using Microsoft.Data.Sqlite;

namespace SyncNote.Core;

// SQLite-хранилище (WAL + транзакции на запись). Схема v1:
// notes(id, rev, title, body, updated_at, author_device, is_deleted),
// keyvalue(key, value) — schema_version и device_id.
public sealed class SqliteNoteStore : INoteStore, IDisposable
{
    private const int SchemaVersion = 2;
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
            // Свежая установка: полная схема v2 сразу.
            cmd.CommandText = """
                ALTER TABLE notes ADD COLUMN title_norm TEXT NOT NULL DEFAULT '';
                ALTER TABLE notes ADD COLUMN body_norm TEXT NOT NULL DEFAULT '';
                """;
            cmd.ExecuteNonQuery();
            SetValue(cmd, "schema_version", "2");
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

    public void Dispose()
    {
        if (!_disposed)
        {
            _db.Dispose();
            _disposed = true;
        }
    }
}
