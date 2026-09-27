using SyncNote.Core;

namespace SyncNote.Core.Tests;

[TestClass]
public sealed class TextMarkupTests
{
    private static string Show(IReadOnlyList<Segment> segs) =>
        string.Join("|", segs.Select(s =>
            $"{(s.Bold ? "B" : "")}{(s.Italic ? "I" : "")}:{s.Text}"));

    [TestMethod]
    public void PlainText_IsSingleSegment() =>
        Assert.AreEqual(":hello", Show(TextMarkup.Parse("hello")));

    [TestMethod]
    public void Bold_Works() =>
        Assert.AreEqual(":a |B:b|: c", Show(TextMarkup.Parse("a **b** c")));

    [TestMethod]
    public void Italic_Works() =>
        Assert.AreEqual(":a |I:b|: c", Show(TextMarkup.Parse("a *b* c")));

    [TestMethod]
    public void UnpairedMarker_IsLiteral()
    {
        Assert.AreEqual(":a **b", Show(TextMarkup.Parse("a **b")));
        Assert.AreEqual(":a *b", Show(TextMarkup.Parse("a *b")));
    }

    [TestMethod]
    public void MixedKinds_DoNotPair() =>
        Assert.AreEqual(":a **b*", Show(TextMarkup.Parse("a **b*")));

    [TestMethod]
    public void Empty_IsEmpty() =>
        Assert.AreEqual(0, TextMarkup.Parse(string.Empty).Count);
}
