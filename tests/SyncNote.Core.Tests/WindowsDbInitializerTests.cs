using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SyncNote.Windows.Services;

namespace SyncNote.Core.Tests;

[TestClass]
public sealed class WindowsDbInitializerTests
{
    private static string TempDb()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "test.db");
    }

    private static bool HasTable(string dbPath, string table)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$t";
        cmd.Parameters.AddWithValue("$t", table);
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    [TestMethod]
    public void Initialize_CreatesTablesAndVersion()
    {
        string db = TempDb();
        new WindowsDbInitializer().Initialize(db);
        Assert.IsTrue(HasTable(db, "notes"));
        Assert.IsTrue(HasTable(db, "files"));
        Assert.IsTrue(HasTable(db, "attachments"));
        Assert.IsTrue(HasTable(db, "sync_state"));
        Assert.IsTrue(HasTable(db, "seen_conflicts"));
        Assert.IsTrue(HasTable(db, "keyvalue"));
        Assert.AreEqual(1, new WindowsDbInitializer().GetCurrentVersion(db));
    }

    [TestMethod]
    public void Initialize_Twice_IsIdempotentAndKeepsData()
    {
        string db = TempDb();
        var init = new WindowsDbInitializer();
        init.Initialize(db);
        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO notes(id, title, body, updated_at, author_device)"
                + " VALUES('n1', 't', 'b', '2026-09-29T10:00:00Z', 'pc')";
            cmd.ExecuteNonQuery();
        }
        init.Initialize(db); // не должно упасть и не должно чистить
        Assert.AreEqual(1, init.GetCurrentVersion(db));
        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM notes WHERE id='n1'";
            Assert.AreEqual(1L, Convert.ToInt64(cmd.ExecuteScalar()));
        }
    }
}
