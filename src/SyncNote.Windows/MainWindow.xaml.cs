using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SyncNote.Core;
using SyncNote.Core.Interfaces;
using SyncNote.Core.Models;
using SyncNote.Core.Services;
using SyncNote.Windows.Services;
using SyncNote.Windows.ViewModels;
using SyncNote.Windows.Views;

namespace SyncNote.Windows;

public partial class MainWindow : Window
{
    private readonly string _dbPath;
    private readonly string _deviceId;
    private readonly SqliteNoteRepo _notes;
    private readonly SqliteFileRepo _files;
    private readonly SqlSyncStateStore _sync;
    private readonly WindowsFileIo _fileIo;
    private readonly MainViewModel _vm;
    private readonly CancellationTokenSource _cts = new();

    public MainWindow()
    {
        InitializeComponent();

        // v2 живёт рядом с v1: отдельный каталог, иначе v1-база
        // (schema_version=7) блокирует применение v2-схемы.
        _dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncNote_v2", "notes.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        new WindowsDbInitializer().Initialize(_dbPath);
        _deviceId = Environment.MachineName;

        _notes = new SqliteNoteRepo(_dbPath, Environment.MachineName);
        _fileIo = new WindowsFileIo(Path.Combine(Path.GetDirectoryName(_dbPath)!, "files"));
        _files = new SqliteFileRepo(_dbPath, Environment.MachineName, new WindowsFileIo(Path.Combine(Path.GetDirectoryName(_dbPath)!, "files")));
        _sync = new SqlSyncStateStore(_dbPath);

        _vm = new MainViewModel(_notes, _files, _sync, this);
        _vm.ShowPairing += () => ShowPairingWindow();
        DataContext = _vm;

        Closing += (_, e) =>
        {
            _cts.Cancel();
            _vm.Dispose();
        };

        _ = AutoSyncLoop();
    }

    private void ShowPairingWindow()
    {
        var pw = new PairingWindow(_dbPath, Environment.MachineName);
        pw.Owner = this;
        pw.ShowDialog();
        // После закрытия окна сопряжения обновляем списки (могли прилететь новые)
        _vm.RefreshAll();
    }

    private async Task AutoSyncLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), _cts.Token);
            if (!_cts.IsCancellationRequested)
            {
                try
                {
                    // Авто-синк с доверенными устройствами (пока заглушка)
                    // TODO: Реализовать авто-синк с доверенными устройствами
                }
                catch { }
            }
        }
    }
}

public class MainViewModel : IDisposable, INotifyPropertyChanged
{
    private readonly INoteRepo _notes;
    private readonly IFileRepo _files;
    private readonly ISyncStateStore _sync;
    private readonly Window _owner;
    private readonly List<SelectableNote> _allNotes = new();
    private readonly List<SelectableFile> _allFiles = new();
    private string _searchQuery = "";
    private SelectableNote? _selectedNote;
    private SelectableFile? _selectedFile;
    private bool _showNotes = true;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnProp([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));

    public event Action? ShowPairing;

    public ICommand AddNoteCommand { get; }
    public ICommand SaveNoteCommand { get; }
    public ICommand DeleteNoteCommand { get; }
    public ICommand DeleteFileCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ShowPairingCommand { get; }
    public ICommand ExitCommand { get; }

    public IReadOnlyList<SelectableNote> Notes => _allNotes;
    public IReadOnlyList<SelectableFile> Files => _allFiles;

    public bool ShowNotes
    {
        get => _showNotes;
        set
        {
            if (_showNotes == value) return;
            _showNotes = value;
            OnProp();
            OnProp(nameof(ShowFiles));
            OnProp(nameof(NotesVisible));
            OnProp(nameof(FilesVisible));
            OnProp(nameof(HasItems));
        }
    }

    public bool ShowFiles
    {
        get => !_showNotes;
        set
        {
            if (!_showNotes == value) return;
            _showNotes = !value;
            OnProp();
            OnProp(nameof(ShowNotes));
            OnProp(nameof(NotesVisible));
            OnProp(nameof(FilesVisible));
            OnProp(nameof(HasItems));
        }
    }

    public Visibility NotesVisible => _showNotes ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FilesVisible => _showNotes ? Visibility.Collapsed : Visibility.Visible;

    public bool HasItems => _showNotes
        ? _allNotes.Any(n => n.IsVisible)
        : _allFiles.Any(f => f.IsVisible);

    public SelectableNote? SelectedNote
    {
        get => _selectedNote;
        set { _selectedNote = value; OnProp(); OnProp(nameof(HasSelection)); }
    }

    public SelectableFile? SelectedFile
    {
        get => _selectedFile;
        set { _selectedFile = value; OnProp(); OnProp(nameof(HasFileSelection)); }
    }

    public bool HasSelection => _selectedNote != null;
    public bool HasFileSelection => _selectedFile != null;

