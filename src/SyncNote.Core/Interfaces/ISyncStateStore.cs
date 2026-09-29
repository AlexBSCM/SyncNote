namespace SyncNote.Core.Interfaces;

// Доступ к таблицам sync_state / seen_conflicts / files-подтверждениям.
// Реализации: SQL (платформы). Тесты: in-memory фейк.
public interface ISyncStateStore
{
    long GetSyncRev(string entityType, string entityId);
    void SetSyncRev(string entityType, string entityId, long rev);
    // true — такой конфликт уже видели (копию не создавать).
    bool NoteSeenConflict(string originalId, long rev, string contentHash);
    // Живая строка с таким sha (для ответа hash_response).
    bool HasLiveFileRef(string sha256);
}
