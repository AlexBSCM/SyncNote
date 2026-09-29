using Microsoft.Data.Sqlite;
using SyncNote.Core.Interfaces;
using SyncNote.Core.Models;

namespace SyncNote.Windows.Services;

// Сырой SQL поверх Microsoft.Data.Sqlite. Без ORM, без UI-логики:
// возвращает модели как есть, сортировка/фильтрация — дело вызывающего.
public sealed class SqliteNoteRepo : INoteRepo
{
    private readonly string _dbPath;
    private readonly string _deviceId;

    public SqliteNoteRepo(string dbPath, string deviceId)
    {
        _dbPath = dbPath;
        _deviceId = deviceId;
    }

    public Task<NoteEntry?> GetByIdAsync(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, body, rev, updated_at, author_device, is_deleted"
            + " FROM notes WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return Task.FromResult<NoteEntry?>(null);
        return Task.FromResult<NoteEntry?>(Read(r));
    }

    public Task<List<NoteEntry>> GetAllAsync(bool includeDeleted = false)
    {
        var out_ = new List<NoteEntry>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, body, rev, updated_at, author_device, is_deleted"
            + " FROM notes " + (includeDeleted ? "" : "WHERE is_deleted = 0 ")
            + "ORDER BY updated_at DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            out_.Add(Read(r));
        return Task.FromResult(out_);
    }

    public Task<string> CreateAsync(NoteEntry note)
    {
        // Синк передаёт готовый id; UI — пустой (тогда генерируем).
        string id = string.IsNullOrEmpty(note.Id) ? Guid.NewGuid().ToString("N") : note.Id;
        string now = UtcNow();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO notes(id, title, body, rev, updated_at,"
            + " author_device, is_deleted) VALUES($id, $t, $b, 1, $u, $d, 0)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$t", note.Title);
        cmd.Parameters.AddWithValue("$b", note.Body);
        cmd.Parameters.AddWithValue("$u", now);
        cmd.Parameters.AddWithValue("$d", _deviceId);
        cmd.ExecuteNonQuery();
        return Task.FromResult(id);
    }

    public Task UpdateAsync(NoteEntry note)
    {
        using var conn = Open();
        NoteEntry? cur;
        using (var get = conn.CreateCommand())
        {
            get.CommandText = "SELECT id, title, body, rev, updated_at, author_device, is_deleted"
                + " FROM notes WHERE id = $id";
            get.Parameters.AddWithValue("$id", note.Id);
            using var r = get.ExecuteReader();
            if (!r.Read())
                throw new KeyNotFoundException($"Note {note.Id} not found.");
            cur = Read(r);
        }
        if (cur.Title == note.Title && cur.Body == note.Body)
            return Task.CompletedTask; // no-op без изменений
        string now = UtcNow();
        using var cmd = conn.CreateCommand();
        // Optimistic locking: пишем только если ревизия не ушла вперёд.
        cmd.CommandText = "UPDATE notes SET title = $t, body = $b, rev = $r + 1,"
            + " updated_at = $u, author_device = $d"
            + " WHERE id = $id AND rev = $r AND is_deleted = 0";
        cmd.Parameters.AddWithValue("$t", note.Title);
        cmd.Parameters.AddWithValue("$b", note.Body);
        cmd.Parameters.AddWithValue("$r", note.Rev);
        cmd.Parameters.AddWithValue("$u", now);
        cmd.Parameters.AddWithValue("$d", _deviceId);
        cmd.Parameters.AddWithValue("$id", note.Id);
        if (cmd.ExecuteNonQuery() == 0)
            throw new InvalidOperationException(
                $"Note {note.Id} changed concurrently (expected rev {note.Rev}).");
        return Task.CompletedTask;
    }

    public Task SoftDeleteAsync(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE notes SET is_deleted = 1, rev = rev + 1,"
            + " updated_at = $u WHERE id = $id AND is_deleted = 0";
        cmd.Parameters.AddWithValue("$u", UtcNow());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task InsertFullAsync(NoteEntry e)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO notes(id, title, body, rev, updated_at,"
            + " author_device, is_deleted) VALUES($id, $t, $b, $r, $u, $d, $del)";
        Bind(cmd, e);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task UpdateFullAsync(NoteEntry e)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE notes SET title = $t, body = $b, rev = $r,"
            + " updated_at = $u, author_device = $d, is_deleted = $del WHERE id = $id";
        Bind(cmd, e);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    private static void Bind(SqliteCommand cmd, NoteEntry e)
    {
        cmd.Parameters.AddWithValue("$id", e.Id);
        cmd.Parameters.AddWithValue("$t", e.Title);
        cmd.Parameters.AddWithValue("$b", e.Body);
        cmd.Parameters.AddWithValue("$r", e.Rev);
        cmd.Parameters.AddWithValue("$u", e.UpdatedAt);
        cmd.Parameters.AddWithValue("$d", e.AuthorDeviceId);
        cmd.Parameters.AddWithValue("$del", e.IsDeleted ? 1 : 0);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private static NoteEntry Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Title = r.GetString(1),
        Body = r.GetString(2),
        Rev = r.GetInt64(3),
        UpdatedAt = r.GetString(4),
        AuthorDeviceId = r.GetString(5),
        IsDeleted = r.GetInt64(6) != 0,
    };

    internal static string UtcNow() =>
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'");
}
