namespace SyncNote.Core;

// DTO синхронизации (JSON-совместимо, см. protocol/pairing-and-sync.md).
// BaseRev — ревизия, от которой отталкивается отправитель
// (его syncRev для этой заметки).
public sealed class ChecklistItemDto
{
    public int Position { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool IsChecked { get; set; }
}

public sealed class AttachmentMetaDto
{
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class SyncNoteDto
{
    public Guid Id { get; set; }
    public long Rev { get; set; }
    public long BaseRev { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
    public string Author { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public List<ChecklistItemDto> Checklist { get; set; } = new();
    public List<AttachmentMetaDto> Attachments { get; set; } = new();
}
