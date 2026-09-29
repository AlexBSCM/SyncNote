using System.IO;
using Microsoft.Data.Sqlite;
using SyncNote.Core.Interfaces;
using SyncNote.Core.Utils;

namespace SyncNote.Windows.Services;

// Десктопная реализация IDbInitializer. Схема читается из сборки Core
// (typeof(IDbInitializer).Assembly), а НЕ GetExecutingAssembly():
// ресурс schema_v1.sql встроен в SyncNote.Core.dll, и вызов через
// исполняющую сборку его бы не нашёл. Ошибка — исключение (fail-fast).
public sealed class WindowsDbInitializer : IDbInitializer
{
    public void Initialize(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        using var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
        conn.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            if (ReadVersion(conn, tx) < 1)
            {
                foreach (var stmt in SqlSplitter.Split(LoadSchemaSql()))
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = stmt;
                    cmd.ExecuteNonQuery();
                }
                using var ver = conn.CreateCommand();
                ver.Transaction = tx;
                ver.CommandText =
                    "INSERT OR REPLACE INTO keyvalue (key, value) VALUES ('schema_version', '1')";
                ver.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    public int GetCurrentVersion(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
        conn.Open();
        return ReadVersion(conn, null);
    }

    private static int ReadVersion(SqliteConnection conn, SqliteTransaction? tx)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT value FROM keyvalue WHERE key='schema_version'";
            var res = cmd.ExecuteScalar();
            if (res is not null && int.TryParse(res.ToString(), out int v))
                return v;
            return 0;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            return 0; // нет таблицы keyvalue — чистая база
        }
    }

    private static string LoadSchemaSql()
    {
        var asm = typeof(IDbInitializer).Assembly;
        using var stream = asm.GetManifestResourceStream("schema_v1.sql")
            ?? throw new InvalidOperationException(
                "Embedded resource schema_v1.sql not found in SyncNote.Core.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
