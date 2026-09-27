using System.Windows;
using SyncNote.Core;

namespace SyncNote.Windows;

// Клиент синхронизации: подключение к узлу (петля для локальной проверки,
// Wi-Fi Direct адрес — на реальном железе). Состояния по protocol/:
// ищем → синхронизируется → готово/ошибка. Длительная операция отменяется.
public partial class SyncDialog : Window
{
    private readonly ISyncStore _store;
    private readonly Action<string> _setMainStatus;
    private readonly Action _refreshMain;
    private CancellationTokenSource? _cts;

    public SyncDialog(ISyncStore store, Action<string> setMainStatus, Action refreshMain)
    {
        InitializeComponent();
        _store = store;
        _setMainStatus = setMainStatus;
        _refreshMain = refreshMain;
        DeviceBox.Text = Environment.MachineName;
        Closed += (_, _) => { try { _cts?.Cancel(); } catch { } };
    }

    private void SetState(string text)
    {
        Dispatcher.Invoke(() =>
        {
            StateText.Text = text;
            _setMainStatus(text);
        });
    }

    private async void GoButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out int port) || port is < 1 or > 65535)
        {
            SetState("Ошибка: некорректный порт.");
            return;
        }
        GoButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        _cts = new CancellationTokenSource();
        SetState("Ищем устройство…");
        try
        {
            var token = string.IsNullOrWhiteSpace(TokenBox.Text) ? null : TokenBox.Text.Trim();
            var client = new SyncClient(HostBox.Text.Trim(), port,
                _store.DeviceId, DeviceBox.Text.Trim(), token);
            SetState("Синхронизируется…");
            var result = await Task.Run(() => client.PushAndPullAsync(_store, _cts.Token));
            SetState($"Готово: отправлено {result.Pushed}, получено {result.Pulled}, " +
                $"конфликтов {result.Conflicts}.");
            _refreshMain();
            if (result.Conflicts > 0)
            {
                MessageBox.Show(this,
                    "Есть конфликты — откройте «Конфликты» в строке состояния.",
                    "Синхронизация", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            SetState("Отменено пользователем. Хранилище не повреждено.");
        }
        catch (HelloRejectedException ex)
        {
            SetState($"Ошибка: {ex.Message}");
        }
        catch (Exception ex)
        {
            SetState($"Ошибка: {ex.Message}");
        }
        finally
        {
            Dispatcher.Invoke(() =>
            {
                GoButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
            });
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
    }
}
