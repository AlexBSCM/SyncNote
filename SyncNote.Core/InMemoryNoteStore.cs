namespace SyncNote.Core;

// Временное хранилище в памяти для этапа UI.
// Заменено на SQLite на этапе хранения без изменения интерфейса.
public sealed class InMemoryNoteStore : INoteStore
{
    private readonly List<Note> _notes = new();
    private readonly List<ChecklistItem> _items = new();
    private readonly List<Attachment> _attachments = new();

    public string FilesDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "SyncNoteMemFiles");

    public IReadOnlyList<Note> List() =>
        _notes.OrderByDescending(n => n.UpdatedAt).ToList();

    public IReadOnlyList<Note> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return List();
        return _notes
            .Where(n => n.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || n.Body.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n.UpdatedAt)
            .ToList();
    }

    public Note Add(string title, string body)
    {
        var note = new Note { Title = title, Body = body, UpdatedAt = DateTime.UtcNow };
        _notes.Add(note);
        return note;
    }

    public void Update(Note note)
    {
        var existing = _notes.FirstOrDefault(n => n.Id == note.Id)
            ?? throw new KeyNotFoundException($"Note {note.Id} not found.");
        existing.Title = note.Title;
        existing.Body = note.Body;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.Rev += 1;
    }

    public bool Delete(Guid id)
    {
        var existing = _notes.FirstOrDefault(n => n.Id == id);
        if (existing is null)
            return false;
        _notes.Remove(existing);
        return true;
    }

    public IReadOnlyList<ChecklistItem> GetChecklist(Guid noteId) =>
        _items.Where(i => i.NoteId == noteId).OrderBy(i => i.Position).ToList();

    public ChecklistItem AddChecklistItem(Guid noteId, string text)
    {
        var item = new ChecklistItem
        {
            NoteId = noteId,
            Position = _items.Where(i => i.NoteId == noteId).Count(),
            Text = text,
        };
        _items.Add(item);
        return item;
    }

    public void UpdateChecklistItem(ChecklistItem item)
    {
        var existing = _items.FirstOrDefault(i => i.Id == item.Id)
            ?? throw new KeyNotFoundException($"Checklist item {item.Id} not found.");
        existing.Text = item.Text;
        existing.IsChecked = item.IsChecked;
        existing.Position = item.Position;
    }

    public bool DeleteChecklistItem(Guid itemId)
    {
        var existing = _items.FirstOrDefault(i => i.Id == itemId);
        if (existing is null)
            return false;
        _items.Remove(existing);
        return true;
    }

    public IReadOnlyList<Attachment> GetAttachments(Guid noteId) =>
        _attachments.Where(a => a.NoteId == noteId).ToList();

    public Attachment AddAttachment(Guid noteId, string sourcePath)
    {
        var att = new Attachment { NoteId = noteId };
        var (storedName, size, sha) = AttachmentIo.CopyIn(FilesDirectory, att.Id, sourcePath);
        att.FileName = Path.GetFileName(sourcePath);
        att.MimeType = AttachmentIo.MimeByExtension(att.FileName);
        att.SizeBytes = size;
        att.Sha256 = sha;
        att.StoredName = storedName;
        _attachments.Add(att);
        return att;
    }

    public bool DeleteAttachment(Guid attachmentId)
    {
        var existing = _attachments.FirstOrDefault(a => a.Id == attachmentId);
        if (existing is null)
            return false;
        _attachments.Remove(existing);
        try { File.Delete(Path.Combine(FilesDirectory, existing.StoredName)); } catch { }
        return true;
    }
}
