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
    private SyncServer? _server;
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;

    public PairingWindow(ISyncStore store, string dbPath)
    {
        InitializeComponent();
        _store = store;
        _pairing = PairingService.Open(dbPath);
        Loaded += (_, _) =>
        {
            StartServer();
            IssueNewToken();
            RefreshTrusted();
        };
        Closed += (_, _) => StopServer();
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
            token,
            exp = ((DateTimeOffset)exp).ToUnixTimeSeconds(),
        });
        QrImage.Source = RenderQr(json);
        TokenInfo.Text = $"Код действует до {exp:HH:mm:ss} (5 минут, одноразовый).";
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
