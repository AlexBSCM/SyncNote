using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
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

        _dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncNote", "notes.db");
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
        // После закрытия окна сопряжения обновляем список заметок (могли прилететь новые)
        _vm.RefreshNotes();
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

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _vm.SearchQuery = SearchBox.Text;
    }

    private void NotesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _vm.SelectedNote = (sender as ListBox)?.SelectedItem as SelectableNote;
    }

    private void AddNote_Click(object sender, RoutedEventArgs e)
    {
        var newId = _vm.AddNote();
        NotesList.SelectedItem = _vm.Notes.FirstOrDefault(n => n.Note.Id == newId);
    }

    private void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedNote != null)
            _vm.DeleteNote(_vm.SelectedNote.Note.Id);
    }
}

public class MainViewModel : IDisposable, INotifyPropertyChanged
{
    private readonly INoteRepo _notes;
    private readonly IFileRepo _files;
    private readonly ISyncStateStore _sync;
    private readonly Window _owner;
    private readonly List<SelectableNote> _allNotes = new();
    private string _searchQuery = "";
    private SelectableNote? _selectedNote;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnProp([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));

    public event Action? ShowPairing;

    public ICommand AddNoteCommand { get; }
    public ICommand DeleteNoteCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ShowPairingCommand { get; }
    public ICommand ExitCommand { get; }

    public IReadOnlyList<SelectableNote> Notes => _allNotes;
    public SelectableNote? SelectedNote
    {
        get => _selectedNote;
        set { _selectedNote = value; OnProp(); OnProp(nameof(HasSelection)); }
    }

    public bool HasSelection => _selectedNote != null;

    public string SearchQuery
    {
        get => "";
        set
        {
            var q = value.Trim().ToLowerInvariant();
            foreach (var n in _allNotes)
                n.IsVisible = string.IsNullOrEmpty(q) || n.Note.Title.ToLower().Contains(q);
        }
    }

    public MainViewModel(INoteRepo notes, IFileRepo files, ISyncStateStore sync, Window owner)
    {
        _notes = notes;
        _files = files;
        _sync = sync;
        _owner = owner;

        AddNoteCommand = new RelayCommand(_ => AddNote());
        DeleteNoteCommand = new RelayCommand(_ => DeleteSelected(), _ => HasSelection);
        OpenSettingsCommand = new RelayCommand(_ => { /* TODO: Settings window */ });
        ShowPairingCommand = new RelayCommand(_ => ShowPairing?.Invoke());
        ExitCommand = new RelayCommand(_ => Application.Current.Shutdown());

        RefreshNotes();
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
        RefreshNotes();
        return id;
    }

    public void DeleteNote(string id)
    {
        _notes.SoftDeleteAsync(id).Wait();
        RefreshNotes();
    }

    public void DeleteSelected()
    {
        if (_selectedNote != null)
            DeleteNote(_selectedNote.Note.Id);
    }

    public void RefreshNotes()
    {
        var selectedId = _selectedNote?.Note.Id;
        _allNotes.Clear();
        foreach (var n in _notes.GetAllAsync(false).Result)
        {
            var sn = new SelectableNote(n);
            if (sn.Note.Id == selectedId) sn.IsSelected = true;
            _allNotes.Add(sn);
        }
        OnProp(nameof(Notes));
    }

    public void Dispose() { }
}

public sealed class SelectableNote : INotifyPropertyChanged
{
    public NoteEntry Note { get; }
    public bool IsVisible { get; set; } = true;
    private bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    public SelectableNote(NoteEntry n) => Note = n;
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? p) => canExecute?.Invoke(p) ?? true;
    public void Execute(object? p) => execute(p);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}