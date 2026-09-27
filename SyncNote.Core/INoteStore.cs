namespace SyncNote.Core;

public interface INoteStore
{
    IReadOnlyList<Note> List();
    IReadOnlyList<Note> Search(string query);
    Note Add(string title, string body);
    void Update(Note note);
    bool Delete(Guid id);

    IReadOnlyList<ChecklistItem> GetChecklist(Guid noteId);
    ChecklistItem AddChecklistItem(Guid noteId, string text);
    void UpdateChecklistItem(ChecklistItem item);
    bool DeleteChecklistItem(Guid itemId);

    string FilesDirectory { get; }
    IReadOnlyList<Attachment> GetAttachments(Guid noteId);
    Attachment AddAttachment(Guid noteId, string sourcePath);
    bool DeleteAttachment(Guid attachmentId);
}
