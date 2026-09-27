using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;

namespace SyncNote.Windows;

// Автономная Wi-Fi Direct группа (Group Owner) на ПК.
// Телефон находит устройство по имени через discoverPeers и подключается
// по WPS; SSID/пароль группы API не раскрывает — сверка идёт по имени ПК.
// NOT-VERIFIED: needs hardware — запуск только на адаптере с поддержкой
// (проверка: netsh wlan show drivers). Без адаптера — честная ошибка.
public sealed class WifiDirectGroup : IDisposable
{
    private WiFiDirectAdvertisementPublisher? _publisher;
    private bool _disposed;

    public string Status { get; private set; } = "Группа не создана.";
    public bool IsActive => _publisher?.Status ==
        WiFiDirectAdvertisementPublisherStatus.Started;

    // Есть ли вообще Wi-Fi Direct устройства в системе (быстрая проверка).
    public static async Task<bool> IsSupportedAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(
                WiFiDirectDevice.GetDeviceSelector());
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Start()
    {
        Stop();
        _publisher = new WiFiDirectAdvertisementPublisher
        {
            Advertisement =
            {
                IsAutonomousGroupOwnerEnabled = true,
                ListenStateDiscoverability =
                    WiFiDirectAdvertisementListenStateDiscoverability.Normal,
            },
        };
        _publisher.StatusChanged += (_, args) =>
        {
            Status = args.Status switch
            {
                WiFiDirectAdvertisementPublisherStatus.Started =>
                    "Группа создана. Имя для поиска с телефона: " + Environment.MachineName,
                WiFiDirectAdvertisementPublisherStatus.Aborted =>
                    $"Ошибка группы: {args.Error}. Проверьте адаптер (netsh wlan show drivers).",
                _ => $"Статус группы: {args.Status}.",
            };
        };
        _publisher.Start();
        if (_publisher.Status == WiFiDirectAdvertisementPublisherStatus.Aborted)
            throw new InvalidOperationException(
                $"Группа не создана: {_publisher.Status}.");
        Status = "Группа запускается…";
    }

    public void Stop()
    {
        try { _publisher?.Stop(); } catch { }
        _publisher = null;
        Status = "Группа не создана.";
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
