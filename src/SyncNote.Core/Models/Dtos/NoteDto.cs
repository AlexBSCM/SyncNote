namespace SyncNote.Core.Models.Dtos;

// DTO заметки для провода. stored_name/norm-колонки не едут (локальное).
public sealed class NoteDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public long Rev { get; set; }
    public string UpdatedAt { get; set; } = "";
    public string Author { get; set; } = "";
    public bool IsDeleted { get; set; }
    // sync_state отправителя — нужен получателю для fast-forward/конфликт.
    public long BaseRev { get; set; }
}
