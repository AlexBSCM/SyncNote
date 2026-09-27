using SyncNote.Core;

namespace SyncNote.Core.Tests;

[TestClass]
public sealed class SyncJsonTests
{
    [TestMethod]
    public void Roundtrip_PreservesFields()
    {
        var dto = new SyncNoteDto
        {
            Id = Guid.NewGuid(),
            Rev = 3,
            BaseRev = 2,
            Title = "Заг",
            Body = "a **b**",
            UpdatedAt = new DateTime(2026, 9, 27, 8, 0, 0, DateTimeKind.Utc),
            Author = "dev1",
            Checklist = { new ChecklistItemDto { Position = 0, Text = "п", IsChecked = true } },
            Attachments = { new AttachmentMetaDto
            {
                FileName = "f.bin", MimeType = "application/octet-stream",
                SizeBytes = 4, Sha256 = "abc",
            } },
        };
        var back = SyncJson.FromNode(SyncJson.ToNode(dto));
        Assert.AreEqual(dto.Id, back.Id);
        Assert.AreEqual(3, back.Rev);
        Assert.AreEqual(2, back.BaseRev);
        Assert.AreEqual("Заг", back.Title);
        Assert.AreEqual(dto.UpdatedAt, back.UpdatedAt);
        Assert.AreEqual(1, back.Checklist.Count);
        Assert.IsTrue(back.Checklist[0].IsChecked);
        Assert.AreEqual("abc", back.Attachments[0].Sha256);
    }

    [TestMethod]
    public void FromNode_AcceptsIsoStringTime()
    {
        var node = SyncJson.ToNode(new SyncNoteDto
        {
            Id = Guid.NewGuid(),
            UpdatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        });
        node["UpdatedAt"] = "2026-01-02T03:04:05Z";
        var back = SyncJson.FromNode(node);
        Assert.AreEqual(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), back.UpdatedAt);
    }
}
