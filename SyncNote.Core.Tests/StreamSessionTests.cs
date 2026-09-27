using SyncNote.Core;

namespace SyncNote.Core.Tests;

[TestClass]
public sealed class StreamSessionTests
{
    private static string TempDb(string tag) =>
        Path.Combine(Path.GetTempPath(), $"syncnote-stream-{tag}-{Guid.NewGuid():N}.db");

    [TestMethod]
    public async Task FullSession_OverMemoryStreams()
    {
        using var serverStore = new SqliteNoteStore(TempDb("srv"));
        using var clientStore = new SqliteNoteStore(TempDb("cli"));
        var note = serverStore.Add("Через стрим", "тело");
        serverStore.AddChecklistItem(note.Id, "пункт");

        using var pairing = PairingService.Open(TempDb("pair"));
        var (token, _) = pairing.IssueToken();

        var (endA, endB) = DuplexStream.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = StreamSession.ServerSideAsync(serverStore, pairing, endA, cts.Token);
        var clientTask = StreamSession.ClientSideAsync(
            clientStore, endB, clientStore.DeviceId, "MemPhone", token, cts.Token);

        var result = await clientTask;
        endB.Dispose();
        await serverTask;

        Assert.AreEqual(0, result.Pushed);
        Assert.AreEqual(1, result.Pulled);
        Assert.AreEqual("Через стрим", clientStore.List()[0].Title);
        Assert.AreEqual(1, clientStore.GetChecklist(note.Id).Count);
        Assert.IsTrue(pairing.IsTrusted(clientStore.DeviceId));
    }

    [TestMethod]
    public async Task Untrusted_OverMemoryStreams_Rejected()
    {
        using var serverStore = new SqliteNoteStore(TempDb("srv"));
        using var pairing = PairingService.Open(TempDb("pair"));
        var (endA, endB) = DuplexStream.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var serverTask = StreamSession.ServerSideAsync(serverStore, pairing, endA, cts.Token);
        await Assert.ThrowsExceptionAsync<HelloRejectedException>(() =>
            StreamSession.ClientSideAsync(serverStore, endB, "stranger", "X", null, cts.Token));
        endB.Dispose();
        await serverTask;
    }
}
