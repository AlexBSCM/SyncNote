using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SyncNote.Core;
using SyncNote.Core.Models;
using SyncNote.Core.Services;
using SyncNote.Windows.Services;

namespace SyncNote.Core.Tests;

// Сквозные сессии поверх реального TCP-loopback (127.0.0.1, эфемерный порт):
// два движка с настоящими SQLite-хранилищами гоняют протокол целиком.
[TestClass]
public sealed class SyncEngineTests
{
    private sealed class Node : IDisposable
    {
        public readonly string Dir;
        public readonly string Db;
        public readonly SqliteNoteRepo Notes;
        public readonly SqliteFileRepo Files;
        public readonly SqlSyncStateStore Sync;
        public readonly SyncEngine Engine;

        public Node(string device)
        {
            Dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Db = Path.Combine(Dir, "n.db");
            new WindowsDbInitializer().Initialize(Db);
            Notes = new SqliteNoteRepo(Db, device);
            var io = new WindowsFileIo(Path.Combine(Dir, "files"));
            Files = new SqliteFileRepo(Db, device, io);
            Sync = new SqlSyncStateStore(Db);
            Engine = new SyncEngine(Notes, Files, io, Sync, device,
                Path.Combine(Dir, "tmp"));
        }

        public async Task<SyncResult> SyncWith(
            Node peer, string? clientToken = null, string? serverExpected = null)
        {
            // Страховка от дедлока протокола: висим не дольше 60 с.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                var clientTask = TcpFrameTransport.ConnectAsync("127.0.0.1", port, cts.Token);
                await using var serverT = await TcpFrameTransport.AcceptAsync(listener, cts.Token);
                await using var clientT = await clientTask;
                var serverRun = peer.Engine.RunSessionAsync(
                    serverT, isInitiator: false, cts.Token, expectedToken: serverExpected);
                var clientRun = Engine.RunSessionAsync(
                    clientT, isInitiator: true, cts.Token, token: clientToken);
                await Task.WhenAll(serverRun, clientRun);
                return await clientRun;
            }
            finally
            {
                listener.Stop();
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task A_CreateNote_ArrivesAtPeer()
    {
        using var a = new Node("a");
        using var b = new Node("b");
        string id = await a.Notes.CreateAsync(new NoteEntry { Title = "hi", Body = "body" });
        var r = await a.SyncWith(b);
        Assert.AreEqual(1, r.Pushed);
        var got = await b.Notes.GetByIdAsync(id);
        Assert.IsNotNull(got);
        Assert.AreEqual("hi", got.Title);
        Assert.AreEqual(1, got.Rev);
    }

    [TestMethod]
    public async Task B_EditNote_FastForwards()
    {
        using var a = new Node("a");
        using var b = new Node("b");
        string id = await a.Notes.CreateAsync(new NoteEntry { Title = "t", Body = "b" });
        await a.SyncWith(b);
        var cur = (await a.Notes.GetByIdAsync(id))!;
        await a.Notes.UpdateAsync(new NoteEntry
            { Id = id, Rev = cur.Rev, Title = "t2", Body = "b" });
        await a.SyncWith(b);
        Assert.AreEqual("t2", (await b.Notes.GetByIdAsync(id))!.Title);
        Assert.AreEqual(2, (await b.Notes.GetByIdAsync(id))!.Rev);
    }

    [TestMethod]
    public async Task C_DivergentEdits_KeepBothCopies_Once()
    {
        using var a = new Node("a");
        using var b = new Node("b");
        string id = await a.Notes.CreateAsync(new NoteEntry { Title = "t", Body = "b" });
        await a.SyncWith(b);
        var ca = (await a.Notes.GetByIdAsync(id))!;
        var cb = (await b.Notes.GetByIdAsync(id))!;
        await a.Notes.UpdateAsync(new NoteEntry
            { Id = id, Rev = ca.Rev, Title = "A", Body = "b" });
        await b.Notes.UpdateAsync(new NoteEntry
            { Id = id, Rev = cb.Rev, Title = "B", Body = "b" });
        var r = await a.SyncWith(b);
        Assert.AreEqual(1, r.Conflicts);
        Assert.AreEqual(2, (await a.Notes.GetAllAsync(includeDeleted: true)).Count);
        Assert.AreEqual(2, (await b.Notes.GetAllAsync(includeDeleted: true)).Count);
        Assert.IsTrue((await a.Notes.GetAllAsync()).Any(n => n.Title.EndsWith(" (конфликт)")));
        Assert.IsTrue((await b.Notes.GetAllAsync()).Any(n => n.Title.EndsWith(" (конфликт)")));
        // Повторная сессия копий не плодит (seen-dedup).
        await a.SyncWith(b);
        Assert.AreEqual(2, (await a.Notes.GetAllAsync(includeDeleted: true)).Count);
        Assert.AreEqual(2, (await b.Notes.GetAllAsync(includeDeleted: true)).Count);
    }

    [TestMethod]
    public async Task D_LargeFile_StreamsByteIdentical()
    {
        using var a = new Node("a");
        using var b = new Node("b");
        byte[] payload = new byte[12 * 1024 * 1024];
        new Random(7).NextBytes(payload);
        string src = Path.Combine(a.Dir, "big.bin");
        await File.WriteAllBytesAsync(src, payload);
        string fid = await a.Files.AddFileAsync(
            new FileEntry { Name = "big.bin", Mime = "application/octet-stream" }, src);
        var r = await a.SyncWith(b);
        Assert.AreEqual(1, r.Pushed);
        var got = await b.Files.GetByIdAsync(fid);
        Assert.IsNotNull(got);
        Assert.AreEqual(payload.Length, got.SizeBytes);
        string want = SyncNote.Core.Utils.HashUtils.ComputeSha256(payload);
        Assert.AreEqual(want, got.Sha256);
    }

    [TestMethod]
    public async Task Token_WrongToken_RejectedCleanly()
    {
        using var a = new Node("a");
        using var b = new Node("b");
        await Assert.ThrowsExceptionAsync<HelloRejectedException>(() =>
            a.SyncWith(b, clientToken: "bad", serverExpected: "good"));
    }

    [TestMethod]
    public async Task Token_RightToken_FullSync()
    {
        using var a = new Node("a");
        using var b = new Node("b");
        string id = await a.Notes.CreateAsync(new NoteEntry { Title = "t", Body = "b" });
        var r = await a.SyncWith(b, clientToken: "s3cret", serverExpected: "s3cret");
        Assert.AreEqual(1, r.Pushed);
        Assert.IsNotNull(await b.Notes.GetByIdAsync(id));
    }

    [TestMethod]
    public async Task E_TombstoneWithoutBytes_AppliesCleanly()
    {
        using var a = new Node("a");
        using var b = new Node("b"); // свежий пир: файла нет вообще
        string src = Path.Combine(a.Dir, "x.bin");
        await File.WriteAllBytesAsync(src, new byte[] { 1, 2, 3 });
        string fid = await a.Files.AddFileAsync(
            new FileEntry { Name = "x.bin", Mime = "application/octet-stream" }, src);
        await a.Files.SoftDeleteAsync(fid);
        // Байтов больше нет нигде — как после purge у позднего пира.
        foreach (var f in Directory.GetFiles(Path.Combine(a.Dir, "files")))
            File.Delete(f);
        var r = await a.SyncWith(b); // сессия обязана завершиться без исключений
        Assert.AreEqual(0, r.Conflicts);
        var got = await b.Files.GetByIdAsync(fid);
        Assert.IsNotNull(got);
        Assert.IsTrue(got.IsDeleted);
    }
}
