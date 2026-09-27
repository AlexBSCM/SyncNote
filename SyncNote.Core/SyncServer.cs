using System.Net;
using System.Net.Sockets;

namespace SyncNote.Core;

// TCP-сервер сессии (петля, Wi-Fi Direct TCP). Логика — в StreamSession.
public sealed class SyncServer : IAsyncDisposable, IDisposable
{
    private readonly ISyncStore _store;
    private readonly PairingService _pairing;
    private readonly TcpListener _listener;

    public SyncServer(ISyncStore store, PairingService pairing, int port = 0)
    {
        _store = store;
        _pairing = pairing;
        // Any: доступен и с петли, и с Wi-Fi Direct GO-адреса.
        // Требуется токен/доверие; брандмауэр Windows спросит разрешение.
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = HandleAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
            await StreamSession.ServerSideAsync(_store, _pairing, stream, ct);
    }

    public void Dispose() => _listener.Stop();

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
