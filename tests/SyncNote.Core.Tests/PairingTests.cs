using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SyncNote.Core;
using SyncNote.Core.Services;
using SyncNote.Windows.Services;

namespace SyncNote.Core.Tests;

[TestClass]
public sealed class PairingTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [TestMethod]
    public void Payload_RoundTrips()
    {
        string dir = TempDir();
        string db = Path.Combine(dir, "n.db");
        new WindowsDbInitializer().Initialize(db);
        using var s = PairingService.Start(db, "pc", "192.168.1.5", 0);
        var payload = JsonNode.Parse(s.QrPayload)!.AsObject();
        Assert.AreEqual("192.168.1.5", payload["ip"]?.GetValue<string>());
        Assert.AreEqual(s.Port, payload["port"]?.GetValue<int>());
        Assert.AreEqual(64, payload["token"]?.GetValue<string>()?.Length);
        Assert.AreEqual(s.Token, payload["token"]?.GetValue<string>());
    }

    [TestMethod]
    public void Tokens_AreUnique()
    {
        string dir = TempDir();
        string db = Path.Combine(dir, "n.db");
        new WindowsDbInitializer().Initialize(db);
        using var a = PairingService.Start(db, "pc", "h", 0);
        using var b = PairingService.Start(db, "pc", "h", 0);
        Assert.AreNotEqual(a.Token, b.Token);
    }

    [TestMethod]
    public void QrPng_IsPng()
    {
        string dir = TempDir();
        string db = Path.Combine(dir, "n.db");
        new WindowsDbInitializer().Initialize(db);
        using var s = PairingService.Start(db, "pc", "h", 0);
        byte[] png = s.QrPng;
        Assert.IsTrue(png.Length > 100);
        // Сигнатура PNG.
        CollectionAssert.AreEqual(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
    }

    [TestMethod]
    public async Task Accept_RightToken_Syncs()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string dir = TempDir();
        string db = Path.Combine(dir, "n.db");
        new WindowsDbInitializer().Initialize(db);
        using var session = PairingService.Start(db, "pc", "127.0.0.1", 0);
        var statuses = new List<string>();
        var serverRun = session.WaitForPeerAsync(
            s => statuses.Add(s), cts.Token);
        await using var t = await TcpFrameTransport.ConnectAsync(
            "127.0.0.1", session.Port, cts.Token);
        var notes = new SqliteNoteRepo(db, "pc");
        var io = new WindowsFileIo(Path.Combine(dir, "files"));
        var files = new SqliteFileRepo(db, "pc", io);
        var sync = new SqlSyncStateStore(db);
        var eng = new SyncEngine(notes, files, io, sync, "phone", Path.Combine(dir, "t"));
        var res = await eng.RunSessionAsync(
            t, isInitiator: true, cts.Token, token: session.Token);
        var serverRes = await serverRun;
        Assert.AreEqual(0, res.Conflicts);
        Assert.AreEqual(0, serverRes.Conflicts);
        Assert.IsTrue(statuses.Count >= 2);
    }

    [TestMethod]
    public async Task Accept_WrongToken_Rejected()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string dir = TempDir();
        string db = Path.Combine(dir, "n.db");
        new WindowsDbInitializer().Initialize(db);
        using var session = PairingService.Start(db, "pc", "127.0.0.1", 0);
        var serverRun = session.WaitForPeerAsync(_ => { }, cts.Token);
        await using var t = await TcpFrameTransport.ConnectAsync(
            "127.0.0.1", session.Port, cts.Token);
        var notes = new SqliteNoteRepo(db, "pc");
        var io = new WindowsFileIo(Path.Combine(dir, "files"));
        var files = new SqliteFileRepo(db, "pc", io);
        var sync = new SqlSyncStateStore(db);
        var eng = new SyncEngine(notes, files, io, sync, "phone", Path.Combine(dir, "t"));
        await Assert.ThrowsExceptionAsync<HelloRejectedException>(() =>
            eng.RunSessionAsync(t, isInitiator: true, cts.Token, token: "wrong"));
        await Assert.ThrowsExceptionAsync<HelloRejectedException>(() => serverRun);
    }
}
