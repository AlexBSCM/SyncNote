using Microsoft.Data.Sqlite;

namespace SyncNote.Windows.Services;

// Сырой SQL для таблицы keyvalue (схема docs/schema.sql).
// Хранит настройки и состояние UI: доверенные устройства, время синка.
public sealed class KeyValueStore
{
    private readonly string _dbPath;

    public KeyValueStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    public string? Get(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM keyvalue WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        var res = cmd.ExecuteScalar();
        return res?.ToString();
    }

    public void Set(string key, string value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO keyvalue(key, value) VALUES($k, $v)"
            + " ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public void Remove(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM keyvalue WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
        conn.Open();
        return conn;
    }
}
