namespace SyncNote.Core;

// Контракт подсистемы «Отдельные файлы» (этап F).
// Форма повторяет INoteStore/ISyncStore для заметок:
// чтение / создание / удаление-tombstone / sync rev / экспорт / применение.
// Независимый путь: ни один метод не вызывает методы заметок,
// общие только утилиты (FileIo) и примитивы БД.
public interface IFileStore
{
    IReadOnlyList<FileEntry> GetFiles(bool includeDeleted = false);
    FileEntry? TryGetFile(Guid id);
    FileEntry AddFile(string sourcePath);
    bool DeleteFile(Guid id);

    long GetFileSyncRev(Guid fileId);
    void SetFileSyncRev(Guid fileId, long rev);
    bool HasLiveReferencesToSha(string sha256);

    IReadOnlyList<SyncFileDto> ExportFiles();
    (ApplyResult Result, ConflictInfo? Conflict) ApplyFile(
        SyncFileDto dto, Func<string, byte[]?> fileBytes);

    // Waterfall purge: удаляет ФИЗИЧЕСКИЕ файлы tombstone-строк, на которые
    // нет живых ссылок. Сами строки-tombstone остаются (нужны для
    // распространения удаления). Возвращает число удалённых файлов.
    // При выключенном флаге — no-op, возвращает 0.
    int SweepOrphanedFiles();
}
