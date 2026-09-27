using System.Net.Sockets;

namespace SyncNote.Core;

public sealed record SyncSessionResult(int Pushed, int Pulled, int Conflicts);

// TCP-клиент сессии (петля, Wi-Fi Direct TCP). Логика — в StreamSession.
public sealed class SyncClient(
    string host, int port, string deviceId, string deviceName, string? token)
{
    public async Task<SyncSessionResult> PushAndPullAsync(
        ISyncStore store, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        using var stream = client.GetStream();
        return await StreamSession.ClientSideAsync(
            store, stream, deviceId, deviceName, token, ct);
    }
}
