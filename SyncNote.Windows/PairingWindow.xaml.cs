using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;
using SyncNote.Core;

namespace SyncNote.Windows;

// Окно сопряжения: QR с одноразовым токеном и доверенные устройства.
// Сервер синхронизации работает всегда (владелец — MainWindow).
public partial class PairingWindow : Window
{
    private readonly ISyncStore _store;
    private readonly PairingService _pairing;
    private readonly int _port;
    private readonly WifiDirectGroup _p2pGroup = new();
    private BluetoothServer? _btServer;

    public PairingWindow(ISyncStore store, PairingService pairing, int port)
    {
        InitializeComponent();
        _store = store;
        _pairing = pairing;
        _port = port;
        ServerInfo.Text = $"Принимаю подключения: 127.0.0.1:{port}";
        P2pInfo.Text = _p2pGroup.Status;
        Loaded += (_, _) =>
        {
            IssueNewToken();
            RefreshTrusted();
        };
        Closed += (_, _) =>
        {
            _p2pGroup.Dispose();
            _btServer?.Dispose();
        };
    }

    private void NewTokenButton_Click(object sender, RoutedEventArgs e) => IssueNewToken();

    private void IssueNewToken()
    {
        var (token, exp) = _pairing.IssueToken();
        var hosts = GetLanIPv4s();
        var json = JsonSerializer.Serialize(new
        {
            v = 1,
            host = hosts.Count > 0 ? hosts[0] : "127.0.0.1",
            hosts,
            port = _port,
            p2pName = Environment.MachineName,
            token,
            exp = ((DateTimeOffset)exp).ToUnixTimeSeconds(),
        });
        QrImage.Source = RenderQr(json);
        TokenInfo.Text = $"Код действует до {exp:HH:mm:ss} (5 минут, одноразовый).";
    }

    // Кандидаты IPv4: сначала интерфейсы со шлюзом (настоящий LAN),
    // затем остальные. Петля 127.0.0.1 — только через adb reverse, вручную.
    private static List<string> GetLanIPv4s()
    {
        var withGateway = new List<string>();
        var others = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;
                bool hasGateway = ni.GetIPProperties().GatewayAddresses.Count > 0;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    var ip = addr.Address;
                    if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                        || System.Net.IPAddress.IsLoopback(ip))
                        continue;
                    (hasGateway ? withGateway : others).Add(ip.ToString());
                }
            }
        }
        catch { }
        var result = withGateway.Concat(others).Distinct().ToList();
        return result.Count > 0 ? result : new List<string> { "127.0.0.1" };
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
