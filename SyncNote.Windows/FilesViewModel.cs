using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using SyncNote.Core;

namespace SyncNote.Windows;

// Мини-RelayCommand: команды для биндинга кнопок без code-behind логики.
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? p) => canExecute?.Invoke(p) ?? true;
    public void Execute(object? p) => execute(p);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

// ViewModel одной строки списка файлов. Только чтение + UI-свойства;
// все операции со store — через FilesViewModel.
public sealed class FileEntryViewModel(FileEntry entry, long syncRev) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id => entry.Id;
    public string Name => entry.Name;
    public string Mime => entry.Mime;
    public long Rev => entry.Rev;

    public string DisplayIcon => Mime switch
    {
        var m when m.StartsWith("image/") => "🖼️",
        var m when m.StartsWith("audio/") => "🎧",
        var m when m.StartsWith("video/") => "🎬",
        "application/pdf" => "📕",
        "application/zip" => "📦",
        var m when m.StartsWith("text/") => "📄",
        _ => "📎",
    };

    public string MetaLine =>
        $"{FormattedSize} • {entry.UpdatedAt:yyyy-MM-dd HH:mm}";

    public string FormattedSize => entry.SizeBytes switch
    {
        < 1024 => $"{entry.SizeBytes} Б",
        < 1024 * 1024 => $"{entry.SizeBytes / 1024.0:F1} КБ",
        _ => $"{entry.SizeBytes / (1024.0 * 1024):F1} МБ",
    };

    public string FormattedDate => entry.UpdatedAt.ToString("yyyy-MM-dd HH:mm");

    // Бейдж статуса синхронизации.
    public bool IsConflict => entry.Name.EndsWith(" (конфликт)");
    public bool IsModified => !IsConflict && entry.Rev > syncRev;

    public string StatusBadgeText =>
        IsConflict ? "⚠ конфликт" :
        IsModified ? "● изменено" : "✓ синхр.";

    public Brush StatusBadgeBrush => (IsConflict, IsModified) switch
    {
        (true, _) => new SolidColorBrush(Color.FromRgb(0xF8, 0xD7, 0xDA)),
        (_, true) => new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xCD)),
        _ => new SolidColorBrush(Color.FromRgb(0xD1, 0xE7, 0xDD)),
    };

    public Brush StatusBadgeFg => (IsConflict, IsModified) switch
    {
        (true, _) => new SolidColorBrush(Color.FromRgb(0x84, 0x29, 0x2B)),
        (_, true) => new SolidColorBrush(Color.FromRgb(0x66, 0x4D, 0x03)),
        _ => new SolidColorBrush(Color.FromRgb(0x0F, 0x51, 0x2E)),
    };

    public string FullInfo =>
        $"Размер: {entry.SizeBytes} байт ({FormattedSize})\n" +
        $"MIME: {entry.Mime}\n" +
        $"SHA256: {entry.Sha256[..Math.Min(16, entry.Sha256.Length)]}...\n" +
        $"Ревизия: #{entry.Rev} (синхр: #{syncRev})\n" +
        $"Автор: {entry.AuthorDeviceId}\n" +
        $"Изменён: {FormattedDate}\n" +
        $"Статус: {StatusBadgeText}";
}

