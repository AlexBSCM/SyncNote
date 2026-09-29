package com.syncnote.v2.data

import org.junit.Assert.*
import org.junit.Test

// JVM-тест чистого сплиттера (без Android-рантайма).
class SplitterTest {
    @Test fun splitsSchemaIntoStatements() {
        val sql = """
            -- comment line
            CREATE TABLE t(a TEXT);

            INSERT INTO t(a) VALUES ('x;y');
            INSERT INTO t(a) VALUES ('it''s');
        """.trimIndent()
        val parts = SqlSplitter.split(sql)
        assertEquals(3, parts.size)
        assertTrue(parts[0].startsWith("CREATE TABLE"))
        assertEquals("INSERT INTO t(a) VALUES ('x;y')", parts[1])
        assertEquals("INSERT INTO t(a) VALUES ('it''s')", parts[2])
    }

    @Test fun emptyAndCommentsOnly_yieldNothing() {
        assertTrue(SqlSplitter.split("-- only a comment\n  \n").isEmpty())
        assertTrue(SqlSplitter.split("").isEmpty())
    }
}
