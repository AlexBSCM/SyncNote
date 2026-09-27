namespace SyncNote.Core;

// Расширение хранилища для синхронизации. UI продолжает работать с INoteStore.
public interface ISyncStore : INoteStore
{
    Note? TryGet(Guid id);
    long GetSyncRev(Guid noteId);
    void SetSyncRev(Guid noteId, long rev);
    IReadOnlyList<SyncNoteDto> Export();
    Attachment ImportAttachment(Guid noteId, string fileName, string mime, byte[] content);
    void ImportFull(SyncNoteDto dto, Func<string, byte[]?> fileBytes);
}
