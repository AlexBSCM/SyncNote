using System.IO;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Foundation;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using SyncNote.Core;

namespace SyncNote.Windows;

// Bluetooth RFCOMM-резерв: ПК принимает входящие соединения
// (телефон инициирует после спаривания в настройках ОС).
// Общий UUID с Android — см. protocol/pairing-and-sync.md.
// NOT-VERIFIED: needs hardware — только компиляция; проверка — T7.
public sealed class BluetoothServer : IDisposable
{
    public static readonly Guid ServiceUuid =
        new("8f6d4b2a-1c3e-4a5f-9b6d-7e8f9a0b1c2d");

    private readonly ISyncStore _store;
    private readonly PairingService _pairing;
    private RfcommServiceProvider? _provider;
    private StreamSocketListener? _listener;
    private bool _disposed;

    public string Status { get; private set; } = "Bluetooth не запущен.";
    public bool IsActive => _provider is not null;

    public BluetoothServer(ISyncStore store, PairingService pairing)
    {
        _store = store;
        _pairing = pairing;
    }

    public async Task StartAsync()
    {
        Stop();
        _provider = await RfcommServiceProvider.CreateAsync(
            RfcommServiceId.FromUuid(ServiceUuid));
        _listener = new StreamSocketListener();
        _listener.ConnectionReceived += OnConnection;
        await _listener.BindServiceNameAsync(
            _provider.ServiceId.AsString(),
            SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
        _provider.StartAdvertising(_listener);
        Status = "Bluetooth: жду подключения (устройство должно быть спарено в настройках ОС).";
    }

    private async void OnConnection(
        StreamSocketListener sender,
        StreamSocketListenerConnectionReceivedEventArgs args)
    {
        try
        {
            using var socket = args.Socket;
            using var stream = new JoinedStream(
                new WinRtInputStream(socket.InputStream),
                new WinRtOutputStream(socket.OutputStream));
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await StreamSession.ServerSideAsync(
                _store, _pairing, stream, cts.Token);
        }
        catch { }
    }

    public void Stop()
    {
        try { _provider?.StopAdvertising(); } catch { }
        _listener?.Dispose();
        _listener = null;
        _provider = null;
        Status = "Bluetooth не запущен.";
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _disposed = true;
        }
    }
}

// Адаптеры WinRT-потоков в System.IO.Stream без System.Runtime.WindowsRuntime
// (её нет в net8.0-windows): ожидание через Completed + TaskCompletionSource.
internal sealed class WinRtInputStream(IInputStream source) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken ct)
    {
        using var reader = new DataReader(source)
        {
            InputStreamOptions = InputStreamOptions.Partial,
        };
        uint loaded = await AwaitWinRt(reader.LoadAsync(
            (uint)Math.Min(count, 65536)), ct);
        if (loaded == 0)
            return 0;
        var bytes = new byte[loaded];
        reader.ReadBytes(bytes);
        System.Buffer.BlockCopy(bytes, 0, buffer, offset, (int)loaded);
        return (int)loaded;
    }

    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    internal static Task<T> AwaitWinRt<T>(
        IAsyncOperation<T> op, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>();
        op.Completed = (o, s) =>
        {
            if (s == AsyncStatus.Completed)
                tcs.TrySetResult(o.GetResults());
            else if (s == AsyncStatus.Error)
                tcs.TrySetException(o.ErrorCode);
            else
                tcs.TrySetCanceled();
        };
        if (ct.CanBeCanceled)
            ct.Register(() =>
            {
                try { op.Cancel(); } catch { }
                tcs.TrySetCanceled();
            });
        return tcs.Task;
    }
}

internal sealed class WinRtOutputStream(IOutputStream dest) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override async Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken ct)
    {
        using var writer = new DataWriter(dest);
        var bytes = new byte[count];
        System.Buffer.BlockCopy(buffer, offset, bytes, 0, count);
        writer.WriteBytes(bytes);
        await WinRtInputStream.AwaitWinRt(writer.StoreAsync(), ct);
        await WinRtInputStream.AwaitWinRt(writer.FlushAsync(), ct);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
