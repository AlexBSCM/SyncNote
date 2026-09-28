using SyncNote.Core;

namespace SyncNote.Core.Tests;

// Тесты подсистемы «Отдельные файлы», шаг F.3.
// CRUD, tombstone, ApplyFile (вставка/fast-forward/конфликт/идемпотентность),
// поведение при выключенном флаге EnableSeparateFiles.
[TestClass]
public sealed class FileSyncTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"syncnote-file-{Guid.NewGuid():N}.db");

    private static string WriteSource(string name, byte[] content)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"syncnote-filesrc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [TestInitialize]
    public void EnableFlag() => FeatureFlags.EnableSeparateFiles = true;

    [TestCleanup]
    public void RestoreFlag() => FeatureFlags.EnableSeparateFiles = true;

    [TestMethod]
    public void Crud_AddGetDelete_Tombstone()
    {
        using var store = new SqliteNoteStore(TempDb());
        var src = WriteSource("doc.txt", new byte[] { 1, 2, 3 });
        try
        {
            var entry = store.AddFile(src);
            Assert.AreEqual("doc.txt", entry.Name);
            Assert.AreEqual(3, entry.SizeBytes);
            Assert.AreEqual(1, entry.Rev);
            Assert.IsFalse(entry.IsDeleted);
            // Дедуп с вложениями не пересекается по именам, но контентный хеш общий:
            // физический файл лежит под именем == sha256.
            Assert.AreEqual(entry.Sha256, entry.StoredName);

            var list = store.GetFiles();
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual(entry.Id, list[0].Id);

            Assert.IsTrue(store.DeleteFile(entry.Id));
            Assert.AreEqual(0, store.GetFiles().Count);
            Assert.AreEqual(1, store.GetFiles(includeDeleted: true).Count);

            // Tombstone: запись жива, rev вырос.
            var tomb = store.TryGetFile(entry.Id);
            Assert.IsNotNull(tomb);
            Assert.IsTrue(tomb!.IsDeleted);
            Assert.AreEqual(2, tomb.Rev);

            // Повторное удаление отсутствующего — false.
            Assert.IsFalse(store.DeleteFile(Guid.NewGuid()));
        }
        finally { Directory.Delete(Path.GetDirectoryName(src)!, recursive: true); }
    }

    [TestMethod]
    public void AddFile_Twice_SameContent_SinglePhysicalFile()
    {
        using var store = new SqliteNoteStore(TempDb());
        var src = WriteSource("same.bin", new byte[] { 5, 6 });
        try
        {
            var a = store.AddFile(src);
            var b = store.AddFile(src);
            Assert.AreNotEqual(a.Id, b.Id);
            Assert.AreEqual(a.Sha256, b.Sha256);
            Assert.AreEqual(a.StoredName, b.StoredName);
            // Каталог общий с другими тестами — считаем только наш sha.
            Assert.AreEqual(1, Directory.GetFiles(store.FilesDirectory, a.Sha256).Length);
        }
        finally { Directory.Delete(Path.GetDirectoryName(src)!, recursive: true); }
    }

    private static SyncFileDto Dto(Guid id, long rev, string name, string sha, bool deleted = false) =>
        new()
        {
            Id = id,
            Rev = rev,
            BaseRev = 0,
            Name = name,
            Mime = "application/octet-stream",
            Size = 1,
            Sha256 = sha,
            UpdatedAt = DateTime.UtcNow,
            Author = "dev1",
            IsDeleted = deleted,
        };

    [TestMethod]
    public void ApplyFile_Insert_FastForward_Conflict_NoDup()
    {
        using var a = new SqliteNoteStore(TempDb());
        using var b = new SqliteNoteStore(TempDb());
        var payload = new byte[] { 10, 20 };
        Func<string, byte[]?> files = _ => payload;

        // База A: файл rev 1. Синк A->B: вставка.
        var src = WriteSource("f.bin", payload);
        FileEntry added;
        try { added = a.AddFile(src); }
        finally { Directory.Delete(Path.GetDirectoryName(src)!, recursive: true); }
        var dto = a.ExportFiles().First(d => d.Id == added.Id);

        var (r1, _) = b.ApplyFile(dto, files);
        Assert.AreEqual(ApplyResult.Inserted, r1);
        Assert.AreEqual("f.bin", b.TryGetFile(added.Id)!.Name);

        // Повтор той же версии — NoOp.
        var (r2, c2) = b.ApplyFile(dto, files);
        Assert.AreEqual(ApplyResult.NoOp, r2);
        Assert.IsNull(c2);

        // Расхождение: обе стороны правили после общей базы (rev 2 при syncRev 1).
        var dtoA = new SyncFileDto
        {
            Id = added.Id, Rev = 2, BaseRev = 1, Name = "Версия A",
            Mime = "application/octet-stream", Size = 2, Sha256 = added.Sha256,
            UpdatedAt = DateTime.UtcNow, Author = "devA",
        };
        var dtoB = new SyncFileDto
        {
            Id = added.Id, Rev = 2, BaseRev = 1, Name = "Версия B",
            Mime = "application/octet-stream", Size = 2, Sha256 = added.Sha256,
            UpdatedAt = DateTime.UtcNow, Author = "devB",
        };
        // Сначала B принимает свою правку как fast-forward от base 1:
        // (локально rev 1 == syncRev 1, base 1 >= 1).
        var (rb, _) = b.ApplyFile(dtoB, files);
        Assert.AreEqual(ApplyResult.FastForwarded, rb);

        // Теперь входит A rev 2 base 1 против локальных rev 2 sync 1 -> конфликт.
        var (r3, c3) = b.ApplyFile(dtoA, files);
        Assert.AreEqual(ApplyResult.Conflict, r3);
        Assert.IsNotNull(c3);
        Assert.IsTrue(c3!.CopyTitle.EndsWith(" (конфликт)"));

        // Повтор той же конфликтной версии — копий больше нет.
        var (r4, c4) = b.ApplyFile(dtoA, files);
        Assert.AreEqual(ApplyResult.NoOp, r4);
        Assert.IsNull(c4);
        Assert.AreEqual(2, b.GetFiles(includeDeleted: true).Count); // оригинал + 1 копия
    }

    [TestMethod]
    public void ApplyFile_Delete_Propagates_AsTombstone()
    {
        using var a = new SqliteNoteStore(TempDb());
        using var b = new SqliteNoteStore(TempDb());
        Func<string, byte[]?> files = _ => new byte[] { 1 };
        var src = WriteSource("d.bin", new byte[] { 1 });
        try { a.AddFile(src); }
        finally { Directory.Delete(Path.GetDirectoryName(src)!, recursive: true); }

        var dto = a.ExportFiles()[0];
        var (r1, _) = b.ApplyFile(dto, files);
        Assert.AreEqual(ApplyResult.Inserted, r1);

        a.DeleteFile(dto.Id);
        var tomb = a.ExportFiles().First(d => d.Id == dto.Id);
        Assert.IsTrue(tomb.IsDeleted);

        var (r2, _) = b.ApplyFile(tomb, files);
        Assert.AreEqual(ApplyResult.FastForwarded, r2);
        Assert.AreEqual(0, b.GetFiles().Count);
        Assert.IsNotNull(b.TryGetFile(dto.Id)); // tombstone сохранён
    }

    [TestMethod]
    public void DisabledFlag_BlocksNewPaths()
    {
        using var store = new SqliteNoteStore(TempDb());
        FeatureFlags.EnableSeparateFiles = false;
        try
        {
            var src = WriteSource("x.bin", new byte[] { 1 });
            try
            {
                Assert.ThrowsException<InvalidOperationException>(() => store.AddFile(src));
                Assert.ThrowsException<InvalidOperationException>(() => store.DeleteFile(Guid.NewGuid()));
                Assert.ThrowsException<InvalidOperationException>(() => store.ExportFiles());
                Assert.ThrowsException<InvalidOperationException>(
                    () => store.ApplyFile(Dto(Guid.NewGuid(), 1, "x", new string('a', 64)), _ => null));
            }
            finally { Directory.Delete(Path.GetDirectoryName(src)!, recursive: true); }
            // Чтение не блокируется (безвредно) и старые пути живы.
            Assert.AreEqual(0, store.GetFiles().Count);
            var note = store.Add("t", "b");
            Assert.AreEqual(1, store.List().Count);
            Assert.AreEqual(note.Id, store.List()[0].Id);
        }
        finally
        {
            FeatureFlags.EnableSeparateFiles = true;
        }
    }

    [TestMethod]
    public void ExportFiles_IncludesTombstones_WithBaseRev()
    {
        using var store = new SqliteNoteStore(TempDb());
        var src = WriteSource("e.bin", new byte[] { 2 });
        try
        {
            var e = store.AddFile(src);
            store.DeleteFile(e.Id);
            var all = store.ExportFiles();
            Assert.AreEqual(1, all.Count);
            Assert.IsTrue(all[0].IsDeleted);
            Assert.AreEqual(0, all[0].BaseRev); // sync ещё не было
        }
        finally { Directory.Delete(Path.GetDirectoryName(src)!, recursive: true); }
    }
}
