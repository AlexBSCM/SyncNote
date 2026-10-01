using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SyncNote.Core.Models;

namespace SyncNote.Windows;

// ViewModel одной строки списка заметок. Только чтение + UI-свойства.
public sealed class NoteEntryViewModel(NoteEntry entry, long syncRev) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id => entry.Id;
    public string Title { get => entry.Title; set => entry.Title = value; }
    public string Body { get => entry.Body; set => entry.Body = value; }
    public long Rev => entry.Rev;
    public string UpdatedAt => entry.UpdatedAt;
    public string AuthorDeviceId => entry.AuthorDeviceId;
    public bool IsDeleted => entry.IsDeleted;
    internal NoteEntry Entry => entry;

    public bool HasChecklist { get; set; }
    public bool HasAttachments { get; set; }

    public string FormattedDate => DateTime.TryParse(entry.UpdatedAt, out var dt)
        ? dt.ToString("yyyy-MM-dd HH:mm")
        : entry.UpdatedAt;

    // Бейдж статуса синхронизации.
    public bool IsConflict => entry.Title.EndsWith(" (конфликт)");
    public bool IsModified => !IsConflict && entry.Rev > syncRev;

    public string SyncBadgeText =>
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

    private void OnProp([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new(n));
}

public sealed class SelectableNote : INotifyPropertyChanged
{
    public NoteEntryViewModel Note { get; }
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
    private void OnProp([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new(n));

    public SelectableNote(NoteEntryViewModel n) => Note = n;
}

// ViewModel одной строки списка файлов. Только чтение + UI-свойства.
public sealed class FileEntryViewModel(FileEntry entry, long syncRev) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id => entry.Id;
    public string Name => entry.Name;
    public string Mime => entry.Mime;
    public long SizeBytes => entry.SizeBytes;
    public string Sha256 => entry.Sha256;
    public long Rev => entry.Rev;
    public string UpdatedAt => entry.UpdatedAt;
    public string AuthorDeviceId => entry.AuthorDeviceId;
    public bool IsDeleted => entry.IsDeleted;

    public bool IsVisible { get; set; } = true;

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
        $"{FormattedSize} • {FormattedDate}";

    public string FormattedSize => entry.SizeBytes switch
    {
        < 1024 => $"{entry.SizeBytes} Б",
        < 1024 * 1024 => $"{entry.SizeBytes / 1024.0:F1} КБ",
        _ => $"{entry.SizeBytes / (1024.0 * 1024):F1} МБ",
    };

    public string FormattedDate => DateTime.TryParse(entry.UpdatedAt, out var dt)
        ? dt.ToString("yyyy-MM-dd HH:mm")
        : entry.UpdatedAt;

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

    private void OnProp([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new(n));
}
