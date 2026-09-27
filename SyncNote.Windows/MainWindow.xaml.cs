using System.Windows;
using System.Windows.Controls;
using SyncNote.Core;

namespace SyncNote.Windows;

public partial class MainWindow : Window
{
    public const int SyncPort = 48211;

    private readonly SqliteNoteStore _store = new(SqliteNoteStore.DefaultPath);
    private readonly PairingService _pairing = PairingService.Open(SqliteNoteStore.DefaultPath);
    private readonly SyncServer _server;
    private readonly CancellationTokenSource _serverCts = new();
    private readonly Task _serverTask;
    private CancellationTokenSource? _syncCts;
    private readonly WifiDirectGroup _p2pGroup = new();
    private BluetoothServer? _btServer;

    public MainWindow()
    {
        InitializeComponent();
        // Сервер синхронизации работает всегда, пока открыто приложение:
        // телефон пушит изменения сам, кнопки не нужны.
        _server = new SyncServer(_store, _pairing, SyncPort);
        _serverTask = _server.RunAsync(_serverCts.Token);
        ConnectionStatus.Text = $"Принимаю подключения: {SyncPort}";
        SyncDeviceBox.Text = Environment.MachineName;
        PairServerInfo.Text = $"Принимаю подключения: 127.0.0.1:{SyncPort}";
        P2pInfo.Text = _p2pGroup.Status;
        Closed += (_, _) =>
        {
            try { _serverCts.Cancel(); } catch { }
            try { _syncCts?.Cancel(); } catch { }
            _server.Dispose();
            _pairing.Dispose();
            _p2pGroup.Dispose();
            _btServer?.Dispose();
            _store.Dispose();
        };
        RefreshList();
        // Подтягиваем изменения с телефона без нажатий: тихое обновление списка.
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        timer.Tick += (_, _) => RefreshList();
        timer.Start();
    }

    private bool _suppressSelection;
    private List<SelectableNote> _managed = new();