// ViewModel вкладки «Файлы». Единственная точка доступа к IFileStore
// для этого таба; XAML биндится сюда, SQLite/FileIo напрямую не трогаем.
public sealed class FilesViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly IFileStore _store;
    private readonly List<FileEntryViewModel> _all = new();
    private string _searchQuery = string.Empty;
    private FileEntryViewModel? _selectedFile;

    public ObservableCollection<FileEntryViewModel> FilesList { get; } = new();

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery == value)
                return;
            _searchQuery = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchQuery)));
            ApplyFilter();
        }
    }

    public FileEntryViewModel? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (ReferenceEquals(_selectedFile, value))
                return;
            _selectedFile = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedFile)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelection)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewImage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsImagePreview)));
            OpenCommand.RaiseCanExecuteChanged();
            SaveAsCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelection => _selectedFile is not null;

    public bool IsEmpty => FilesList.Count == 0;
    public bool HasFiles => FilesList.Count > 0;

    public RelayCommand LoadCommand { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ToggleSpoilerCommand { get; }

    public FilesViewModel(IFileStore store)
    {
        _store = store;
        LoadCommand = new RelayCommand(_ => Refresh());
        AddCommand = new RelayCommand(_ => Add());
        OpenCommand = new RelayCommand(_ => Open(), _ => HasSelection);
        SaveAsCommand = new RelayCommand(_ => SaveAs(), _ => HasSelection);
        DeleteCommand = new RelayCommand(_ => Delete(), _ => HasSelection);
        ToggleSpoilerCommand = new RelayCommand(_ => IsSpoilerOpen = !IsSpoilerOpen);
        FilesList.CollectionChanged += (_, _) =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEmpty)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasFiles)));
        };
    }

    private bool _spoilerOpen;
    public bool IsSpoilerOpen
    {
        get => _spoilerOpen;
        set
        {
            if (_spoilerOpen == value)
                return;
            _spoilerOpen = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSpoilerOpen)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpoilerHeader)));
        }
    }

    public string SpoilerHeader => IsSpoilerOpen ? "Информация о файле ▼" : "Информация о файле ▲";

    // Превью изображений: thumbnail 400px, ошибки молча гасят в placeholder.
    public System.Windows.Media.Imaging.BitmapImage? PreviewImage
    {
        get
        {
            if (_selectedFile is null || !_selectedFile.Mime.StartsWith("image/"))
                return null;
            try
            {
                var entry = _store.TryGetFile(_selectedFile.Id);
                if (entry is null)
                    return null;
                var img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(System.IO.Path.Combine(_store.FilesDirectory, entry.StoredName));
                img.DecodePixelWidth = 400;
                img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch
            {
                return null;
            }
        }
    }

    public bool IsImagePreview => PreviewImage is not null;

    private IFileStore Store => _store;

    public void Refresh()
    {
        if (!FeatureFlags.EnableSeparateFiles)
            return;
        var selectedId = _selectedFile?.Id;
        _all.Clear();
        foreach (var e in Store.GetFiles(includeDeleted: false))
            _all.Add(new FileEntryViewModel(e, Store.GetFileSyncRev(e.Id)));
        ApplyFilter();
        // Гард в сеттере SelectedFile обрывает пинг-понг TwoWay-биндинга,
        // поэтому присваивание здесь безопасно и завершается за один шаг.
        SelectedFile = selectedId is null
            ? null
            : _all.FirstOrDefault(v => v.Id == selectedId);
    }

    private void ApplyFilter()
    {
        var q = _searchQuery.Trim().ToLowerInvariant();
        FilesList.Clear();
        foreach (var v in _all.Where(v => q.Length == 0 || v.Name.ToLowerInvariant().Contains(q)))
            FilesList.Add(v);
    }

    private void Add()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog();
        if (dlg.ShowDialog() != true)
            return;
        Store.AddFile(dlg.FileName);
        Refresh();
    }

    private void Open()
    {
        if (_selectedFile is null)
            return;
        var entry = Store.TryGetFile(_selectedFile.Id);
        if (entry is null)
            return;
        // Путь собирается из свойств store (прецедент OpenAttachment_Click),
        // сами байты и метаданные — только через IFileStore.
        var path = Path.Combine(Store.FilesDirectory, entry.StoredName);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    private void SaveAs()
    {
        if (_selectedFile is null)
            return;
        var entry = Store.TryGetFile(_selectedFile.Id);
        if (entry is null)
            return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = entry.Name };
        if (dlg.ShowDialog() != true)
            return;
        File.Copy(Path.Combine(Store.FilesDirectory, entry.StoredName), dlg.FileName, overwrite: true);
    }

    private void Delete()
    {
        if (_selectedFile is null)
            return;
        Store.DeleteFile(_selectedFile.Id);
        SelectedFile = null;
        Refresh();
    }
}
