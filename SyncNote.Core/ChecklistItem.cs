namespace SyncNote.Core;

public sealed class ChecklistItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid NoteId { get; init; }
    public int Position { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool IsChecked { get; set; }
}
