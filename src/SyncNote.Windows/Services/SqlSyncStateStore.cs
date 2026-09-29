using Microsoft.Data.Sqlite;
using SyncNote.Core.Interfaces;

namespace SyncNote.Windows.Services;

// SQL-реализация ISyncStateStore поверх той же БД.
public sealed class SqlSyncStateStore(string dbPath) : ISyncStateStore
{
    public long GetSyncRev(string entityType, string entityId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_synced_rev FROM sync_state"
            + " WHERE entity_type = $t AND entity_id = $id";
        cmd.Parameters.AddWithValue("$t", entityType);
        cmd.Parameters.AddWithValue("$id", entityId);
        var res = cmd.ExecuteScalar();
        return res is long l ? l : 0;
    }

    public void SetSyncRev(string entityType, string entityId, long rev)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO sync_state(entity_type, entity_id, last_synced_rev)"
            + " VALUES($t, $id, $r)"
            + " ON CONFLICT(entity_type, entity_id)"
            + " DO UPDATE SET last_synced_rev = excluded.last_synced_rev";
        cmd.Parameters.AddWithValue("$t", entityType);
        cmd.Parameters.AddWithValue("$id", entityId);
        cmd.Parameters.AddWithValue("$r", rev);
        cmd.ExecuteNonQuery();
    }

    public bool NoteSeenConflict(string originalId, long rev, string contentHash)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM seen_conflicts"
            + " WHERE original_entity_id = $id AND original_rev = $r AND content_hash = $h";
        cmd.Parameters.AddWithValue("$id", originalId);
        cmd.Parameters.AddWithValue("$r", rev);
        cmd.Parameters.AddWithValue("$h", contentHash);
        if (cmd.ExecuteScalar() is not null)
            return true;
        using var ins = conn.CreateCommand();
        ins.CommandText = "INSERT OR IGNORE INTO seen_conflicts(conflict_id,"
            + " original_entity_id, original_rev, content_hash)"
            + " VALUES($c, $id, $r, $h)";
        ins.Parameters.AddWithValue("$c", Guid.NewGuid().ToString("N"));
        ins.Parameters.AddWithValue("$id", originalId);
        ins.Parameters.AddWithValue("$r", rev);
        ins.Parameters.AddWithValue("$h", contentHash);
        ins.ExecuteNonQuery();
        return false;
    }

    public bool HasLiveFileRef(string sha256)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM files WHERE sha256 = $h AND is_deleted = 0 LIMIT 1";
        cmd.Parameters.AddWithValue("$h", sha256.ToLowerInvariant());
        return cmd.ExecuteScalar() is not null;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        return conn;
    }
}
