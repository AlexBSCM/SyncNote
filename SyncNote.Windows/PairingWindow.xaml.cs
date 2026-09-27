using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;
using SyncNote.Core;

namespace SyncNote.Windows;

// Окно сопряжения: сервер синхронизации (петля), QR с одноразовым токеном,
// доверенные устройства. Wi-Fi Direct — на реальном железе (этап 4, ТЗ).
public partial class PairingWindow : Window
{
    private readonly ISyncStore _store;
    private readonly PairingService _pairing;
    private readonly WifiDirectGroup _p2pGroup = new();
    private BluetoothServer? _btServer;
    private SyncServer? _server;
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;

    public PairingWindow(ISyncStore store, string dbPath)
    {
        InitializeComponent();
        _store = store;
        _pairing = PairingService.Open(dbPath);
        P2pInfo.Text = _p2pGroup.Status;
        Loaded += (_, _) =>
        {
            StartServer();
            IssueNewToken();
            RefreshTrusted();
        };
        Closed += (_, _) =>
        {
            StopServer();
            _p2pGroup.Dispose();
            _btServer?.Dispose();
        };
    }

    private void StartServer()
    {
        if (_server is not null)
            return;
        _server = new SyncServer(_store, _pairing, 0);
        _serverCts = new CancellationTokenSource();
        _serverTask = _server.RunAsync(_serverCts.Token);
        ServerInfo.Text = $"Принимаю подключения: 127.0.0.1:{_server.Port}";
        StartStopButton.Content = "Остановить";
    }

    private void StopServer()
    {
        try { _serverCts?.Cancel(); } catch { }
        _server?.Dispose();
        _server = null;
        ServerInfo.Text = "Сервер остановлен.";
        StartStopButton.Content = "Принимать подключения";
    }

    private void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null)
        {
            StartServer();
            IssueNewToken();
        }
        else
        {
            StopServer();
        }
    }

    private void NewTokenButton_Click(object sender, RoutedEventArgs e) => IssueNewToken();

    private void IssueNewToken()
    {
        var (token, exp) = _pairing.IssueToken();
        var json = JsonSerializer.Serialize(new
        {
            v = 1,
            host = "127.0.0.1",
            port = _server?.Port ?? 0,
            p2pName = Environment.MachineName,
            token,
            exp = ((DateTimeOffset)exp).ToUnixTimeSeconds(),
        });
        QrImage.Source = RenderQr(json);
        TokenInfo.Text = $"Код действует до {exp:HH:mm:ss} (5 минут, одноразовый).";
    }

    private async void BtButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_btServer is not null)
            {
                _btServer.Dispose();
                _btServer = null;
                BtButton.Content = "Принимать по Bluetooth";
                BtInfo.Text = "Bluetooth не запущен.";
                return;
            }
            _btServer = new BluetoothServer(_store, _pairing);
            await _btServer.StartAsync();
            BtButton.Content = "Остановить Bluetooth";
            BtInfo.Text = _btServer.Status;
        }
        catch (Exception ex)
        {
            BtInfo.Text = $"Bluetooth не запустился: {ex.Message} " +
                "Нужен включённый Bluetooth-адаптер (проверка на железе, T7).";
            _btServer = null;
            BtButton.Content = "Принимать по Bluetooth";
        }
    }
    private void P2pButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_p2pGroup.IsActive)
            {
                _p2pGroup.Stop();
                P2pButton.Content = "Создать Wi-Fi Direct группу";
            }
            else
            {
                _p2pGroup.Start();
                P2pButton.Content = "Закрыть Wi-Fi Direct группу";
            }
        }
        catch (Exception ex)
        {
            P2pInfo.Text = $"Не удалось: {ex.Message}";
        }
        finally
        {
            if (_p2pGroup.IsActive)
                P2pInfo.Text = _p2pGroup.Status;
            IssueNewToken();
        }
    }

    private static BitmapImage RenderQr(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        using var qr = new QRCode(data);
        using var bmp = qr.GetGraphic(8);
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = ms;
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }

    private void RefreshTrusted() =>
        TrustedList.ItemsSource = _pairing.ListTrusted();

    private void Untrust_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string deviceId)
        {
            _pairing.Untrust(deviceId);
            RefreshTrusted();
        }
    }
}
