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
    public void Migrate_V3_to_V4_CreatesAttachments()
    {
        var path = TempDb();
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE keyvalue(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO keyvalue(key, value) VALUES('schema_version', '3');
                CREATE TABLE notes(
                    id TEXT PRIMARY KEY, rev INTEGER NOT NULL,
                    title TEXT NOT NULL, body TEXT NOT NULL,
                    updated_at TEXT NOT NULL, author_device TEXT NOT NULL,
                    is_deleted INTEGER NOT NULL DEFAULT 0,
                    title_norm TEXT NOT NULL DEFAULT '', body_norm TEXT NOT NULL DEFAULT '');
                CREATE TABLE checklist_items(
                    id TEXT PRIMARY KEY, note_id TEXT NOT NULL REFERENCES notes(id),
                    position INTEGER NOT NULL, text TEXT NOT NULL,
                    is_checked INTEGER NOT NULL DEFAULT 0);
                """;
            cmd.ExecuteNonQuery();
        }

        using (var store = new SqliteNoteStore(path))
        {
            var note = store.Add("Влож", "тело");
            var src = Path.Combine(Path.GetTempPath(), $"syncnote-v34-{Guid.NewGuid():N}.txt");
            File.WriteAllText(src, "hello");
            try
            {
                var att = store.AddAttachment(note.Id, src);
                Assert.AreEqual(1, store.GetAttachments(note.Id).Count);
                Assert.AreEqual("hello", File.ReadAllText(
                    Path.Combine(store.FilesDirectory, att.StoredName)));
            }
            finally { File.Delete(src); }
        }
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

    [TestMethod]
    public void Migrate_V6_to_V7_Empty_CreatesFileTables()
    {
        var path = TempDb();
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            // Минимальная v6: keyvalue + версия. Остальные таблицы v6 не нужны,
            // миграция v6->v7 их не трогает; store откроется после RepairStoredNames,
            // которому нужна attachments — создаём пустую.
            cmd.CommandText = """
                CREATE TABLE keyvalue(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO keyvalue(key, value) VALUES('schema_version', '6');
                CREATE TABLE notes(
                    id TEXT PRIMARY KEY, rev INTEGER NOT NULL,
                    title TEXT NOT NULL, body TEXT NOT NULL,
                    updated_at TEXT NOT NULL, author_device TEXT NOT NULL,
                    is_deleted INTEGER NOT NULL DEFAULT 0,
                    title_norm TEXT NOT NULL DEFAULT '', body_norm TEXT NOT NULL DEFAULT '');
                CREATE TABLE attachments(
                    id TEXT PRIMARY KEY, note_id TEXT NOT NULL REFERENCES notes(id),
                    file_name TEXT NOT NULL, mime TEXT NOT NULL,
                    size INTEGER NOT NULL, sha256 TEXT NOT NULL, stored_name TEXT NOT NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        using (var store = new SqliteNoteStore(path))
        {
            using var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
            check.Open();
            using var cmd = check.CreateCommand();
            cmd.CommandText = "SELECT value FROM keyvalue WHERE key='schema_version';";
            Assert.AreEqual("7", cmd.ExecuteScalar() as string);
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('files','file_syncstate');";
            var tables = new List<string>();
            using (var r = cmd.ExecuteReader())
                while (r.Read()) tables.Add(r.GetString(0));
            CollectionAssert.AreEquivalent(new[] { "files", "file_syncstate" }, tables);
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name='idx_files_sha';";
            Assert.IsNotNull(cmd.ExecuteScalar());
            // Колонки files по контракту.
            cmd.CommandText = "PRAGMA table_info(files);";
            var cols = new List<string>();
            using (var r = cmd.ExecuteReader())
                while (r.Read()) cols.Add(r.GetString(1));
            CollectionAssert.AreEquivalent(
                new[] { "id", "name", "mime", "size", "sha256", "stored_name", "rev", "updated_at", "author_device", "is_deleted" },
                cols);
        }
    }

    [TestMethod]
    public void Migrate_V6_to_V7_KeepsNotesData()
    {
        var path = TempDb();
        var noteId = Guid.NewGuid().ToString("N");
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE keyvalue(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO keyvalue(key, value) VALUES('schema_version', '6');
                CREATE TABLE notes(
                    id TEXT PRIMARY KEY, rev INTEGER NOT NULL,
                    title TEXT NOT NULL, body TEXT NOT NULL,
                    updated_at TEXT NOT NULL, author_device TEXT NOT NULL,
                    is_deleted INTEGER NOT NULL DEFAULT 0,
                    title_norm TEXT NOT NULL DEFAULT '', body_norm TEXT NOT NULL DEFAULT '');
                CREATE TABLE attachments(
                    id TEXT PRIMARY KEY, note_id TEXT NOT NULL REFERENCES notes(id),
                    file_name TEXT NOT NULL, mime TEXT NOT NULL,
                    size INTEGER NOT NULL, sha256 TEXT NOT NULL, stored_name TEXT NOT NULL);
                """;
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO notes(id, rev, title, body, updated_at, author_device, is_deleted, title_norm, body_norm) " +
                "VALUES($id, 3, 'Живая', 'важная', '2026-01-01T00:00:00Z', 'dev1', 0, 'живая', 'важная');";
            cmd.Parameters.AddWithValue("$id", noteId);
            cmd.ExecuteNonQuery();
        }

        using (var store = new SqliteNoteStore(path))
        {
            var note = store.TryGet(Guid.Parse(noteId));
            Assert.IsNotNull(note);
            Assert.AreEqual("Живая", note!.Title);
            Assert.AreEqual(3, note.Rev);
            Assert.AreEqual(1, store.Search("живая").Count);
            // Старые таблицы не тронуты миграцией: поиск и чтение работают.
        }
    }
}
