using Microsoft.Data.Sqlite;
using SyncNote.Core.Models;

namespace SyncNote.Windows.Services;

// Сырой SQL для таблицы attachments (схема docs/schema.sql).
// Байты лежат в content-addressed хранилище (sha256), здесь только связь.
public sealed class SqliteAttachmentRepo
{
    private readonly string _dbPath;
    private readonly string _deviceId;

    public SqliteAttachmentRepo(string dbPath, string deviceId)
    {
        _dbPath = dbPath;
        _deviceId = deviceId;
    }

    public Task<string> InsertAsync(string noteId, string name, string mime,
        long size, string sha256)
    {
        string id = Guid.NewGuid().ToString("N");
        string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO attachments(id, note_id, name, mime, size,"
            + " sha256, stored_name, rev, updated_at, author_device, is_deleted)"
            + " VALUES($id, $n, $name, $mime, $size, $sha, $sha, 1, $u, $d, 0)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$n", noteId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$mime", mime);
        cmd.Parameters.AddWithValue("$size", size);
        cmd.Parameters.AddWithValue("$sha", sha256);
        cmd.Parameters.AddWithValue("$u", now);
        cmd.Parameters.AddWithValue("$d", _deviceId);
        cmd.ExecuteNonQuery();
        return Task.FromResult(id);
    }

    public Task<List<AttachmentEntry>> ListByNoteAsync(string noteId)
    {
        var out_ = new List<AttachmentEntry>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, note_id, name, mime, size, sha256,"
            + " stored_name, rev, updated_at, author_device, is_deleted"
            + " FROM attachments WHERE note_id = $n AND is_deleted = 0"
            + " ORDER BY updated_at DESC";
        cmd.Parameters.AddWithValue("$n", noteId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            out_.Add(new AttachmentEntry
            {
                Id = r.GetString(0),
                NoteId = r.GetString(1),
                Name = r.GetString(2),
                Mime = r.GetString(3),
                SizeBytes = r.GetInt64(4),
                Sha256 = r.GetString(5),
                StoredName = r.GetString(6),
                Rev = r.GetInt64(7),
                UpdatedAt = r.GetString(8),
                AuthorDeviceId = r.GetString(9),
                IsDeleted = r.GetInt32(10) != 0,
            });
        }
        return Task.FromResult(out_);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
        conn.Open();
        return conn;
    }
}
