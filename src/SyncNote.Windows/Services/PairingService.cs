using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using QRCoder;
using SyncNote.Core;
using SyncNote.Core.Services;

namespace SyncNote.Windows.Services;

// Серверная сторона сопряжения: одноразовый токен + QR + ожидание пира.
// Токен сгорает при первом предъявлении (успех или отказ); в логи
// и статусы токен/IP-пейлоад не попадают — только факты событий.
public sealed class PairingService : IDisposable
{
    // Фиксированный порт синка: телефон идёт на него напрямую
    // (или через adb reverse tcp:48211 tcp:48211).
    public const int DefaultPort = 48211;

    private readonly TcpListener _listener;
    private readonly string _dbPath;
    private readonly string _deviceId;
    private readonly string _tempDir;
    private string? _token;
    private bool _disposed;

    private PairingService(
        TcpListener listener, string host, int port, string token,
        string dbPath, string deviceId, string tempDir)
    {
        _listener = listener;
        Host = host;
        Port = port;
        _token = token;
        _dbPath = dbPath;
        _deviceId = deviceId;
        _tempDir = tempDir;
    }

    public string Host { get; }
    public int Port { get; }
    public string Token => _token
        ?? throw new InvalidOperationException("Токен уже использован.");

    // Пейлоад QR: только нужное для соединения (правило Security).
    public string QrPayload => new JsonObject
    {
        ["ip"] = Host,
        ["port"] = Port,
        ["token"] = Token,
    }.ToJsonString();

    public byte[] QrPng
    {
        get
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(QrPayload, QRCodeGenerator.ECCLevel.Q);
            return new PngByteQRCode(data).GetGraphic(pixelsPerModule: 8);
        }
    }

    public async Task<SyncResult> WaitForPeerAsync(
        Action<string> status, CancellationToken ct = default)
    {
        status("Ожидание подключения...");
        await using var transport = await TcpFrameTransport.AcceptAsync(_listener, ct);
        string? presented = _token;
        try
        {
            status("Подключено, проверка токена...");
            var notes = new SqliteNoteRepo(_dbPath, _deviceId);
            var io = new WindowsFileIo(Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(_dbPath)) ?? ".", "files"));
            var files = new SqliteFileRepo(_dbPath, _deviceId, io);
            var sync = new SqlSyncStateStore(_dbPath);
            var engine = new SyncEngine(notes, files, io, sync, _deviceId, _tempDir);
            status("Синхронизация...");
            var res = await engine.RunSessionAsync(
                transport, isInitiator: false, ct, expectedToken: presented);
            status($"Готово: отправлено {res.Pushed}, получено {res.Pulled},"
                + $" конфликтов {res.Conflicts}.");
            return res;
        }
        catch (HelloRejectedException)
        {
            status("Чужой токен — в соединении отказано.");
            throw;
        }
        finally
        {
            // Одноразовость: сгорел при первом предъявлении.
            _token = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _listener.Stop(); } catch { }
    }

    public static PairingService Start(
        string dbPath, string deviceId, string host, int port, string? tempDir = null)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        try
        {
            listener.Start();
        }
        catch
        {
            listener.Stop();
            throw new IOException($"Порт {port} занят.");
        }
        byte[] raw = RandomNumberGenerator.GetBytes(32);
        string token = Convert.ToHexString(raw).ToLowerInvariant();
        int bound = ((IPEndPoint)listener.LocalEndpoint).Port;
        string tmp = tempDir ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        return new PairingService(listener, host, bound, token, dbPath, deviceId, tmp);
    }
}
