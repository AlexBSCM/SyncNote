namespace SyncNote.Core;

public enum ApplyResult { Inserted, FastForwarded, NoOp, Conflict }

public sealed record ConflictInfo(Guid OriginalId, Guid CopyId, string CopyTitle);

// Применение входящих изменений. Идемпотентно: повтор (id, rev) — NoOp.
// Конфликт (обе стороны правили после общей базы): локальное не трогаем,
// входящее сохраняем копией с новым id. Потерь нет.
public static class SyncEngine
{
    public static (ApplyResult Result, ConflictInfo? Conflict) Apply(
        ISyncStore store, SyncNoteDto dto, Func<string, byte[]?> fileBytes)
    {
        var local = store.TryGet(dto.Id);
        if (local is null)
        {
            store.ImportFull(dto, fileBytes);
            store.SetSyncRev(dto.Id, dto.Rev);
            return (ApplyResult.Inserted, null);
        }

        var syncRev = store.GetSyncRev(dto.Id);
        if (dto.Rev == local.Rev)
        {
            if (SameContent(store, local, dto))
            {
                store.SetSyncRev(dto.Id, Math.Max(syncRev, dto.Rev));
                return (ApplyResult.NoOp, null);
            }
            // Та же ревизия, разное содержимое: копия — один раз на хеш.
            var hash = ContentHash(dto);
            if (store.NoteSeenConflict(dto.Id, dto.Rev, hash))
            {
                store.SetSyncRev(dto.Id, Math.Max(syncRev, dto.Rev));
                return (ApplyResult.NoOp, null);
            }
            return MakeConflictCopy(store, dto, fileBytes);
        }

        if (dto.Rev < local.Rev)
            return (ApplyResult.NoOp, null);

        // dto.Rev > local.Rev
        if (local.Rev == syncRev || dto.BaseRev >= syncRev)
        {
            store.ImportFull(dto, fileBytes);
            store.SetSyncRev(dto.Id, dto.Rev);
            return (ApplyResult.FastForwarded, null);
        }

        // Расхождение: копия — один раз на хеш входящей версии.
        var incomingHash = ContentHash(dto);
        if (store.NoteSeenConflict(dto.Id, dto.Rev, incomingHash))
            return (ApplyResult.NoOp, null);
        return MakeConflictCopy(store, dto, fileBytes);
    }

    public static string ContentHash(SyncNoteDto dto)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(dto.Title).Append('\0').Append(dto.Body).Append('\0')
            .Append(dto.IsDeleted ? '1' : '0').Append('\0');
        foreach (var c in dto.Checklist.OrderBy(c => c.Position))
            sb.Append(c.Position).Append(':').Append(c.Text).Append(':')
                .Append(c.IsChecked ? '1' : '0').Append('\0');
        foreach (var s in dto.Attachments.Select(a => a.Sha256).OrderBy(s => s))
            sb.Append(s).Append('\0');
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static (ApplyResult, ConflictInfo?) MakeConflictCopy(
        ISyncStore store, SyncNoteDto dto, Func<string, byte[]?> fileBytes)
    {
        var copy = new SyncNoteDto
        {
            Id = Guid.NewGuid(),
            Rev = 1,
            BaseRev = 0,
            Title = $"{dto.Title} (копия конфликта)",
            Body = dto.Body,
            UpdatedAt = dto.UpdatedAt,
            Author = dto.Author,
            IsDeleted = false,
            Checklist = dto.Checklist,
            Attachments = dto.Attachments,
        };
        store.ImportFull(copy, fileBytes);
        store.SetSyncRev(copy.Id, 1);
        return (ApplyResult.Conflict, new ConflictInfo(dto.Id, copy.Id, copy.Title));
    }

    private static bool SameContent(ISyncStore store, Note local, SyncNoteDto dto)
    {
        if (local.Title != dto.Title || local.Body != dto.Body || local.IsDeleted != dto.IsDeleted)
            return false;
        var items = store.GetChecklist(local.Id);
        if (items.Count != dto.Checklist.Count)
            return false;
        var ordered = items.OrderBy(i => i.Position).ToList();
        var want = dto.Checklist.OrderBy(c => c.Position).ToList();
        for (int k = 0; k < ordered.Count; k++)
            if (ordered[k].Text != want[k].Text || ordered[k].IsChecked != want[k].IsChecked)
                return false;
        var haveFiles = store.GetAttachments(local.Id).Select(a => a.Sha256).OrderBy(s => s);
        var wantFiles = dto.Attachments.Select(a => a.Sha256).OrderBy(s => s);
        return haveFiles.SequenceEqual(wantFiles);
    }
}
