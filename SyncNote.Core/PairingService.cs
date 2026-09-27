using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace SyncNote.Core;

// Краткоживущие одноразовые токены сопряжения + доверенные устройства.
// Токен: 128 бит, Base64Url (22 символа), TTL 5 минут, одноразовый.
// Хранение: таблицы pairing_tokens / trusted_devices (схема v5).
public sealed class PairingService : IDisposable
{
    public static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(5);
    private readonly SqliteConnection _db;
    private bool _disposed;

    public PairingService(SqliteConnection db)
    {
        _db = db;
        EnsureSchema();
    }

    public static PairingService Open(string filePath)
    {
        var db = new SqliteConnection($"Data Source={filePath}");
        db.Open();
        return new PairingService(db);
    }

    private void EnsureSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS pairing_tokens(
                token TEXT PRIMARY KEY, created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL, used INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS trusted_devices(
                device_id TEXT PRIMARY KEY, name TEXT NOT NULL,
                trusted_at TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public (string Token, DateTime ExpiresAt) IssueToken()
    {
        PurgeExpired();
        var bytes = RandomNumberGenerator.GetBytes(16);
        var token = Convert.ToBase64String(bytes)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var now = DateTime.UtcNow;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO pairing_tokens(token, created_at, expires_at, used) " +
            "VALUES($t, $c, $e, 0);";
        cmd.Parameters.AddWithValue("$t", token);
        cmd.Parameters.AddWithValue("$c", now.ToString("o"));
        cmd.Parameters.AddWithValue("$e", (now + TokenTtl).ToString("o"));
        cmd.ExecuteNonQuery();
        return (token, now + TokenTtl);
    }

    // Проверяет и сразу помечает использованным (атомарно).
    public bool RedeemToken(string token)
    {
        using var tx = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "SELECT expires_at, used FROM pairing_tokens WHERE token = $t;";
        cmd.Parameters.AddWithValue("$t", token);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return false;
        var exp = DateTime.Parse(reader.GetString(0), null,
            System.Globalization.DateTimeStyles.RoundtripKind);
        var used = reader.GetInt64(1) != 0;
        reader.Close();
        if (used || exp < DateTime.UtcNow)
            return false;
        cmd.CommandText = "UPDATE pairing_tokens SET used = 1 WHERE token = $t;";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$t", token);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    public void TrustDevice(string deviceId, string name)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO trusted_devices(device_id, name, trusted_at) " +
            "VALUES($d, $n, $t) ON CONFLICT(device_id) DO UPDATE SET " +
            "name = excluded.name, trusted_at = excluded.trusted_at;";
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public bool IsTrusted(string deviceId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM trusted_devices WHERE device_id = $d;";
        cmd.Parameters.AddWithValue("$d", deviceId);
        return cmd.ExecuteScalar() is not null;
    }

    public bool Untrust(string deviceId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM trusted_devices WHERE device_id = $d;";
        cmd.Parameters.AddWithValue("$d", deviceId);
        return cmd.ExecuteNonQuery() > 0;
    }

    private void PurgeExpired()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM pairing_tokens WHERE expires_at < $now;";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
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
