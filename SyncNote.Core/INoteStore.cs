namespace SyncNote.Core;

public interface INoteStore
{
    IReadOnlyList<Note> List();
    IReadOnlyList<Note> Search(string query);
    Note Add(string title, string body);
    void Update(Note note);
    bool Delete(Guid id);
}
