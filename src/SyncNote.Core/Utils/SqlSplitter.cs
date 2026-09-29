using System.Collections.Generic;

namespace SyncNote.Core.Utils;

// Разбиение SQL-скрипта на отдельные команды.
// Зеркало Kotlin-функции splitSqlStatements (android, data/SqlSplitter.kt):
// алгоритм обязан совпадать — обе платформы исполняют один docs/schema.sql.
// Правила: full-line комментарии `--` и пустые строки пропускаются;
// разделитель `;` вне строковых литералов; внутри литералов '' — escape.
public static class SqlSplitter
{
    public static IReadOnlyList<string> Split(string sql)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inString = false;
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            // Комментарий до конца строки (вне литерала).
            if (!inString && c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                    i++;
                continue;
            }
            if (c == '\'')
            {
                // Удвоенная кавычка внутри литерала — escape, не граница.
                if (inString && i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    current.Append("''");
                    i += 2;
                    continue;
                }
                inString = !inString;
            }
            if (c == ';' && !inString)
            {
                Flush(current, result);
                i++;
                continue;
            }
            current.Append(c);
            i++;
        }
        Flush(current, result);
        return result;
    }

    private static void Flush(System.Text.StringBuilder sb, List<string> out_)
    {
        string s = sb.ToString().Trim();
        sb.Clear();
        if (s.Length > 0)
            out_.Add(s);
    }
}
