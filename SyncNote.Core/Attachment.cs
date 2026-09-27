namespace SyncNote.Core;

// Метаданные вложения. Сам файл хранится в каталоге files/ без изменений
// (побайтово оригинал), имя файла в метаданных — оригинальное.
public sealed class Attachment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid NoteId { get; init; }
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string StoredName { get; set; } = string.Empty;
}
