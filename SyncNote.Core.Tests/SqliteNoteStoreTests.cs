using SyncNote.Core;

namespace SyncNote.Core.Tests;

[TestClass]
public sealed class SqliteNoteStoreTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"syncnote-test-{Guid.NewGuid():N}.db");

    [TestMethod]
    public void Add_List_Search_Update_Delete()
    {
        using var store = new SqliteNoteStore(TempDb());
        var first = store.Add("Покупки", "молоко");
        store.Add("Работа", "отчёт");
        Assert.AreEqual(1, first.Rev);
        Assert.AreNotEqual(string.Empty, first.AuthorDeviceId);

        var list = store.List();
        Assert.AreEqual(2, list.Count);

        Assert.AreEqual(1, store.Search("покуп").Count);
        Assert.AreEqual(0, store.Search("нет-такого").Count);

        first.Title = "Покупки!";
        store.Update(first);
        Assert.AreEqual(2, first.Rev);

        Assert.IsTrue(store.Delete(first.Id));
        Assert.AreEqual(1, store.List().Count);
        Assert.IsFalse(store.Delete(first.Id));
    }

    [TestMethod]
    public void Data_Persists_Across_Reopen()
    {
        var path = TempDb();
        Guid id;
        using (var store = new SqliteNoteStore(path))
            id = store.Add("Сохрани", "меня").Id;

        using (var reopened = new SqliteNoteStore(path))
        {
            var notes = reopened.List();
            Assert.AreEqual(1, notes.Count);
            Assert.AreEqual(id, notes[0].Id);
            Assert.AreEqual("Сохрани", notes[0].Title);
        }
    }

    [TestMethod]
    public void Search_IsCaseInsensitive_ForCyrillic()
    {
        using var store = new SqliteNoteStore(TempDb());
        store.Add("ПОКУПКИ", "МОЛОКО");

        Assert.AreEqual(1, store.Search("покупки").Count);
        Assert.AreEqual(1, store.Search("молоко").Count);
        Assert.AreEqual(1, store.Search("ПОКУПКИ").Count);
    }

    [TestMethod]
    public void Migrate_V1_to_V2_KeepsData_AndSearchWorks()
    {
        var path = TempDb();
        var id = Guid.NewGuid().ToString("N");
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE keyvalue(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO keyvalue(key, value) VALUES('schema_version', '1');
                CREATE TABLE notes(
                    id TEXT PRIMARY KEY, rev INTEGER NOT NULL,
                    title TEXT NOT NULL, body TEXT NOT NULL,
                    updated_at TEXT NOT NULL, author_device TEXT NOT NULL,
                    is_deleted INTEGER NOT NULL DEFAULT 0);
                INSERT INTO notes(id, rev, title, body, updated_at, author_device, is_deleted)
                VALUES($id, 1, 'Старая', 'заметка', '2026-01-01T00:00:00Z', 'dev1', 0);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        using (var store = new SqliteNoteStore(path))
        {
            Assert.AreEqual(1, store.Search("старая").Count);
            Assert.AreEqual("Старая", store.List()[0].Title);
        }
    }
    [TestMethod]
    public void Update_Missing_Throws()
    {
        using var store = new SqliteNoteStore(TempDb());
        Assert.ThrowsException<KeyNotFoundException>(
            () => store.Update(new Note { Title = "x", Body = "y" }));
    }

    [TestMethod]
    public void Checklist_Add_Update_Delete_BumpsNoteRev()
    {
        using var store = new SqliteNoteStore(TempDb());
        var note = store.Add("С чек-листом", "тело");
        var rev0 = store.List()[0].Rev;

        var a = store.AddChecklistItem(note.Id, "первый");
        var b = store.AddChecklistItem(note.Id, "второй");
        Assert.AreEqual(0, a.Position);
        Assert.AreEqual(1, b.Position);

        var items = store.GetChecklist(note.Id);
        Assert.AreEqual(2, items.Count);
        Assert.AreEqual("первый", items[0].Text);
        Assert.IsFalse(items[0].IsChecked);

        b.IsChecked = true;
        store.UpdateChecklistItem(b);
        Assert.IsTrue(store.GetChecklist(note.Id)[1].IsChecked);

        Assert.IsTrue(store.DeleteChecklistItem(a.Id));
        Assert.AreEqual(1, store.GetChecklist(note.Id).Count);
        Assert.IsFalse(store.DeleteChecklistItem(Guid.NewGuid()));

        Assert.IsTrue(store.List()[0].Rev > rev0);
    }

    [TestMethod]
    public void Attachments_Roundtrip_Delete_AndLimits()
    {
        using var store = new SqliteNoteStore(TempDb());
        var note = store.Add("С файлом", "тело");

        var src = Path.Combine(Path.GetTempPath(), $"syncnote-src-{Guid.NewGuid():N}.bin");
        var payload = new byte[256];
        new Random(42).NextBytes(payload);
        File.WriteAllBytes(src, payload);

        var att = store.AddAttachment(note.Id, src);
        Assert.AreEqual(Path.GetFileName(src), att.FileName);
        Assert.AreEqual(256, att.SizeBytes);
        Assert.AreEqual(64, att.Sha256.Length);

        var stored = Path.Combine(store.FilesDirectory, att.StoredName);
        Assert.IsTrue(File.Exists(stored));
        CollectionAssert.AreEqual(payload, File.ReadAllBytes(stored));

        var list = store.GetAttachments(note.Id);
        Assert.AreEqual(1, list.Count);
        Assert.AreEqual(att.Id, list[0].Id);

        Assert.IsTrue(store.DeleteAttachment(att.Id));
        Assert.IsFalse(File.Exists(stored));
        Assert.AreEqual(0, store.GetAttachments(note.Id).Count);
        Assert.IsFalse(store.DeleteAttachment(Guid.NewGuid()));

        File.Delete(src);
    }

    [TestMethod]
    public void AddAttachment_MissingFile_Throws()
    {
        using var store = new SqliteNoteStore(TempDb());
        var note = store.Add("Без файла", "тело");
        Assert.ThrowsException<FileNotFoundException>(
            () => store.AddAttachment(note.Id, Path.Combine(Path.GetTempPath(), "syncnote-nope.bin")));
    }

    [TestMethod]
    public void AddAttachment_OverLimit_ThrowsExplicitly()
    {
        using var store = new SqliteNoteStore(TempDb());
        var note = store.Add("Большой", "тело");
        var big = Path.Combine(Path.GetTempPath(), $"syncnote-big-{Guid.NewGuid():N}.bin");
        using (var fs = File.Create(big))
            fs.SetLength(AttachmentIo.MaxAttachmentBytes + 1);
        try
        {
            Assert.ThrowsException<AttachmentTooLargeException>(
                () => store.AddAttachment(note.Id, big));
            Assert.AreEqual(0, store.GetAttachments(note.Id).Count);
        }
        finally { File.Delete(big); }
    }
}
