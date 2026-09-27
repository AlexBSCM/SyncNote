using SyncNote.Core;

namespace SyncNote.Core.Tests;

[TestClass]
public sealed class NoteStoreTests
{
    [TestMethod]
    public void Add_List_ReturnsNewestFirst()
    {
        var store = new InMemoryNoteStore();
        store.Add("Первая", "текст 1");
        store.Add("Вторая", "текст 2");

        var list = store.List();

        Assert.AreEqual(2, list.Count);
        Assert.AreEqual("Вторая", list[0].Title);
    }

    [TestMethod]
    public void Search_FindsByTitleAndBody_CaseInsensitive()
    {
        var store = new InMemoryNoteStore();
        store.Add("Покупки", "молоко");
        store.Add("Работа", "отчёт");

        Assert.AreEqual(1, store.Search("покуп").Count);
        Assert.AreEqual(1, store.Search("ОТЧЁТ").Count);
        Assert.AreEqual(2, store.Search("").Count);
        Assert.AreEqual(0, store.Search("нет-такого").Count);
    }

    [TestMethod]
    public void Update_ChangesTitleAndBody()
    {
        var store = new InMemoryNoteStore();
        var note = store.Add("Старый", "тело");

        note.Title = "Новый";
        store.Update(note);

        Assert.AreEqual("Новый", store.List()[0].Title);
    }

    [TestMethod]
    public void Delete_RemovesExisting_ReturnsFalseForMissing()
    {
        var store = new InMemoryNoteStore();
        var note = store.Add("Удалить", "тело");

        Assert.IsTrue(store.Delete(note.Id));
        Assert.AreEqual(0, store.List().Count);
        Assert.IsFalse(store.Delete(Guid.NewGuid()));
    }
}
