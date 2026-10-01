using System.Windows;
using System.Windows.Controls;
using SyncNote.Core;

namespace SyncNote.Windows;

public partial class MainWindow : Window
{
    public const int SyncPort = 48211;

    // Путь к БД: для UI-тестов можно переопределить через env SYNCNOTE_DB_PATH
    // (GetFolderPath(LOCALAPPDATA) игнорирует env LOCALAPPDATA, поэтому
    // изолировать базу через него нельзя — проверено падением F.6 тестов).
    private static string DbPath() =>
        Environment.GetEnvironmentVariable("SYNCNOTE_DB_PATH") is string p &&
        !string.IsNullOrWhiteSpace(p) ? p : SqliteNoteStore.DefaultPath;

    private readonly SqliteNoteStore _store = new(DbPath());
    private readonly PairingService _pairing = PairingService.Open(DbPath());
    private readonly SyncServer _server;
    private readonly CancellationTokenSource _serverCts = new();
    private readonly Task _serverTask;
    private CancellationTokenSource? _syncCts;
    private readonly WifiDirectGroup _p2pGroup = new();
    private BluetoothServer? _btServer;
    private readonly FilesViewModel _filesVm;

    public MainWindow()
    {
        InitializeComponent();
        // Сервер синхронизации работает всегда, пока открыто приложение:
        // телефон пушит изменения сам, кнопки не нужны.
        _server = new SyncServer(_store, _pairing, SyncPort);
        _serverTask = _server.RunAsync(_serverCts.Token);
        // Локальный клиент — тот же пользователь: доверяем себе для петли.
        _pairing.TrustDevice(_store.DeviceId, Environment.MachineName);
        ConnectionStatus.Text = $"Принимаю подключения: {SyncPort}";
        AppVersionText.Text =
            "v" + (System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString(3) ?? "?");
        SyncDeviceBox.Text = Environment.MachineName;
        PairServerInfo.Text = $"Принимаю подключения: 127.0.0.1:{SyncPort}";
        P2pInfo.Text = _p2pGroup.Status;
        // Вкладка «Файлы» (этап F.6): DataContext + флаг.
        _filesVm = new FilesViewModel(_store);
        FilesTab.DataContext = _filesVm;
        ApplyFilesFlag();
        MainTabs.SelectionChanged += (_, e) =>
        {
            // SelectionChanged всплывает от вложенных ListBox (выбор файла
            // тоже прилетает сюда): реагируем только на сам TabControl,
            // иначе Refresh() зацикливается в StackOverflow.
            if (!ReferenceEquals(e.OriginalSource, MainTabs))
                return;
            if (MainTabs.SelectedItem == FilesTab && FeatureFlags.EnableSeparateFiles)
                _filesVm.Refresh();
        };
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
        // QR для сопряжения генерируем сразу, чтобы вкладка «Настройки» была готова.
        IssuePairToken();
        // Подтягиваем изменения с телефона без нажатий: тихое обновление списка.
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        timer.Tick += (_, _) => RefreshList();
        timer.Start();
    }

    private bool _suppressSelection;
    private bool _updatingSelectAll;
    private bool _loadingEditor;
    private void SetDirty(bool v)
    {
        SaveButton.IsEnabled = v;
        DirtyHint.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
    }
    private List<SelectableNote> _notesWrap = new();
    private int _knownTrustedCount = -1;

    private sealed class SelectableNote(
        Note note, bool selected, bool hasChecklist, bool hasAttachments,
        bool isConflict, bool isModified) : System.ComponentModel.INotifyPropertyChanged
    {
        public Note Note { get; } = note;
        // Только для отображения в списке (бейджи/индикаторы), логика не меняется.
        public bool HasChecklist { get; } = hasChecklist;
        public bool HasAttachments { get; } = hasAttachments;
        public bool IsConflict { get; } = isConflict;
        public bool IsModified { get; } = isModified;
        public string SyncBadgeText =>
            IsConflict ? "⚠ конфликт" : IsModified ? "● изменено" : "✓ синхр.";
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
        var selectedId = CurrentNote()?.Id;
        var notes = _store.Search(SearchBox.Text);
        var checkedIds = _notesWrap.Where(s => s.IsSelected).Select(s => s.Note.Id).ToHashSet();
        var pairs = Conflicts.FindPairs(_store);
        var conflictIds = pairs.SelectMany(p => new[] { p.Original.Id, p.Copy.Id }).ToHashSet();
        _notesWrap = notes.Select(n => new SelectableNote(n, checkedIds.Contains(n.Id),
            _store.GetChecklist(n.Id).Count > 0,
            _store.GetAttachments(n.Id).Count > 0,
            conflictIds.Contains(n.Id),
            n.Rev > _store.GetSyncRev(n.Id))).ToList();
        foreach (var s in _notesWrap)
            s.PropertyChanged += (_, _) => UpdateCheckedButton();
        var keep = selectedId is not null
            ? _notesWrap.FirstOrDefault(s => s.Note.Id == selectedId)
            : null;
        // Подавляем событие, только если выбор сохранён; удаление с другой
        // стороны показываем честно (пустое состояние).
        _suppressSelection = keep is not null;
        try
        {
            NotesList.ItemsSource = _notesWrap;
            NotesList.SelectedItem = keep; // null, если удалена с другой стороны
        }
        finally
        {
            _suppressSelection = false;
        }
        int conflicts = pairs.Count;
        ConflictsButton.Content = $"Конфликты ({conflicts})";
        ConflictsButton.Visibility = conflicts > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCheckedButton();
        RefreshSettings();
    }