    public string SearchQuery
    {
        get => "";
        set
        {
            var q = value.Trim().ToLowerInvariant();
            foreach (var n in _allNotes)
                n.IsVisible = string.IsNullOrEmpty(q) || n.Note.Title.ToLower().Contains(q);
            foreach (var f in _allFiles)
                f.IsVisible = string.IsNullOrEmpty(q) || f.File.Name.ToLower().Contains(q);
            OnProp(nameof(HasItems));
        }
    }

    public MainViewModel(INoteRepo notes, IFileRepo files, ISyncStateStore sync, Window owner)
    {
        _notes = notes;
        _files = files;
        _sync = sync;
        _owner = owner;

        AddNoteCommand = new RelayCommand(_ => AddNote());
        SaveNoteCommand = new RelayCommand(_ => SaveSelected(), _ => HasSelection);
        DeleteNoteCommand = new RelayCommand(_ => DeleteSelected(), _ => HasSelection);
        DeleteFileCommand = new RelayCommand(_ => DeleteSelectedFile(), _ => HasFileSelection);
        OpenSettingsCommand = new RelayCommand(_ => { /* TODO: Settings window */ });
        ShowPairingCommand = new RelayCommand(_ => ShowPairing?.Invoke());
        ExitCommand = new RelayCommand(_ => Application.Current.Shutdown());

        RefreshAll();
    }

    public string AddNote()
    {
        var id = Guid.NewGuid().ToString("N");
        var note = new NoteEntry
        {
            Id = id,
            Title = "Новая заметка",
            Body = "",
            Rev = 1,
            UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            AuthorDeviceId = Environment.MachineName,
            IsDeleted = false
        };
        _notes.CreateAsync(note).Wait();
        RefreshAll();
        SelectedNote = _allNotes.FirstOrDefault(n => n.Note.Id == id);
        return id;
    }

    public void SaveSelected()
    {
        if (_selectedNote == null) return;
        _notes.UpdateAsync(_selectedNote.Note).Wait();
        RefreshAll();
    }

    public void DeleteNote(string id)
    {
        _notes.SoftDeleteAsync(id).Wait();
        if (_selectedNote?.Note.Id == id) SelectedNote = null;
        RefreshAll();
    }

    public void DeleteSelected()
    {
        if (_selectedNote != null)
            DeleteNote(_selectedNote.Note.Id);
    }

    public void DeleteSelectedFile()
    {
        if (_selectedFile == null) return;
        _files.SoftDeleteAsync(_selectedFile.File.Id).Wait();
        SelectedFile = null;
        RefreshAll();
    }

    public void RefreshAll()
    {
        var noteId = _selectedNote?.Note.Id;
        var fileId = _selectedFile?.File.Id;
        _allNotes.Clear();
        foreach (var n in _notes.GetAllAsync(false).Result)
        {
            var sn = new SelectableNote(n)
            {
                IsSynced = n.Rev <= _sync.GetSyncRev("note", n.Id)
            };
            _allNotes.Add(sn);
        }
        _allFiles.Clear();
        foreach (var f in _files.GetAllAsync(false).Result)
        {
            var sf = new SelectableFile(f)
            {
                IsSynced = f.Rev <= _sync.GetSyncRev("file", f.Id)
            };
            _allFiles.Add(sf);
        }
        SelectedNote = _allNotes.FirstOrDefault(n => n.Note.Id == noteId);
        SelectedFile = _allFiles.FirstOrDefault(f => f.File.Id == fileId);
        OnProp(nameof(Notes));
        OnProp(nameof(Files));
        OnProp(nameof(HasItems));
    }

    // Совместимость со старым вызовом после сопряжения.
    public void RefreshNotes() => RefreshAll();

    public void Dispose() { }
}

public sealed class SelectableNote : INotifyPropertyChanged
{
    public NoteEntry Note { get; }
    public bool IsVisible { get; set; } = true;
    public bool IsSynced { get; set; } = true;
    public string Badge => IsSynced ? "✓" : "●";
    private bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    public SelectableNote(NoteEntry n) => Note = n;
}

public sealed class SelectableFile : INotifyPropertyChanged
{
    public FileEntry File { get; }
    public bool IsVisible { get; set; } = true;
    public bool IsSynced { get; set; } = true;
    public string Badge => IsSynced ? "✓" : "●";
    public string SizeText
    {
        get
        {
            long b = File.SizeBytes;
            if (b < 1024) return $"{b} Б";
            double kb = b / 1024.0;
            if (kb < 1024) return $"{kb:F1} КБ";
            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:F1} МБ";
            return $"{mb / 1024.0:F2} ГБ";
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    public SelectableFile(FileEntry f) => File = f;
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }
    public bool CanExecute(object? p) => canExecute?.Invoke(p) ?? true;
    public void Execute(object? p) => execute(p);
    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}
