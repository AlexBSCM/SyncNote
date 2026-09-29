package com.syncnote.v2.data

// Разбиение SQL-скрипта на команды. Чистый Kotlin без Android-зависимостей
// (покрыт JVM-тестом SplitterTest). Зеркало C# SyncNote.Core.Utils.SqlSplitter:
// алгоритм обязан совпадать — обе платформы исполняют один docs/schema.sql.
object SqlSplitter {
    fun split(sql: String): List<String> {
        val out = mutableListOf<String>()
        val cur = StringBuilder()
        var inString = false
        var i = 0
        fun flush() {
            val s = cur.toString().trim()
            cur.clear()
            if (s.isNotEmpty()) out += s
        }
        while (i < sql.length) {
            val c = sql[i]
            if (!inString && c == '-' && i + 1 < sql.length && sql[i + 1] == '-') {
                while (i < sql.length && sql[i] != '\n') i++
                continue
            }
            if (c == '\'') {
                if (inString && i + 1 < sql.length && sql[i + 1] == '\'') {
                    cur.append("''")
                    i += 2
                    continue
                }
                inString = !inString
            }
            if (c == ';' && !inString) {
                flush()
                i++
                continue
            }
            cur.append(c)
            i++
        }
        flush()
        return out
    }
}