    private Note? CurrentNote() =>
        (NotesList.SelectedItem as SelectableNote)?.Note;

    private void SelectNote(Guid id) =>
        NotesList.SelectedItem = _notesWrap.FirstOrDefault(s => s.Note.Id == id);

    private void UpdateCheckedButton()
    {
        int count = _notesWrap.Count(s => s.IsSelected);
        DeleteCheckedButton.Content = count == 0
            ? "Удалить выбранные"
            : $"Удалить выбранные ({count})";
        _updatingSelectAll = true;
        try
        {
            if (_notesWrap.Count == 0)
                SelectAllNotesBox.IsChecked = false;
            else if (count == _notesWrap.Count)
                SelectAllNotesBox.IsChecked = true;
            else if (SelectAllNotesBox.IsChecked == true)
                SelectAllNotesBox.IsChecked = false;
        }
        finally
        {
            _updatingSelectAll = false;
        }
    }

    private void SelectAllNotesBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingSelectAll)
            return;
        bool v = SelectAllNotesBox.IsChecked == true;
        foreach (var s in _notesWrap)
            s.IsSelected = v;
        UpdateCheckedButton();
    }

    private void DeleteChecked_Click(object sender, RoutedEventArgs e)
    {
        var ids = _notesWrap.Where(s => s.IsSelected).Select(s => s.Note.Id).ToList();
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
        NotesList.SelectedItem = null;
        RefreshList();
    }

    private void ApplyFilesFlag()
    {
        bool on = FeatureFlags.EnableSeparateFiles;
        FilesContent.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        FilesDisabledHint.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on)
            _filesVm.Refresh();
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
        try { dbBytes = new System.IO.FileInfo(DbPath()).Length; } catch { }
        // Метрики раскладываем по отдельным полям карточки (те же данные, другой вид).
        StatNotesValue.Text = notes.Count.ToString();
        StatFilesValue.Text = $"{filesCount} ({filesBytes / 1024} КБ)";
        StatDbValue.Text = $"{dbBytes / 1024} КБ";
        StatDeviceValue.Text = $"{_store.DeviceId[..Math.Min(8, _store.DeviceId.Length)]}…";
        StatPortValue.Text = $"Порт {SyncPort} (принимаю подключения)";
        DbPathText.Text = DbPath();
        TrustedBox.ItemsSource = _pairing.ListTrusted();
        // Новое сопряжение завершено — возвращаемся к заметкам.
        int trusted = _pairing.ListTrusted().Count;
        if (_knownTrustedCount >= 0 && trusted > _knownTrustedCount)
        {
            ConnectionStatus.Text = "Устройство сопряжено.";
            MainTabs.SelectedIndex = 0;
        }
        _knownTrustedCount = trusted;
    }

    private void DbPath_Click(object sender, RoutedEventArgs e)
    {
        // Открыть папку с базой в проводнике (только просмотр пути).
        try
        {
            var dir = System.IO.Path.GetDirectoryName(DbPath());
            if (!string.IsNullOrEmpty(dir))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true,
                });
        }
        catch { }
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
            // LED-индикатор — чисто визуальное отображение того же текста.
            SyncLed.Fill = text.StartsWith("Готово")
                ? SyncOkBrush
                : text.StartsWith("Ошибка")
                    ? SyncErrBrush
                    : text.Contains('…') ? SyncBusyBrush : SyncIdleBrush;
            StatusLedBar.Fill = SyncLed.Fill;
        });
    }

    private static readonly System.Windows.Media.SolidColorBrush SyncOkBrush =
        new(System.Windows.Media.Color.FromRgb(0x2E, 0x9E, 0x4F));
    private static readonly System.Windows.Media.SolidColorBrush SyncBusyBrush =
        new(System.Windows.Media.Color.FromRgb(0xE8, 0xA3, 0x17));
    private static readonly System.Windows.Media.SolidColorBrush SyncErrBrush =
        new(System.Windows.Media.Color.FromRgb(0xD6, 0x3B, 0x3B));
    private static readonly System.Windows.Media.SolidColorBrush SyncIdleBrush =
        new(System.Windows.Media.Color.FromRgb(0x9A, 0x9A, 0xA2));

    private async void SyncGoButton_Click(object sender, RoutedEventArgs e)
    {
        // Пустые поля — значения по умолчанию, а не ошибка.
        if (string.IsNullOrWhiteSpace(SyncHostBox.Text))
            SyncHostBox.Text = "127.0.0.1";
        if (!int.TryParse(SyncPortBox.Text, out int port) || port is < 1 or > 65535)
        {
            if (string.IsNullOrWhiteSpace(SyncPortBox.Text))
            {
                SyncPortBox.Text = SyncPort.ToString();
                port = SyncPort;
            }
            else
            {
                SetSyncState("Ошибка: некорректный порт.");
                return;
            }
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
            if (FeatureFlags.EnableSeparateFiles)
                _filesVm.Refresh();
            MainTabs.SelectedIndex = 0;
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
        if (CurrentNote() is Note note)
        {
            EmptyHint.Visibility = Visibility.Collapsed;
            EditorPanel.Visibility = Visibility.Visible;
            _loadingEditor = true;
            try
            {
                TitleBox.Text = note.Title;
                BodyBox.Text = note.Body;
            }
            finally
            {
                _loadingEditor = false;
            }
            RefreshChecklist();
            RefreshAttachments();
            SetDirty(false);
        }
        else
        {
            EmptyHint.Visibility = Visibility.Visible;
            EditorPanel.Visibility = Visibility.Collapsed;
            SetDirty(false);
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var note = _store.Add(string.Empty, string.Empty);
        RefreshList();
        SelectNote(note.Id);
        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        TitleWatermark.Visibility = string.IsNullOrEmpty(TitleBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        if (!_loadingEditor)
            SetDirty(true);
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentNote() is Note note)
        {
            _store.Delete(note.Id);
            NotesList.SelectedItem = null;
            RefreshList();
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentNote() is Note note)
        {
            note.Title = TitleBox.Text;
            note.Body = BodyBox.Text;
            _store.Update(note);
            RefreshList();
            SelectNote(note.Id);
            SetDirty(false);
        }
    }

    private void RefreshChecklist()
    {
        if (CurrentNote() is Note note)
            ChecklistBox.ItemsSource = _store.GetChecklist(note.Id);
        else
            ChecklistBox.ItemsSource = null;
    }

    private void AddChecklistItem_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentNote() is Note note && !string.IsNullOrWhiteSpace(NewItemBox.Text))
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
            && CurrentNote() is Note note)
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
        if (!_loadingEditor)
            SetDirty(true);
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
        if (CurrentNote() is Note note)
            AttachmentsBox.ItemsSource = _store.GetAttachments(note.Id);
        else
            AttachmentsBox.ItemsSource = null;
    }

    private void AddAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentNote() is not Note note)
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
            && CurrentNote() is Note note)
        {
            var att = _store.GetAttachments(note.Id).FirstOrDefault(a => a.Id == attId);
            if (att is null)
                return;
            var stored = System.IO.Path.Combine(_store.FilesDirectory, att.StoredName);
            // Хранилище content-addressed (имя == sha, без расширения) —
            // для открытия ОС копируем во временный файл с настоящим именем.
            var openDir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "syncnote-open");
            System.IO.Directory.CreateDirectory(openDir);
            var path = System.IO.Path.Combine(
                openDir, att.Sha256 + "_" + AttachmentIo.SanitizeFileName(att.FileName));
            try
            {
                var srcInfo = new System.IO.FileInfo(stored);
                var dstInfo = new System.IO.FileInfo(path);
                if (!dstInfo.Exists || dstInfo.Length != srcInfo.Length)
                    System.IO.File.Copy(stored, path, overwrite: true);
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

    private void DownloadAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is Guid attId
            && CurrentNote() is Note note)
        {
            var att = _store.GetAttachments(note.Id).FirstOrDefault(a => a.Id == attId);
            if (att is null)
                return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = att.FileName,
            };
            if (dlg.ShowDialog() != true)
                return;
            try
            {
                System.IO.File.Copy(
                    System.IO.Path.Combine(_store.FilesDirectory, att.StoredName),
                    dlg.FileName, overwrite: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Не удалось скачать вложение",
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
