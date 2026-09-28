namespace SyncNote.Core;

// DTO отдельного файла для протокола (JSON-совместимо).
// BaseRev — ревизия отправителя (его file_syncstate для этого файла).
public sealed class SyncFileDto
{
    public Guid Id { get; set; }
    public long Rev { get; set; }
    public long BaseRev { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Mime { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
    public string Author { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
}

// Строка таблицы files.
public sealed class FileEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Mime { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string StoredName { get; set; } = string.Empty;
    public long Rev { get; set; } = 1;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string AuthorDeviceId { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
}
