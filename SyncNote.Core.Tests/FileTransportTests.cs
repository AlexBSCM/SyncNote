using System.Net.Sockets;
using System.Text.Json.Nodes;
using SyncNote.Core;

namespace SyncNote.Core.Tests;

// Интеграционные тесты F.4: standalone-файлы поверх loopback-транспорта.
// Кадры file_register/query_has_hash, пропуск байтов при дедупе,
// стриминг приёма во временный файл, игнор неизвестных типов, флаг.
[TestClass]
public sealed class FileTransportTests
{
    private static string TempDb(string tag) =>
        Path.Combine(Path.GetTempPath(), $"syncnote-filet-{tag}-{Guid.NewGuid():N}.db");

    [TestInitialize]
    public void Setup()
    {
        FeatureFlags.EnableSeparateFiles = true;
        ServerLog.Path = Path.Combine(Path.GetTempPath(), $"syncnote-test-{Guid.NewGuid():N}.log");
    }

    [TestCleanup]
    public void Restore() => FeatureFlags.EnableSeparateFiles = true;

    private static string WriteSource(byte[] content)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"syncnote-filesrc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"f-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    private sealed class RawPeer(TcpClient client) : IAsyncDisposable
    {
        private readonly NetworkStream _stream = client.GetStream();

        public Task Send(JsonObject msg, CancellationToken ct) =>
            Frame.WriteAsync(_stream, msg, ct);

        public Task<JsonObject> Recv(CancellationToken ct) =>
            Frame.ReadAsync(_stream, ct);

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync();
            client.Dispose();
        }
    }

    private static async Task<RawPeer> ConnectHelloAsync(
        int port, string deviceId, string? token, CancellationToken ct)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port, ct);
        var peer = new RawPeer(tcp);
        var hello = new JsonObject
        {
            ["t"] = "hello",
            ["deviceId"] = deviceId,
            ["deviceName"] = "RawTest",
        };
        if (token is not null)
            hello["token"] = token;
        await peer.Send(hello, ct);
        var greet = await peer.Recv(ct);
        if (greet["t"]?.GetValue<string>() != "hello_ok")
            throw new HelloRejectedException(
                greet["reason"]?.GetValue<string>() ?? "отказ");
        return peer;
    }

    private static JsonObject KnowledgeFrame() =>
        new() { ["t"] = "sync_begin", ["knowledge"] = new JsonArray() };

    [TestMethod]
    public async Task StandaloneFileSync_Loopback_BytesIdentical()
    {
        using var serverStore = new SqliteNoteStore(TempDb("srv"));
        using var clientStore = new SqliteNoteStore(TempDb("cli"));
        var payload = new byte[50_000];
        new Random(11).NextBytes(payload);
        var src = WriteSource(payload);
        FileEntry added;
        try { added = serverStore.AddFile(src); }
        finally { Directory.Delete(Path.GetDirectoryName(src)!, recursive: true); }

        using var pairing = PairingService.Open(TempDb("pair"));
        var (token, _) = pairing.IssueToken();
        using var server = new SyncServer(serverStore, pairing, 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = server.RunAsync(cts.Token);

        var client = new SyncClient("127.0.0.1", server.Port,
            clientStore.DeviceId, "Phone", token);
        var result = await client.PushAndPullAsync(clientStore, cts.Token);
        cts.Cancel();
        await run;

        Assert.AreEqual(0, result.Pushed);
        Assert.AreEqual(1, result.Pulled);
        var got = clientStore.GetFiles();
        Assert.AreEqual(1, got.Count);
        Assert.AreEqual(added.Id, got[0].Id);
        Assert.AreEqual("f-", got[0].Name.Substring(0, 2));
        CollectionAssert.AreEqual(payload,
            File.ReadAllBytes(Path.Combine(clientStore.FilesDirectory, got[0].StoredName)));
        // Один физический файл с нашим sha на принимающей стороне
        // (каталог общий с другими тестами — считаем точечно).
        Assert.AreEqual(1, Directory.GetFiles(clientStore.FilesDirectory, got[0].StoredName).Length);
    }

    [TestMethod]
    public async Task SecondSync_SendsNothingNew()
    {
        using var serverStore = new SqliteNoteStore(TempDb("srv"));
        using var clientStore = new SqliteNoteStore(TempDb("cli"));
        var src = WriteSource(new byte[] { 1, 2, 3 });
        try { serverStore.AddFile(src); }
        finally { Directory.Delete(Path.GetDirectoryName(src)!, recursive: true); }

        using var pairing = PairingService.Open(TempDb("pair"));
        var (token, _) = pairing.IssueToken();
        using var server = new SyncServer(serverStore, pairing, 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = server.RunAsync(cts.Token);

        var mkClient = (string? tok) => new SyncClient("127.0.0.1", server.Port,
            clientStore.DeviceId, "Phone", tok);
        var first = await mkClient(token).PushAndPullAsync(clientStore, cts.Token);
        Assert.AreEqual(1, first.Pulled);

        var second = await mkClient(null).PushAndPullAsync(clientStore, cts.Token);
        Assert.AreEqual(0, second.Pushed);
        Assert.AreEqual(0, second.Pulled);
        cts.Cancel();
        await run;
    }

    [TestMethod]
    public async Task QueryHasHash_Roundtrip()
    {
        using var serverStore = new SqliteNoteStore(TempDb("srv"));
        var src = WriteSource(new byte[] { 7, 7, 7 });
        string sha;
        try
        {
            var e = serverStore.AddFile(src);
            sha = e.Sha256;
        }
        finally { Directory.Delete(Path.GetDirectoryName(src)!, recursive: true); }

        using var pairing = PairingService.Open(TempDb("pair"));
        var (token, _) = pairing.IssueToken();
        using var server = new SyncServer(serverStore, pairing, 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = server.RunAsync(cts.Token);

        await using var peer = await ConnectHelloAsync(
            server.Port, "raw-dev", token, cts.Token);
        await peer.Send(KnowledgeFrame(), cts.Token);

        // Известный хеш -> has:true.
        await peer.Send(new JsonObject
        {
            ["t"] = "query_has_hash",
            ["sha256"] = sha,
        }, cts.Token);
        var yes = await peer.Recv(cts.Token);
        Assert.AreEqual("hash_response", yes["t"]?.GetValue<string>());
        Assert.AreEqual(sha, yes["sha256"]?.GetValue<string>());
        Assert.IsTrue(yes["has"]?.GetValue<bool>());

        // Неизвестный хеш -> has:false.
        await peer.Send(new JsonObject
        {
            ["t"] = "query_has_hash",
            ["sha256"] = new string('b', 64),
        }, cts.Token);
        var no = await peer.Recv(cts.Token);
        Assert.AreEqual("hash_response", no["t"]?.GetValue<string>());
        Assert.IsFalse(no["has"]?.GetValue<bool>());

        // Чистое завершение: сервер уходит в фазу отдачи (пусто) и закрывает.
        await peer.Send(new JsonObject { ["t"] = "sync_end" }, cts.Token);
        var end = await peer.Recv(cts.Token);
        Assert.AreEqual("sync_end", end["t"]?.GetValue<string>());
        cts.Cancel();
        await run;
    }

    [TestMethod]
    public async Task UnknownFrameType_Ignored_SessionContinues()
    {
        using var serverStore = new SqliteNoteStore(TempDb("srv"));
        using var pairing = PairingService.Open(TempDb("pair"));
        var (token, _) = pairing.IssueToken();
        using var server = new SyncServer(serverStore, pairing, 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = server.RunAsync(cts.Token);

        await using var peer = await ConnectHelloAsync(
            server.Port, "raw-dev", token, cts.Token);
        await peer.Send(KnowledgeFrame(), cts.Token);
        // Мусорный кадр из "будущей" версии — сессия не должна рваться.
        await peer.Send(new JsonObject
        {
            ["t"] = "frobnicate",
            ["future"] = 1,
        }, cts.Token);
        await peer.Send(new JsonObject { ["t"] = "sync_end" }, cts.Token);
        var end = await peer.Recv(cts.Token);
        Assert.AreEqual("sync_end", end["t"]?.GetValue<string>());
        cts.Cancel();
        await run;
    }

    [TestMethod]
    public async Task StreamingIntegrity_5MB_ThroughSession()
    {
        using var serverStore = new SqliteNoteStore(TempDb("srv"));
        using var clientStore = new SqliteNoteStore(TempDb("cli"));
        var payload = new byte[5_000_000];
        new Random(13).NextBytes(payload);
        var src = WriteSource(payload);
        string sha;
        try
        {
            var e = serverStore.AddFile(src);
            sha = e.Sha256;
        }
        finally { Directory.Delete(Path.GetDirectoryName(src)!, recursive: true); }

        using var pairing = PairingService.Open(TempDb("pair"));
        var (token, _) = pairing.IssueToken();
        using var server = new SyncServer(serverStore, pairing, 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var run = server.RunAsync(cts.Token);

        var client = new SyncClient("127.0.0.1", server.Port,
            clientStore.DeviceId, "Phone", token);
        var result = await client.PushAndPullAsync(clientStore, cts.Token);
        cts.Cancel();
        await run;

        Assert.AreEqual(1, result.Pulled);
        var got = clientStore.TryGetFile(clientStore.GetFiles()[0].Id);
        Assert.IsNotNull(got);
        var bytes = File.ReadAllBytes(Path.Combine(clientStore.FilesDirectory, got!.StoredName));
        Assert.AreEqual(payload.Length, bytes.Length);
        CollectionAssert.AreEqual(payload, bytes);
        Assert.AreEqual(sha, got.Sha256);
    }

    [TestMethod]
    public async Task DisabledFlag_FileRegister_NoopAck_NoRow()
    {
        using var serverStore = new SqliteNoteStore(TempDb("srv"));
        using var pairing = PairingService.Open(TempDb("pair"));
        var (token, _) = pairing.IssueToken();
        using var server = new SyncServer(serverStore, pairing, 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = server.RunAsync(cts.Token);

        await using var peer = await ConnectHelloAsync(
            server.Port, "raw-dev", token, cts.Token);
        await peer.Send(KnowledgeFrame(), cts.Token);

        // Выключаем флаг уже после handshake: file_register должен
        // закончиться честным NoOp-ack без создания строки.
        FeatureFlags.EnableSeparateFiles = false;
        try
        {
            var dto = new SyncFileDto
            {
                Id = Guid.NewGuid(),
                Rev = 1,
                Name = "x.bin",
                Sha256 = new string('c', 64),
                UpdatedAt = DateTime.UtcNow,
                Author = "raw",
            };
            await peer.Send(new JsonObject
            {
                ["t"] = "file_register",
                ["file"] = SyncJson.ToFileNode(dto),
            }, cts.Token);
            var ack = await peer.Recv(cts.Token);
            Assert.AreEqual("applied", ack["t"]?.GetValue<string>());
            Assert.AreEqual("NoOp", ack["result"]?.GetValue<string>());
            Assert.AreEqual(0, serverStore.GetFiles(includeDeleted: true).Count);
        }
        finally
        {
            FeatureFlags.EnableSeparateFiles = true;
        }
        await peer.Send(new JsonObject { ["t"] = "sync_end" }, cts.Token);
        var end = await peer.Recv(cts.Token);
        Assert.AreEqual("sync_end", end["t"]?.GetValue<string>());
        cts.Cancel();
        await run;
    }
}
