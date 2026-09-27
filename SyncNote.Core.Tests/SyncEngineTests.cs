using SyncNote.Core;

namespace SyncNote.Core.Tests;

[TestClass]
public sealed class SyncEngineTests
{
    private static (InMemoryNoteStore A, InMemoryNoteStore B) TwoDevices()
    {
        var a = new InMemoryNoteStore();
        var b = new InMemoryNoteStore();
        return (a, b);
    }

    private static void SyncAll(InMemoryNoteStore from, InMemoryNoteStore to)
    {
        foreach (var dto in from.Export())
            to.Apply(dto);
    }

    [TestMethod]
    public void Insert_Then_Reapply_IsNoOp()
    {
        var (a, b) = TwoDevices();
        var note = a.Add("Привет", "мир");
        a.AddChecklistItem(note.Id, "пункт");

        var dto = a.Export()[0];
        var (r1, _) = b.Apply(dto);
        Assert.AreEqual(ApplyResult.Inserted, r1);
        Assert.AreEqual("Привет", b.List()[0].Title);
        Assert.AreEqual(1, b.GetChecklist(note.Id).Count);

        var (r2, _) = b.Apply(dto);
        Assert.AreEqual(ApplyResult.NoOp, r2);
    }

    [TestMethod]
    public void SequentialEdits_FastForward()
    {
        var (a, b) = TwoDevices();
        var note = a.Add("Заг", "тело");
        SyncAll(a, b);

        var nb = b.TryGet(note.Id)!;
        nb.Title = "Заг v2";
        b.Update(nb);
        var dto = b.Export().First(d => d.Id == note.Id);

        var (r, _) = a.Apply(dto);
        Assert.AreEqual(ApplyResult.FastForwarded, r);
        Assert.AreEqual("Заг v2", a.TryGet(note.Id)!.Title);
    }

    [TestMethod]
    public void DivergentEdits_KeepBoth()    {
        var (a, b) = TwoDevices();
        var note = a.Add("Общая", "база");
        SyncAll(a, b);

        var na = a.TryGet(note.Id)!;
        na.Title = "Версия A";
        a.Update(na);

        var nb = b.TryGet(note.Id)!;
        nb.Title = "Версия B";
        b.Update(nb);

        var dtoA = a.Export().First(d => d.Id == note.Id);
        var (r, conflict) = b.Apply(dtoA);

        Assert.AreEqual(ApplyResult.Conflict, r);
        Assert.IsNotNull(conflict);
        Assert.AreEqual("Версия B", b.TryGet(note.Id)!.Title); // локальное цело
        var copy = b.TryGet(conflict.CopyId);
        Assert.IsNotNull(copy);
        Assert.AreEqual("Версия A", copy.Title.Replace(" (копия конфликта)", ""));
        Assert.AreEqual(2, b.List().Count); // обе видимы
    }

    [TestMethod]
    public void FindPairs_MatchesCopyWithOriginal()
    {
        var store = new InMemoryNoteStore();
        var orig = store.Add("Документ", "v1");
        store.Add("Другое", "x");
        // Имитация копии от SyncEngine.
        var copy = new SyncNoteDto
        {
            Id = Guid.NewGuid(),
            Rev = 1,
            Title = "Документ" + Conflicts.CopySuffix,
            Body = "v2",
            UpdatedAt = DateTime.UtcNow,
        };
        store.ImportFull(copy, _ => null);

        var pairs = Conflicts.FindPairs(store);
        Assert.AreEqual(1, pairs.Count);
        Assert.AreEqual(orig.Id, pairs[0].Original.Id);
        Assert.AreEqual("Документ" + Conflicts.CopySuffix, pairs[0].Copy.Title);
    }

    [TestMethod]
    public void Delete_Propagates_AsTombstone()
    {
        var (a, b) = TwoDevices();
        var note = a.Add("Удалить", "меня");
        SyncAll(a, b);
        Assert.AreEqual(1, b.List().Count);

        a.Delete(note.Id);
        var dto = a.Export().First(d => d.Id == note.Id);
        Assert.IsTrue(dto.IsDeleted);

        var (r, _) = b.Apply(dto);
        Assert.AreEqual(ApplyResult.FastForwarded, r);
        Assert.AreEqual(0, b.List().Count);
        Assert.IsNotNull(b.TryGet(note.Id)); // tombstone сохранён
    }

    [TestMethod]
    public void Attachments_Transfer_WithBytes()
    {
        var (a, b) = TwoDevices();
        var note = a.Add("С файлом", "тело");
        var src = Path.Combine(Path.GetTempPath(), $"syncnote-eng-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(src, new byte[] { 1, 2, 3, 4 });
        try
        {
            var att = a.AddAttachment(note.Id, src);
            var dto = a.Export().First(d => d.Id == note.Id);
            Assert.AreEqual(1, dto.Attachments.Count);

            var files = new Dictionary<string, byte[]>
            {
                [att.Sha256] = File.ReadAllBytes(Path.Combine(a.FilesDirectory, att.StoredName)),
            };
            var (r, _) = b.Apply(dto, sha => files.TryGetValue(sha, out var v) ? v : null);
            Assert.AreEqual(ApplyResult.Inserted, r);
            var got = b.GetAttachments(note.Id);
            Assert.AreEqual(1, got.Count);
            CollectionAssert.AreEqual(
                new byte[] { 1, 2, 3, 4 },
                File.ReadAllBytes(Path.Combine(b.FilesDirectory, got[0].StoredName)));
        }
        finally { File.Delete(src); }
    }

    [TestMethod]
    public void Conflict_Retry_DoesNotDuplicate()
    {
        var (a, b) = TwoDevices();
        var note = a.Add("Общая", "база");
        SyncAll(a, b);

        var na = a.TryGet(note.Id)!;
        na.Title = "Версия A";
        a.Update(na);
        var nb = b.TryGet(note.Id)!;
        nb.Title = "Версия B";
        b.Update(nb);

        var dtoA = a.Export().First(d => d.Id == note.Id);
        var (r1, c1) = b.Apply(dtoA);
        Assert.AreEqual(ApplyResult.Conflict, r1);
        Assert.IsNotNull(c1);
        Assert.AreEqual(2, b.List().Count);

        // Повтор той же версии — без новой копии.
        var (r2, c2) = b.Apply(dtoA);
        Assert.AreEqual(ApplyResult.NoOp, r2);
        Assert.IsNull(c2);
        Assert.AreEqual(2, b.List().Count);
    }
}
