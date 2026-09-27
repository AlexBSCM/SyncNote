namespace SyncNote.Core;

// Пары «оригинал — копия конфликта». Копии создаёт SyncEngine
// с суффиксом CopySuffix; обе версии остаются видимыми до решения.
public static class Conflicts
{
    public const string CopySuffix = " (копия конфликта)";

    public static IReadOnlyList<(Note Original, Note Copy)> FindPairs(INoteStore store)
    {
        var notes = store.List();
        var result = new List<(Note, Note)>();
        foreach (var copy in notes.Where(n => n.Title.EndsWith(CopySuffix)))
        {
            var baseTitle = copy.Title[..^CopySuffix.Length];
            var original = notes.FirstOrDefault(n =>
                !n.Title.EndsWith(CopySuffix) && n.Title == baseTitle);
            if (original is not null)
                result.Add((original, copy));
        }
        return result;
    }
}