    private sealed class SelectableNote(Note note, bool selected) : System.ComponentModel.INotifyPropertyChanged
    {
        public Note Note { get; } = note;
        private bool _selected = selected;
        public bool IsSelected
        {
            get => _selected;
            set
            {
                _selected = value;
                PropertyChanged?.Invoke(this,
                    new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private void RefreshList()
    {
        // Выбор сохраняем вручную: замена ItemsSource создаёт новые объекты
        // и роняет выделение. Событие при этом подавляем, чтобы таймер
        // не затирал набираемый текст.
        var selectedId = (NotesList.SelectedItem as Note)?.Id;
        var notes = _store.Search(SearchBox.Text);
        var keep = selectedId is not null
            ? notes.FirstOrDefault(n => n.Id == selectedId)
            : null;
        // Подавляем событие, только если выбор сохранён; удаление с другой
        // стороны показываем честно (пустое состояние).
        _suppressSelection = keep is not null;
        try
        {
            NotesList.ItemsSource = notes;
            NotesList.SelectedItem = keep; // null, если удалена с другой стороны
        }
        finally
        {
            _suppressSelection = false;
        }
        int conflicts = Conflicts.FindPairs(_store).Count;
        ConflictsButton.Content = $"Конфликты ({conflicts})";
        ConflictsButton.Visibility = conflicts > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshSettings();
    }

    private void RefreshSettings()
    {
        var notes = _store.List();
        long filesBytes = 0;
        int filesCount = 0;
        try
        {
            foreach (var f in System.IO.Directory.GetFiles(_store.FilesDirectory))
            {
                filesCount++;
                filesBytes += new System.IO.FileInfo(f).Length;
            }
        }
        catch { }
        long dbBytes = 0;
        try { dbBytes = new System.IO.FileInfo(SqliteNoteStore.DefaultPath).Length; } catch { }
        StorageInfo.Text = $"Заметок: {notes.Count}\n" +
            $"Файлов: {filesCount} ({filesBytes / 1024} КБ)\n" +
            $"База: {dbBytes / 1024} КБ\n" +
            $"Путь: {SqliteNoteStore.DefaultPath}\n" +
            $"Устройство: {_store.DeviceId[..Math.Min(8, _store.DeviceId.Length)]}…\n" +
            $"Принимаю подключения: {SyncPort}";
        TrustedBox.ItemsSource = _pairing.ListTrusted();
        var selected = _managed.Where(s => s.IsSelected).Select(s => s.Note.Id).ToHashSet();
        _managed = notes.Select(n => new SelectableNote(n, selected.Contains(n.Id))).ToList();
        foreach (var s in _managed)
            s.PropertyChanged += (_, _) => UpdateDeleteButton();
        AllNotesBox.ItemsSource = _managed;
        UpdateDeleteButton();
    }

    private void UpdateDeleteButton()
    {
        int count = _managed.Count(s => s.IsSelected);
        DeleteSelectedButton.Content = count == 0
            ? "Удалить выбранные"
            : $"Удалить выбранные ({count})";
    }

    private void SelectAllBox_Changed(object sender, RoutedEventArgs e)
    {
        bool v = SelectAllBox.IsChecked == true;
        foreach (var s in _managed)
            s.IsSelected = v;
        UpdateDeleteButton();
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var ids = _managed.Where(s => s.IsSelected).Select(s => s.Note.Id).ToList();
        if (ids.Count == 0)
        {
            MessageBox.Show(this, "Ничего не выбрано.", "Удаление",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(this,
            $"Удалить заметок: {ids.Count}? Они пропадут и на другом устройстве при синхронизации.",
            "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;
        foreach (var id in ids)
            _store.Delete(id);
        SelectAllBox.IsChecked = false;
        RefreshList();
    }

    private void Untrust_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string deviceId)
        {
            _pairing.Untrust(deviceId);
            RefreshSettings();
        }
    }

    private void ConflictsButton_Click(object sender, RoutedEventArgs e)
    {
        var w = new ConflictWindow(_store) { Owner = this };
        w.ShowDialog();
        RefreshList();
    }

    private void SetSyncState(string text)
    {
        Dispatcher.Invoke(() =>
        {
            SyncStateText.Text = text;
            ConnectionStatus.Text = text;
        });
    }

    private async void SyncGoButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SyncPortBox.Text, out int port) || port is < 1 or > 65535)
        {
            SetSyncState("Ошибка: некорректный порт.");
            return;
        }
        SyncGoButton.IsEnabled = false;
        SyncCancelButton.IsEnabled = true;
        _syncCts = new CancellationTokenSource();
        SetSyncState("Ищем устройство…");
        try
        {
            var token = string.IsNullOrWhiteSpace(SyncTokenBox.Text) ? null : SyncTokenBox.Text.Trim();
            var client = new SyncClient(SyncHostBox.Text.Trim(), port,
                _store.DeviceId, SyncDeviceBox.Text.Trim(), token);
            SetSyncState("Синхронизируется…");
            var result = await Task.Run(() => client.PushAndPullAsync(_store, _syncCts.Token));
            SetSyncState($"Готово: отправлено {result.Pushed}, получено {result.Pulled}, " +
                $"конфликтов {result.Conflicts}.");
            RefreshList();
            if (result.Conflicts > 0)
            {
                MessageBox.Show(this,
                    "Есть конфликты — откройте «Конфликты» в строке состояния.",
                    "Синхронизация", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            SetSyncState("Отменено пользователем. Хранилище не повреждено.");
        }
        catch (HelloRejectedException ex)
        {
            SetSyncState($"Ошибка: {ex.Message}");
        }
        catch (Exception ex)
        {
            SetSyncState($"Ошибка: {ex.Message}");
        }
        finally
        {
            SyncGoButton.IsEnabled = true;
            SyncCancelButton.IsEnabled = false;
        }
    }

    private void SyncCancelButton_Click(object sender, RoutedEventArgs e)
    {
        try { _syncCts?.Cancel(); } catch { }
    }

    private void PairNewToken_Click(object sender, RoutedEventArgs e) => IssuePairToken();

    private void IssuePairToken()
    {
        var (token, exp) = _pairing.IssueToken();
        var hosts = GetLanHosts();
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            v = 1,
            host = hosts.Count > 0 ? hosts[0] : "127.0.0.1",
            hosts,
            port = SyncPort,
            p2pName = Environment.MachineName,
            token,
            exp = ((DateTimeOffset)exp).ToUnixTimeSeconds(),
        });
        PairQrImage.Source = RenderQr(json);
        PairTokenInfo.Text = $"Код действует до {exp:HH:mm:ss} (5 минут, одноразовый).";
        RefreshSettings();
    }

    private static List<string> GetLanHosts()
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

    private static System.Windows.Media.Imaging.BitmapImage RenderQr(string text)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        using var qr = new QRCoder.QRCode(data);
        using var bmp = qr.GetGraphic(8);
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var img = new System.Windows.Media.Imaging.BitmapImage();
        img.BeginInit();
        img.StreamSource = ms;
        img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
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
            IssuePairToken();
        }
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

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshList();

