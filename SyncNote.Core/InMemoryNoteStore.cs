namespace SyncNote.Core;

// Временное хранилище в памяти для этапа UI.
// Заменено на SQLite на этапе хранения без изменения интерфейса.
public sealed class InMemoryNoteStore : INoteStore
{
    private readonly List<Note> _notes = new();

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
}
