namespace SyncNote.Core;

public static class SyncStoreExtensions
{
    public static (ApplyResult Result, ConflictInfo? Conflict) Apply(
        this ISyncStore store, SyncNoteDto dto, Func<string, byte[]?>? fileBytes = null) =>
        SyncEngine.Apply(store, dto, fileBytes ?? (_ => null));
}
