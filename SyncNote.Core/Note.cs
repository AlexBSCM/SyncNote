namespace SyncNote.Core;

// Этап UI: минимальная модель. Этап хранения добавит Rev, DeviceId автора
// правки, чек-листы, вложения и состояние конфликта (docs/architecture.md §5).
public sealed class Note
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Этап хранения (sync-ready): монотонная ревизия, автор правки,
    // мягкое удаление (tombstone для будущей синхронизации).
    public long Rev { get; set; } = 1;
    public string AuthorDeviceId { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
}
