namespace SyncNote.Core.Models;

// Строка таблицы attachments (схема docs/schema.sql).
// Байты — в content-addressed хранилище под sha256.
public sealed class AttachmentEntry
{
    public string Id { get; set; } = "";
    public string NoteId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mime { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string StoredName { get; set; } = "";
    public long Rev { get; set; } = 1;
    public string UpdatedAt { get; set; } = "";
    public string AuthorDeviceId { get; set; } = "";
    public bool IsDeleted { get; set; }
}
