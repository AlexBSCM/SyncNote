namespace SyncNote.Core;

// Мини-разметка тела заметки: **жирный** и *курсив*, без вложенности.
// Непарный маркер — обычный текст. Общий формат для Windows и Android.
public sealed record Segment(string Text, bool Bold, bool Italic);

public static class TextMarkup
{
    public static IReadOnlyList<Segment> Parse(string body)
    {
        // 1. Собираем позиции маркеров: "**" приоритетом, одиночные "*" —
        // только вне "**". 2. Спариваем по порядку; непарный остаток — текст.
        var markers = new List<(int Pos, int Len, bool Bold)>();
        int i = 0;
        while (i < body.Length)
        {
            if (body[i] == '*' && i + 1 < body.Length && body[i + 1] == '*')
            {
                markers.Add((i, 2, true));
                i += 2;
            }
            else if (body[i] == '*')
            {
                markers.Add((i, 1, false));
                i += 1;
            }
            else
            {
                i += 1;
            }
        }
        var paired = new HashSet<int>();
        for (int b = 0; b + 1 < markers.Count; b += 2)
        {
            // Паруем только одинаковые маркеры (** с **, * с *).
            if (markers[b].Bold == markers[b + 1].Bold)
            {
                paired.Add(b);
                paired.Add(b + 1);
            }
        }

        var result = new List<Segment>();
        var current = new System.Text.StringBuilder();
        bool bold = false, italic = false;
        int mi = 0, pos = 0;
        void Flush()
        {
            if (current.Length > 0)
            {
                result.Add(new Segment(current.ToString(), bold, italic));
                current.Clear();
            }
        }
        while (pos < body.Length)
        {
            var next = mi < markers.Count && paired.Contains(mi)
                ? markers[mi] : default((int Pos, int Len, bool Bold)?);
            if (next is { } m && m.Pos == pos)
            {
                Flush();
                if (m.Bold) bold = !bold; else italic = !italic;
                pos += m.Len;
                mi += 1;
            }
            else if (mi < markers.Count && markers[mi].Pos == pos)
            {
                // Непарный маркер — буквальный текст.
                current.Append(body.Substring(pos, markers[mi].Len));
                pos += markers[mi].Len;
                mi += 1;
            }
            else
            {
                current.Append(body[pos]);
                pos += 1;
            }
        }
        Flush();
        return result;
    }
}
