using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SyncNote.Windows.Services;

// Мини-рендерер Markdown-подмножества в FlowDocument (без сторонних пакетов):
// заголовки #, жирный **, курсив *, код `, списки -, чек-листы - [ ]/- [x],
// ссылки [text](url). Неподдержанная разметка показывается как есть.
public static class MarkdownPreview
{
    private static Brush Res(string key, Brush fallback)
    {
        try
        {
            var app = Application.Current;
            if (app?.Resources[key] is Brush b)
                return b;
        }
        catch { }
        return fallback;
    }

    public static FlowDocument ToFlowDocument(string markdown, Action<string>? onLink = null)
    {
        var doc = new FlowDocument();
        doc.FontSize = 14;
        doc.Foreground = Res("TextPrimaryBrush", Brushes.Black);
        if (string.IsNullOrEmpty(markdown))
            return doc;

        Paragraph? current = null;
        foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine;
            if (line.Trim().Length == 0)
            {
                current = null; // пустая строка — разрыв абзаца
                continue;
            }

            if (TryHeader(line, out var header))
            {
                doc.Blocks.Add(header!);
                current = null;
                continue;
            }

            if (TryListItem(line, out var prefix, out var rest))
            {
                var p = new Paragraph();
                p.Margin = new Thickness(0, 0, 0, 2);
                p.Inlines.Add(new Run(prefix));
                AddInline(p.Inlines, rest, onLink);
                doc.Blocks.Add(p);
                current = null;
                continue;
            }

            if (current == null)
            {
                current = new Paragraph();
                current.Margin = new Thickness(0, 0, 0, 8);
                doc.Blocks.Add(current);
            }
            else
            {
                current.Inlines.Add(new LineBreak());
            }
            AddInline(current.Inlines, line, onLink);
        }
        return doc;
    }

    private static bool TryHeader(string line, out Paragraph? p)
    {
        p = null;
        int level = 0;
        while (level < line.Length && line[level] == '#')
            level++;
        if (level == 0 || level > 3 || level >= line.Length || line[level] != ' ')
            return false;
        p = new Paragraph();
        p.Margin = new Thickness(0, level == 1 ? 8 : 6, 0, 4);
        var run = new Run(line.Substring(level + 1).Trim());
        run.FontWeight = FontWeights.Bold;
        run.FontSize = level == 1 ? 20 : level == 2 ? 17 : 15;
        p.Inlines.Add(run);
        return true;
    }

    private static bool TryListItem(string line, out string prefix, out string rest)
    {
        prefix = "";
        rest = line;
        var t = line.TrimStart();
        if (t.StartsWith("- [ ] "))
        {
            prefix = "☐ ";
            rest = t.Substring(6);
            return true;
        }
        if (t.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "☑ ";
            rest = t.Substring(6);
            return true;
        }
        if (t.StartsWith("- ") || t.StartsWith("* "))
        {
            prefix = "• ";
            rest = t.Substring(2);
            return true;
        }
        return false;
    }

    // Строчная разметка: код `, жирный **, курсив *, ссылки [t](u).
    private static void AddInline(InlineCollection into, string text, Action<string>? onLink)
    {
        int i = 0;
        var plain = new System.Text.StringBuilder();
        void flush()
        {
            if (plain.Length > 0)
            {
                into.Add(new Run(plain.ToString()));
                plain.Clear();
            }
        }
        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    flush();
                    var code = new Run(text.Substring(i + 1, end - i - 1));
                    code.FontFamily = new FontFamily("Consolas, Courier New");
                    code.Background = Res("BgSecondaryBrush", Brushes.LightGray);
                    into.Add(code);
                    i = end + 1;
                    continue;
                }
            }
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    flush();
                    var bold = new Bold(new Run(text.Substring(i + 2, end - i - 2)));
                    into.Add(bold);
                    i = end + 2;
                    continue;
                }
            }
            if (text[i] == '[')
            {
                int mid = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                if (mid > i)
                {
                    int end = text.IndexOf(')', mid + 2);
                    if (end > mid)
                    {
                        flush();
                        var label = text.Substring(i + 1, mid - i - 1);
                        var url = text.Substring(mid + 2, end - mid - 2);
                        var link = new Hyperlink(new Run(label));
                        if (onLink != null)
                        {
                            link.Tag = url;
                            link.RequestNavigate += (_, e) =>
                            {
                                onLink((string)((Hyperlink)e.Source).Tag);
                                e.Handled = true;
                            };
                            link.Cursor = System.Windows.Input.Cursors.Hand;
                        }
                        into.Add(link);
                        i = end + 1;
                        continue;
                    }
                }
            }
            if (text[i] == '*' && (i == 0 || text[i - 1] != '*'))
            {
                int end = text.IndexOf('*', i + 1);
                if (end > i && !(end + 1 < text.Length && text[end + 1] == '*'))
                {
                    flush();
                    into.Add(new Italic(new Run(text.Substring(i + 1, end - i - 1))));
                    i = end + 1;
                    continue;
                }
            }
            plain.Append(text[i]);
            i++;
        }
        flush();
    }
}
