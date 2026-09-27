using SyncNote.Core;

namespace SyncNote.Core.Tests;

[TestClass]
public sealed class SyncTransportTests
{
    private static string TempDb(string tag) =>
        Path.Combine(Path.GetTempPath(), $"syncnote-{tag}-{Guid.NewGuid():N}.db");

    [TestInitialize]
    public void RedirectLog() =>
        ServerLog.Path = Path.Combine(Path.GetTempPath(), $"syncnote-test-{Guid.NewGuid():N}.log");

    [TestMethod]
    public async Task FullSession_WithToken_SyncsNotesAndFiles()
    {
        using var serverStore = new SqliteNoteStore(TempDb("srv"));
        using var clientStore = new SqliteNoteStore(TempDb("cli"));
        var note = serverStore.Add("От сервера", "текст **жирный**");
        serverStore.AddChecklistItem(note.Id, "пункт");
        var src = Path.Combine(Path.GetTempPath(), $"syncnote-t-{Guid.NewGuid():N}.bin");
        var payload = new byte[100_000];
        new Random(7).NextBytes(payload);
        File.WriteAllBytes(src, payload);
        serverStore.AddAttachment(note.Id, src);
        File.Delete(src);

        using var pairing = PairingService.Open(TempDb("pair"));
        var (token, _) = pairing.IssueToken();
        using var server = new SyncServer(serverStore, pairing, 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = server.RunAsync(cts.Token);

        var client = new SyncClient("127.0.0.1", server.Port,
            clientStore.DeviceId, "TestPhone", token);
        var result = await client.PushAndPullAsync(clientStore, cts.Token);
        cts.Cancel();
        await run;

        Assert.AreEqual(0, result.Pushed);
        Assert.AreEqual(1, result.Pulled);
        var got = clientStore.List();
        Assert.AreEqual(1, got.Count);
        Assert.AreEqual("От сервера", got[0].Title);
        Assert.AreEqual(1, clientStore.GetChecklist(note.Id).Count);
        var files = clientStore.GetAttachments(note.Id);
        Assert.AreEqual(1, files.Count);
        CollectionAssert.AreEqual(payload,
            File.ReadAllBytes(Path.Combine(clientStore.FilesDirectory, files[0].StoredName)));
        Assert.IsTrue(pairing.IsTrusted(clientStore.DeviceId));
    }

    [TestMethod]
    public async Task Untrusted_WithoutToken_Rejected()
    {
        using var serverStore = new SqliteNoteStore(TempDb("srv"));
        using var pairing = PairingService.Open(TempDb("pair"));
        using var server = new SyncServer(serverStore, pairing, 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = server.RunAsync(cts.Token);

        using var clientStore = new SqliteNoteStore(TempDb("cli"));
        var client = new SyncClient("127.0.0.1", server.Port,
            clientStore.DeviceId, "Stranger", null);
        await Assert.ThrowsExceptionAsync<HelloRejectedException>(
            () => client.PushAndPullAsync(clientStore, cts.Token));
        cts.Cancel();
        await run;
        Assert.AreEqual(0, serverStore.List().Count);
    }

    [TestMethod]
    public async Task TwoWay_ClientNote_ReachesServer()
    {
        using var serverStore = new SqliteNoteStore(TempDb("srv"));
        using var clientStore = new SqliteNoteStore(TempDb("cli"));
        clientStore.Add("От клиента", "тело");

        using var pairing = PairingService.Open(TempDb("pair"));
        var (token, _) = pairing.IssueToken();
        using var server = new SyncServer(serverStore, pairing, 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = server.RunAsync(cts.Token);

        var client = new SyncClient("127.0.0.1", server.Port,
            clientStore.DeviceId, "Phone", token);
        var result = await client.PushAndPullAsync(clientStore, cts.Token);
        cts.Cancel();
        await run;

        Assert.AreEqual(1, result.Pushed);
        Assert.AreEqual("От клиента", serverStore.List()[0].Title);
    }

    [TestMethod]
    public async Task CancelMidTransfer_DoesNotCorruptStore()
    {
        using var serverStore = new SqliteNoteStore(TempDb("srv"));
        using var clientStore = new SqliteNoteStore(TempDb("cli"));
        var note = clientStore.Add("Большая", "передача");
        var src = Path.Combine(Path.GetTempPath(), $"syncnote-big-{Guid.NewGuid():N}.bin");
        var payload = new byte[8_000_000];
        new Random(9).NextBytes(payload);
        File.WriteAllBytes(src, payload);
        clientStore.AddAttachment(note.Id, src);
        File.Delete(src);

        using var pairing = PairingService.Open(TempDb("pair"));
        var (token, _) = pairing.IssueToken();
        using var server = new SyncServer(serverStore, pairing, 0);
        using var runCts = new CancellationTokenSource();
        var run = server.RunAsync(runCts.Token);

        using var opCts = new CancellationTokenSource();
        opCts.CancelAfter(100);
        var client = new SyncClient("127.0.0.1", server.Port,
            clientStore.DeviceId, "Phone", token);
        try
        {
            await client.PushAndPullAsync(clientStore, opCts.Token);
        }
        catch (OperationCanceledException) { }
        runCts.Cancel();
        await run;

        // Либо заметки нет вообще, либо она цела с целым файлом.
        var found = serverStore.TryGet(note.Id);
        if (found is not null)
        {
            var files = serverStore.GetAttachments(note.Id);
            Assert.AreEqual(1, files.Count);
            CollectionAssert.AreEqual(payload,
                File.ReadAllBytes(Path.Combine(serverStore.FilesDirectory, files[0].StoredName)));
        }
    }
}
