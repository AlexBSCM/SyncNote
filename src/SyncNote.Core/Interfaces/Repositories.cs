using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SyncNote.Core.Models;

namespace SyncNote.Core.Interfaces;

public interface IFileIo
{
    string ComputeSha256(Stream stream);
    string ComputeSha256(byte[] data);
    Task<(string sha256, long size)> ImportAsync(string sourcePath, CancellationToken ct = default);
    bool ExistsInStorage(string sha256);
    Stream OpenRead(string sha256);
    // Удаляет физический файл БЕЗ проверки ссылок: вызывать только после
    // проверки отсутствия живых записей в БД (это обязанность repo/sync-слоя).
    void DeleteFromStorage(string sha256);
    string GetStoragePath(string sha256);
}

public interface INoteRepo
{
    Task<NoteEntry?> GetByIdAsync(string id);
    Task<List<NoteEntry>> GetAllAsync(bool includeDeleted = false);
    // Создаёт запись (rev=1, updated_at/author выставляет реализация).
    // Если note.Id непустой — используется как есть (нужно синку для
    // вставки строк с известными id); иначе генерируется. Возвращает ID.
    Task<string> CreateAsync(NoteEntry note);
    // Обновляет при изменениях (rev+1 внутри); без изменений — no-op.
    // При гонке ревизий бросает InvalidOperationException.
    Task UpdateAsync(NoteEntry note);
    // Tombstone: is_deleted=1, rev+1.
    Task SoftDeleteAsync(string id);
    // Полная вставка/перезапись строки как есть (ид, rev, автор, дата) —
    // только для sync-движка; UI пользуется Create/Update.
    Task InsertFullAsync(NoteEntry e);
    Task UpdateFullAsync(NoteEntry e);
}

public interface IFileRepo
{
    Task<FileEntry?> GetByIdAsync(string id);
    Task<List<FileEntry>> GetAllAsync(bool includeDeleted = false);
    // Импортирует байты по пути, считает хеш (дедуп физического файла),
    // создаёт строку БД. Хеш вычисляется внутри.
    Task<string> AddFileAsync(FileEntry metadata, string localTempPathOrUri);
    // Tombstone: is_deleted=1, rev+1 (байты чистит purge, не этот метод).
    Task SoftDeleteAsync(string id);
    // Полная вставка/перезапись строки как есть — только для sync-движка.
    Task InsertFullAsync(FileEntry e);
    Task UpdateFullAsync(FileEntry e);
}
