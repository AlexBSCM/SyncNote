using SyncNote.Core;

namespace SyncNote.Core.Tests;

[TestClass]
public sealed class PairingTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"syncnote-pair-{Guid.NewGuid():N}.db");

    [TestMethod]
    public void Issue_RedeemOnce_ReuseFails()
    {
        using var svc = PairingService.Open(TempDb());
        var (token, exp) = svc.IssueToken();
        Assert.AreEqual(22, token.Length);
        Assert.IsTrue(exp > DateTime.UtcNow);

        Assert.IsTrue(svc.RedeemToken(token));
        Assert.IsFalse(svc.RedeemToken(token)); // одноразовый
    }

    [TestMethod]
    public void UnknownToken_Fails()
    {
        using var svc = PairingService.Open(TempDb());
        Assert.IsFalse(svc.RedeemToken("AAAAAAAAAAAAAAAAAAAAAA"));
    }

    [TestMethod]
    public void ExpiredToken_Fails()
    {
        var path = TempDb();
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE pairing_tokens(
                    token TEXT PRIMARY KEY, created_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL, used INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE trusted_devices(
                    device_id TEXT PRIMARY KEY, name TEXT NOT NULL,
                    trusted_at TEXT NOT NULL);
                INSERT INTO pairing_tokens(token, created_at, expires_at, used)
                VALUES('old-token', '2020-01-01T00:00:00Z', '2020-01-01T00:05:00Z', 0);
                """;
            cmd.ExecuteNonQuery();
        }
        using var svc = PairingService.Open(path);
        Assert.IsFalse(svc.RedeemToken("old-token"));
    }

    [TestMethod]
    public void Trust_Untrust_Persists()
    {
        var path = TempDb();
        using (var svc = PairingService.Open(path))
        {
            Assert.IsFalse(svc.IsTrusted("phone1"));
            svc.TrustDevice("phone1", "Pixel");
            Assert.IsTrue(svc.IsTrusted("phone1"));
        }
        using (var reopened = PairingService.Open(path))
        {
            Assert.IsTrue(reopened.IsTrusted("phone1"));
            Assert.IsTrue(reopened.Untrust("phone1"));
            Assert.IsFalse(reopened.IsTrusted("phone1"));
            Assert.IsFalse(reopened.Untrust("phone1"));
        }
    }
}
