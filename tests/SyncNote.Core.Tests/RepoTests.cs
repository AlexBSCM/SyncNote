using Microsoft.VisualStudio.TestTools.UnitTesting;
using SyncNote.Core.Utils;
using SyncNote.Core.Models;
using SyncNote.Windows.Services;

namespace SyncNote.Core.Tests;

[TestClass]
public sealed class RepoTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string InitDb(string dir)
    {
        string db = Path.Combine(dir, "t.db");
        new WindowsDbInitializer().Initialize(db);
        return db;
    }

    [TestMethod]
    public void Hash_StableAndKnownVector()
    {
        // SHA256("abc") — стандартный вектор.
        const string expect = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        Assert.AreEqual(expect, HashUtils.ComputeSha256("abc"u8.ToArray()));
        using var ms = new MemoryStream("abc"u8.ToArray());
        Assert.AreEqual(expect, HashUtils.ComputeSha256(ms));
    }

    [TestMethod]
    public async Task FileIo_ImportDedupsPhysicalCopy()
    {
        string dir = TempDir();
        var io = new WindowsFileIo(Path.Combine(dir, "files"));
        string src = Path.Combine(dir, "a.bin");
        await File.WriteAllBytesAsync(src, new byte[] { 1, 2, 3, 4, 5 });
        var (sha1, size1) = await io.ImportAsync(src);
        var (sha2, size2) = await io.ImportAsync(src);
        Assert.AreEqual(sha1, sha2);
        Assert.AreEqual(5, size1);
        Assert.AreEqual(size1, size2);
        Assert.IsTrue(io.ExistsInStorage(sha1));
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(dir, "files")).Length);
        using (var s = io.OpenRead(sha1))
        Assert.AreEqual(5, s.Length);
        io.DeleteFromStorage(sha1);
        Assert.IsFalse(io.ExistsInStorage(sha1));
    }

    [TestMethod]
    public async Task NoteRepo_FullCycle()
    {
        string db = InitDb(TempDir());
        var repo = new SqliteNoteRepo(db, "pc");
        string id = await repo.CreateAsync(new NoteEntry { Title = "t", Body = "b" });
        var got = await repo.GetByIdAsync(id);
        Assert.IsNotNull(got);
        Assert.AreEqual(1, got.Rev);
        Assert.AreEqual("pc", got.AuthorDeviceId);
        // No-op без изменений: ревизия стоит.
        await repo.UpdateAsync(new NoteEntry { Id = id, Title = "t", Body = "b" });
        Assert.AreEqual(1, (await repo.GetByIdAsync(id))!.Rev);
        // Правка: ревизия растёт.
        await repo.UpdateAsync(new NoteEntry { Id = id, Title = "t2", Body = "b" });
        Assert.AreEqual(2, (await repo.GetByIdAsync(id))!.Rev);
        // Гонка: stale rev 1 против текущей 2.
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            repo.UpdateAsync(new NoteEntry { Id = id, Title = "x", Body = "b" }));
        await repo.SoftDeleteAsync(id);
        var gone = await repo.GetByIdAsync(id);
        Assert.IsNotNull(gone);
        Assert.IsTrue(gone.IsDeleted);
        Assert.AreEqual(0, (await repo.GetAllAsync()).Count);
        Assert.AreEqual(1, (await repo.GetAllAsync(includeDeleted: true)).Count);
    }

    [TestMethod]
    public async Task FileRepo_AddListsAndDeletes()
    {
        string dir = TempDir();
        string db = InitDb(dir);
        var io = new WindowsFileIo(Path.Combine(dir, "files"));
        var repo = new SqliteFileRepo(db, "pc", io);
        string src = Path.Combine(dir, "a.bin");
        await File.WriteAllBytesAsync(src, new byte[] { 9, 9, 9 });
        string id = await repo.AddFileAsync(
            new FileEntry { Name = "a.bin", Mime = "application/octet-stream" }, src);
        var got = await repo.GetByIdAsync(id);
        Assert.IsNotNull(got);
        Assert.AreEqual(3, got.SizeBytes);
        Assert.AreEqual(64, got.Sha256.Length);
        Assert.AreEqual(got.Sha256, got.StoredName);
        Assert.AreEqual(1, (await repo.GetAllAsync()).Count);
        await repo.SoftDeleteAsync(id);
        Assert.AreEqual(0, (await repo.GetAllAsync()).Count);
    }
}
