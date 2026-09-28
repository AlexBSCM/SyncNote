using SyncNote.Core;

namespace SyncNote.Core.Tests;

[TestClass]
public sealed class FileIoTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), $"syncnote-fileio-{Guid.NewGuid():N}");

    private static string WriteSource(string dir, string name, byte[] content)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [TestMethod]
    public void Import_Twice_DedupsToSinglePhysicalFile()
    {
        var baseDir = TempDir();
        var src = WriteSource(baseDir, "a.bin", new byte[] { 1, 2, 3, 4, 5 });
        var filesDir = Path.Combine(baseDir, "files");

        var first = FileIo.ImportFromFile(filesDir, src);
        var second = FileIo.ImportFromFile(filesDir, src);

        // Один физический файл на оба импорта, имя == sha256.
        Assert.AreEqual(first.Sha256Hex, second.Sha256Hex);
        Assert.AreEqual(first.Sha256Hex, first.StoredName);
        Assert.AreEqual(5, first.SizeBytes);
        Assert.AreEqual(1, Directory.GetFiles(filesDir).Length);
        CollectionAssert.AreEqual(
            new byte[] { 1, 2, 3, 4, 5 },
            File.ReadAllBytes(Path.Combine(filesDir, first.StoredName)));
    }

    [TestMethod]
    public void Import_SameContentDifferentNames_Dedups()
    {
        var baseDir = TempDir();
        var payload = new byte[] { 9, 8, 7 };
        var src1 = WriteSource(baseDir, "one.txt", payload);
        var src2 = WriteSource(baseDir, "two.txt", payload);
        var filesDir = Path.Combine(baseDir, "files");

        var r1 = FileIo.ImportFromFile(filesDir, src1);
        var r2 = FileIo.ImportFromFile(filesDir, src2);

        Assert.AreEqual(r1.Sha256Hex, r2.Sha256Hex);
        Assert.AreEqual(1, Directory.GetFiles(filesDir).Length);
    }

    [TestMethod]
    public void Import_OverLimit_ThrowsBeforeCopy()
    {
        var baseDir = TempDir();
        Directory.CreateDirectory(baseDir);
        var filesDir = Path.Combine(baseDir, "files");
        var big = Path.Combine(baseDir, "big.bin");
        using (var fs = File.Create(big))
            fs.SetLength(FileIo.MaxFileSizeBytes + 1);

        var ex = Assert.ThrowsException<FileTooLargeException>(
            () => FileIo.ImportFromFile(filesDir, big));
        Assert.AreEqual(FileIo.MaxFileSizeBytes, ex.Limit);
        // Ничего не скопировано, каталога может даже не быть.
        Assert.IsFalse(Directory.Exists(filesDir) && Directory.GetFiles(filesDir).Length > 0);
    }

    [TestMethod]
    public void Import_MissingSource_ThrowsFileNotFound()
    {
        var filesDir = TempDir();
        Assert.ThrowsException<FileNotFoundException>(
            () => FileIo.ImportFromFile(filesDir, Path.Combine(filesDir, "nope.bin")));
    }

    [TestMethod]
    public void ResolveStoragePath_Missing_ThrowsFileNotFound()
    {
        var filesDir = TempDir();
        Directory.CreateDirectory(filesDir);
        Assert.ThrowsException<FileNotFoundException>(
            () => FileIo.ResolveStoragePath(filesDir, new string('a', 64)));
    }

    [TestMethod]
    public void ResolveStoragePath_BadHash_ThrowsArgument()
    {
        var filesDir = TempDir();
        Assert.ThrowsException<ArgumentException>(
            () => FileIo.ResolveStoragePath(filesDir, "../evil"));
    }

    [TestMethod]
    public void ResolveStoragePath_Ok_ReturnsPath()
    {
        var baseDir = TempDir();
        var src = WriteSource(baseDir, "x.bin", new byte[] { 7 });
        var filesDir = Path.Combine(baseDir, "files");
        var r = FileIo.ImportFromFile(filesDir, src);
        Assert.AreEqual(
            Path.Combine(filesDir, r.Sha256Hex),
            FileIo.ResolveStoragePath(filesDir, r.Sha256Hex));
    }

    [TestMethod]
    public void DeleteIfOrphaned_RespectsRefs()
    {
        var baseDir = TempDir();
        var src = WriteSource(baseDir, "y.bin", new byte[] { 1 });
        var filesDir = Path.Combine(baseDir, "files");
        var r = FileIo.ImportFromFile(filesDir, src);

        // Есть живые ссылки — не удаляем.
        Assert.IsFalse(FileIo.DeleteIfOrphaned(filesDir, r.Sha256Hex, _ => true));
        Assert.IsTrue(File.Exists(Path.Combine(filesDir, r.Sha256Hex)));

        // Ссылок нет — удаляем.
        Assert.IsTrue(FileIo.DeleteIfOrphaned(filesDir, r.Sha256Hex, _ => false));
        Assert.IsFalse(File.Exists(Path.Combine(filesDir, r.Sha256Hex)));

        // Повторное удаление отсутствующего — false, не исключение.
        Assert.IsFalse(FileIo.DeleteIfOrphaned(filesDir, r.Sha256Hex, _ => false));
    }

    [TestMethod]
    public void NoTempLeftovers_AfterSuccess()
    {
        var baseDir = TempDir();
        var src = WriteSource(baseDir, "z.bin", new byte[] { 2, 3 });
        var filesDir = Path.Combine(baseDir, "files");
        FileIo.ImportFromFile(filesDir, src);
        Assert.AreEqual(0, Directory.GetFiles(filesDir, "*.tmp").Length);
    }
}
