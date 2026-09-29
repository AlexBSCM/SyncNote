using Microsoft.Data.Sqlite;
using SyncNote.Core.Interfaces;
using SyncNote.Core.Models;

namespace SyncNote.Windows.Services;

// Строки таблицы files. Байты — через IFileIo (content-addressed, дедуп).
public sealed class SqliteFileRepo : IFileRepo
{
    private readonly string _dbPath;
    private readonly string _deviceId;
    private readonly IFileIo _io;

    public SqliteFileRepo(string dbPath, string deviceId, IFileIo io)
    {
        _dbPath = dbPath;
        _deviceId = deviceId;
        _io = io;
    }

    public Task<FileEntry?> GetByIdAsync(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, mime, size, sha256, stored_name,"
            + " rev, updated_at, author_device, is_deleted"
            + " FROM files WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return Task.FromResult<FileEntry?>(null);
        return Task.FromResult<FileEntry?>(Read(r));
    }

    public Task<List<FileEntry>> GetAllAsync(bool includeDeleted = false)
    {
        var out_ = new List<FileEntry>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, mime, size, sha256, stored_name,"
            + " rev, updated_at, author_device, is_deleted"
            + " FROM files " + (includeDeleted ? "" : "WHERE is_deleted = 0 ")
            + "ORDER BY updated_at DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            out_.Add(Read(r));
        return Task.FromResult(out_);
    }

    public async Task<string> AddFileAsync(FileEntry metadata, string localTempPathOrUri)
    {
        // Сначала байты (дедуп внутри FileIo), затем строка.
        // Упавший INSERT после удачного импорта чистит сироту,
        // если на хеш больше нет живых ссылок.
        var (sha, size) = await _io.ImportAsync(localTempPathOrUri);
        string id = Guid.NewGuid().ToString("N");
        string now = SqliteNoteRepo.UtcNow();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO files(id, name, mime, size, sha256,"
                + " stored_name, rev, updated_at, author_device, is_deleted)"
                + " VALUES($id, $n, $m, $s, $h, $sn, 1, $u, $d, 0)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$n", metadata.Name);
            cmd.Parameters.AddWithValue("$m", metadata.Mime);
            cmd.Parameters.AddWithValue("$s", size);
            cmd.Parameters.AddWithValue("$h", sha);
            cmd.Parameters.AddWithValue("$sn", sha);
            cmd.Parameters.AddWithValue("$u", now);
            cmd.Parameters.AddWithValue("$d", _deviceId);
            cmd.ExecuteNonQuery();
            return id;
        }
        catch
        {
            if (!HasLiveSha(sha))
            {
                try { _io.DeleteFromStorage(sha); } catch { }
            }
            throw;
        }
    }

    public Task SoftDeleteAsync(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE files SET is_deleted = 1, rev = rev + 1,"
            + " updated_at = $u WHERE id = $id AND is_deleted = 0";
        cmd.Parameters.AddWithValue("$u", SqliteNoteRepo.UtcNow());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    private bool HasLiveSha(string sha)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM files WHERE sha256 = $h AND is_deleted = 0 LIMIT 1";
        cmd.Parameters.AddWithValue("$h", sha.ToLowerInvariant());
        return cmd.ExecuteScalar() is not null;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private static FileEntry Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Name = r.GetString(1),
        Mime = r.GetString(2),
        SizeBytes = r.GetInt64(3),
        Sha256 = r.GetString(4),
        StoredName = r.GetString(5),
        Rev = r.GetInt64(6),
        UpdatedAt = r.GetString(7),
        AuthorDeviceId = r.GetString(8),
        IsDeleted = r.GetInt64(9) != 0,
    };
}
