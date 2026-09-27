namespace SyncNote.Core;

// In-memory реализация ISyncStore для тестов и этапа UI.
// Удаление мягкое (tombstone), как в SQLite.
public sealed class InMemoryNoteStore : ISyncStore
{
    private readonly List<Note> _notes = new();
    private readonly List<ChecklistItem> _items = new();
    private readonly List<Attachment> _attachments = new();
    private readonly Dictionary<Guid, long> _syncRevs = new();

    public string FilesDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "SyncNoteMemFiles");

    public string DeviceId { get; } = Guid.NewGuid().ToString("N");

    public IReadOnlyList<Note> List() =>
        _notes.Where(n => !n.IsDeleted).OrderByDescending(n => n.UpdatedAt).ToList();

    public Note? TryGet(Guid id) => _notes.FirstOrDefault(n => n.Id == id);

    public IReadOnlyList<Note> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return List();
        return _notes
            .Where(n => !n.IsDeleted &&
                (n.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || n.Body.Contains(query, StringComparison.OrdinalIgnoreCase)))
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
        if (existing.IsDeleted)
            throw new KeyNotFoundException($"Note {note.Id} not found.");
        existing.Title = note.Title;
        existing.Body = note.Body;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.Rev += 1;
    }

    public bool Delete(Guid id)
    {
        var existing = _notes.FirstOrDefault(n => n.Id == id);
        if (existing is null || existing.IsDeleted)
            return false;
        existing.IsDeleted = true;
        existing.Rev += 1;
        existing.UpdatedAt = DateTime.UtcNow;
        return true;
    }

    public IReadOnlyList<ChecklistItem> GetChecklist(Guid noteId) =>
        _items.Where(i => i.NoteId == noteId).OrderBy(i => i.Position).ToList();

    public ChecklistItem AddChecklistItem(Guid noteId, string text)
    {
        var item = new ChecklistItem
        {
            NoteId = noteId,
            Position = _items.Count(i => i.NoteId == noteId),
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

    public Attachment ImportAttachment(Guid noteId, string fileName, string mime, byte[] content)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"syncnote-imp-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(tmp, content);
            var att = AddAttachment(noteId, tmp);
            att.FileName = fileName;
            att.MimeType = mime;
            return att;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
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

    public long GetSyncRev(Guid noteId) =>
        _syncRevs.TryGetValue(noteId, out var rev) ? rev : 0;

    public void SetSyncRev(Guid noteId, long rev) => _syncRevs[noteId] = rev;

    private readonly HashSet<(Guid, long, string)> _seenConflicts = new();

    public bool NoteSeenConflict(Guid id, long rev, string contentHash)
    {
        var key = (id, rev, contentHash);
        if (_seenConflicts.Contains(key))
            return true;
        _seenConflicts.Add(key);
        return false;
    }

    public void ImportFull(SyncNoteDto dto, Func<string, byte[]?> fileBytes)
    {
        var existing = TryGet(dto.Id);
        Note note;
        if (existing is null)
        {
            note = new Note { Id = dto.Id };
            _notes.Add(note);
        }
        else
        {
            note = existing;
        }
        note.Title = dto.Title;
        note.Body = dto.Body;
        note.UpdatedAt = dto.UpdatedAt;
        note.Rev = dto.Rev;
        note.AuthorDeviceId = dto.Author;
        note.IsDeleted = dto.IsDeleted;

        _items.RemoveAll(i => i.NoteId == dto.Id);
        foreach (var c in dto.Checklist.OrderBy(c => c.Position))
            _items.Add(new ChecklistItem
            {
                NoteId = dto.Id,
                Position = c.Position,
                Text = c.Text,
                IsChecked = c.IsChecked,
            });

        var wantSha = dto.Attachments.Select(a => a.Sha256).ToHashSet();
        foreach (var old in _attachments.Where(a => a.NoteId == dto.Id).ToList())
            if (!wantSha.Contains(old.Sha256))
                DeleteAttachment(old.Id);
        var haveSha = GetAttachments(dto.Id).Select(a => a.Sha256).ToHashSet();
        foreach (var meta in dto.Attachments)
        {
            if (haveSha.Contains(meta.Sha256))
                continue;
            var bytes = fileBytes(meta.Sha256)
                ?? throw new InvalidOperationException(
                    $"Нет байтов файла {meta.FileName} (sha256 {meta.Sha256}).");
            ImportAttachment(dto.Id, meta.FileName, meta.MimeType, bytes);
        }
    }

    public IReadOnlyList<SyncNoteDto> Export() =>
        _notes.Select(n => new SyncNoteDto
        {
            Id = n.Id,
            Rev = n.Rev,
            BaseRev = GetSyncRev(n.Id),
            Title = n.Title,
            Body = n.Body,
            UpdatedAt = n.UpdatedAt,
            Author = n.AuthorDeviceId,
            IsDeleted = n.IsDeleted,
            Checklist = GetChecklist(n.Id).Select(i => new ChecklistItemDto
            {
                Position = i.Position,
                Text = i.Text,
                IsChecked = i.IsChecked,
            }).ToList(),
            Attachments = GetAttachments(n.Id).Select(a => new AttachmentMetaDto
            {
                FileName = a.FileName,
                MimeType = a.MimeType,
                SizeBytes = a.SizeBytes,
                Sha256 = a.Sha256,
            }).ToList(),
        }).ToList();
}
