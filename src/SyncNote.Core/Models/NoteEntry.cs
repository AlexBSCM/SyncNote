namespace SyncNote.Core.Models;

// Строка таблицы notes. Чистая модель без логики отображения.
public sealed class NoteEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public long Rev { get; set; } = 1;
    public string UpdatedAt { get; set; } = "";
    public string AuthorDeviceId { get; set; } = "";
    public bool IsDeleted { get; set; }
}
