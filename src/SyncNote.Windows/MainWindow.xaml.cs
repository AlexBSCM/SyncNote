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

        _vm = new MainViewModel(_notes, _files, _sync,
            new SqliteAttachmentRepo(_dbPath, _deviceId), _fileIo, this);
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
    private readonly SqliteAttachmentRepo _attachments;
    private readonly IFileIo _fileIo;
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
    public ICommand FormatBoldCommand { get; }
    public ICommand FormatItalicCommand { get; }
    public ICommand FormatListCommand { get; }
    public ICommand AttachFileCommand { get; }
    public ICommand SetPreviewCommand { get; }
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
        set { _selectedNote = value; OnProp(); OnProp(nameof(HasSelection)); CommandManager.InvalidateRequerySuggested(); }
    }

    public SelectableFile? SelectedFile
    {
        get => _selectedFile;
        set { _selectedFile = value; OnProp(); OnProp(nameof(HasFileSelection)); CommandManager.InvalidateRequerySuggested(); }
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

    public MainViewModel(INoteRepo notes, IFileRepo files, ISyncStateStore sync,
        SqliteAttachmentRepo attachments, IFileIo fileIo, Window owner)
    {
        _notes = notes;
        _files = files;
        _sync = sync;
        _attachments = attachments;
        _fileIo = fileIo;
        _owner = owner;

        AddNoteCommand = new RelayCommand(_ => AddNote());
        SaveNoteCommand = new RelayCommand(_ => SaveSelected(), _ => HasSelection);
        DeleteNoteCommand = new RelayCommand(_ => DeleteSelected(), _ => HasSelection);
        DeleteFileCommand = new RelayCommand(_ => DeleteSelectedFile(), _ => HasFileSelection);
        FormatBoldCommand = new RelayCommand(p => WrapSelection(p, "**"), _ => HasSelection);
        FormatItalicCommand = new RelayCommand(p => WrapSelection(p, "*"), _ => HasSelection);
        FormatListCommand = new RelayCommand(_ => AppendChecklistItem(), _ => HasSelection);
        AttachFileCommand = new RelayCommand(_ => AttachFile(), _ => HasSelection);
        SetPreviewCommand = new RelayCommand(p => SetPreview(p), _ => HasSelection);
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

    // --- Markdown-редактор ---

    private static System.Windows.Controls.TextBox? BoxOf(object? p)
        => p as System.Windows.Controls.TextBox;

    private void WrapSelection(object? p, string marker)
    {
        var box = BoxOf(p);
        if (_selectedNote == null) return;
        if (box == null || string.IsNullOrEmpty(box.SelectedText))
        {
            _selectedNote.Note.Body += marker + marker;
        }
        else
        {
            int start = box.SelectionStart;
            string sel = box.SelectedText;
            _selectedNote.Note.Body = _selectedNote.Note.Body.Remove(start, sel.Length)
                .Insert(start, marker + sel + marker);
        }
        OnProp(nameof(SelectedNote));
    }

    private void AppendChecklistItem()
    {
        if (_selectedNote == null) return;
        if (_selectedNote.Note.Body.Length > 0
            && !_selectedNote.Note.Body.EndsWith("\n"))
            _selectedNote.Note.Body += "\n";
        _selectedNote.Note.Body += "- [ ] ";
        OnProp(nameof(SelectedNote));
    }

    private void SetPreview(object? p)
    {
        if (_selectedNote == null) return;
        bool want = p is string s ? s == "1" : p is true;
        if (_selectedNote.IsPreview == want) return;
        _selectedNote.IsPreview = want;
        if (want)
        {
            _selectedNote.PreviewDocument = MarkdownPreview.ToFlowDocument(
                _selectedNote.Note.Body, OpenAttachmentLink);
        }
    }

    private void OpenAttachmentLink(string url)
    {
        try
        {
            if (!url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                return;
            string sha = url.Substring("file://".Length);
            string path = _fileIo.GetStoragePath(sha);
            if (!File.Exists(path)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch { }
    }

    private async void AttachFile()
    {
        if (_selectedNote == null) return;
        try
        {
            var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "syncnote-attach.log");
            System.IO.File.AppendAllText(log, $"[{DateTime.Now}] AttachFile called\n");
            
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Прикрепить файл к заметке"
            };
            System.IO.File.AppendAllText(log, $"[{DateTime.Now}] ShowDialog called\n");
            if (dlg.ShowDialog() != true) 
            {
                System.IO.File.AppendAllText(log, $"[{DateTime.Now}] ShowDialog returned false\n");
                return; 
            }
            System.IO.File.AppendAllText(log, $"[{DateTime.Now}] File selected: {dlg.FileName}\n");
            
            string name = Path.GetFileName(dlg.FileName);
            var (sha, size) = await _fileIo.ImportAsync(dlg.FileName);
            System.IO.File.AppendAllText(log, $"[{DateTime.Now}] Imported: sha={sha} size={size}\n");
            
            await _attachments.InsertAsync(
                _selectedNote.Note.Id, name, MimeOf(name), size, sha);
            System.IO.File.AppendAllText(log, $"[{DateTime.Now}] Inserted to DB\n");
            
            if (_selectedNote.Note.Body.Length > 0
                && !_selectedNote.Note.Body.EndsWith("\n"))
                _selectedNote.Note.Body += "\n";
            _selectedNote.Note.Body += $"[📎 {name}](file://{sha})";
            await _notes.UpdateAsync(_selectedNote.Note);
            System.IO.File.AppendAllText(log, $"[{DateTime.Now}] Note updated\n");
            
            RefreshAll();
            System.IO.File.AppendAllText(log, $"[{DateTime.Now}] Refreshed\n");
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "syncnote-attach.log"),
                $"[{DateTime.Now}] ERROR: {ex}\n");
            throw;
        }
    }

    private static string MimeOf(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" => "image/" + ext.TrimStart('.'),
            ".pdf" => "application/pdf",
            ".txt" or ".md" or ".log" => "text/plain",
            ".mp3" or ".wav" or ".ogg" => "audio/" + ext.TrimStart('.'),
            ".mp4" or ".webm" or ".mkv" or ".avi" => "video/" + ext.TrimStart('.'),
            ".zip" => "application/zip",
            ".json" => "application/json",
            _ => "application/octet-stream",
        };
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
    private bool _isPreview;
    public bool IsPreview
    {
        get => _isPreview;
        set
        {
            _isPreview = value;
            PropertyChanged?.Invoke(this, new(nameof(IsPreview)));
            PropertyChanged?.Invoke(this, new(nameof(IsEditing)));
        }
    }
    public bool IsEditing => !IsPreview;
    private System.Windows.Documents.FlowDocument? _preview;
    public System.Windows.Documents.FlowDocument? PreviewDocument
    {
        get => _preview;
        set { _preview = value; PropertyChanged?.Invoke(this, new(nameof(PreviewDocument))); }
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