    private void NotesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
            return;
        if (NotesList.SelectedItem is Note note)
        {
            EmptyHint.Visibility = Visibility.Collapsed;
            EditorPanel.Visibility = Visibility.Visible;
            TitleBox.Text = note.Title;
            BodyBox.Text = note.Body;
            RefreshChecklist();
            RefreshAttachments();
        }
        else
        {
            EmptyHint.Visibility = Visibility.Visible;
            EditorPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void SelectNote(Guid id) =>
        NotesList.SelectedItem = NotesList.Items.OfType<Note>()
            .FirstOrDefault(n => n.Id == id);

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var note = _store.Add(string.Empty, string.Empty);
        RefreshList();
        SelectNote(note.Id);
        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e) =>
        TitleWatermark.Visibility = string.IsNullOrEmpty(TitleBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (NotesList.SelectedItem is Note note)
        {
            _store.Delete(note.Id);
            NotesList.SelectedItem = null;
            RefreshList();
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (NotesList.SelectedItem is Note note)
        {
            note.Title = TitleBox.Text;
            note.Body = BodyBox.Text;
            _store.Update(note);
            RefreshList();
            SelectNote(note.Id);
        }
    }

    private void RefreshChecklist()
    {
        if (NotesList.SelectedItem is Note note)
            ChecklistBox.ItemsSource = _store.GetChecklist(note.Id);
        else
            ChecklistBox.ItemsSource = null;
    }

    private void AddChecklistItem_Click(object sender, RoutedEventArgs e)
    {
        if (NotesList.SelectedItem is Note note && !string.IsNullOrWhiteSpace(NewItemBox.Text))
        {
            _store.AddChecklistItem(note.Id, NewItemBox.Text.Trim());
            NewItemBox.Clear();
            RefreshChecklist();
            RefreshList();
        }
    }

    private void ChecklistItem_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box && box.Tag is Guid itemId
            && NotesList.SelectedItem is Note note)
        {
            var item = _store.GetChecklist(note.Id).FirstOrDefault(i => i.Id == itemId);
            if (item is not null)
            {
                item.IsChecked = box.IsChecked == true;
                _store.UpdateChecklistItem(item);
                RefreshList();
            }
        }
    }

    private void DeleteChecklistItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is Guid itemId)
        {
            _store.DeleteChecklistItem(itemId);
            RefreshChecklist();
            RefreshList();
        }
    }

    private void BodyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PreviewCheck.IsChecked == true)
            RenderPreview();
    }

    private void PreviewCheck_Toggled(object sender, RoutedEventArgs e)
    {
        bool preview = PreviewCheck.IsChecked == true;
        BodyBox.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        PreviewScroll.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        if (preview)
            RenderPreview();
    }

    private void RenderPreview()
    {
        PreviewBlock.Inlines.Clear();
        foreach (var seg in TextMarkup.Parse(BodyBox.Text))
        {
            PreviewBlock.Inlines.Add(new System.Windows.Documents.Run(seg.Text)
            {
                FontWeight = seg.Bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = seg.Italic ? FontStyles.Italic : FontStyles.Normal,
            });
        }
    }

    private void RefreshAttachments()
    {
        if (NotesList.SelectedItem is Note note)
            AttachmentsBox.ItemsSource = _store.GetAttachments(note.Id);
        else
            AttachmentsBox.ItemsSource = null;
    }

    private void AddAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (NotesList.SelectedItem is not Note note)
            return;
        var dlg = new Microsoft.Win32.OpenFileDialog();
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            _store.AddAttachment(note.Id, dlg.FileName);
            RefreshAttachments();
            RefreshList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось добавить вложение",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is Guid attId
            && NotesList.SelectedItem is Note note)
        {
            var att = _store.GetAttachments(note.Id).FirstOrDefault(a => a.Id == attId);
            if (att is null)
                return;
            var path = System.IO.Path.Combine(_store.FilesDirectory, att.StoredName);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Не удалось открыть вложение",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void DeleteAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is Guid attId)
        {
            _store.DeleteAttachment(attId);
            RefreshAttachments();
            RefreshList();
        }
    }
}
