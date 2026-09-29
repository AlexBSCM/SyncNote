package com.syncnote.v2.data

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream

// JVM-тест чистого хеширования (без Android-рантайма).
class HashTest {
    @Test fun sha256_matchesKnownVector() {
        // SHA256("abc") — стандартный вектор.
        val expect = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        assertEquals(expect, HashUtils.sha256Hex("abc".toByteArray()))
        assertEquals(expect, HashUtils.sha256Hex(ByteArrayInputStream("abc".toByteArray())))
    }
}
