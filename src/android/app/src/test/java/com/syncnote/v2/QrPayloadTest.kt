package com.syncnote.v2.domain

import org.junit.Assert.*
import org.junit.Test

class QrPayloadTest {
    @Test fun valid_parses() {
        val p = QrPayload.parse("""{"ip":"192.168.1.5","port":48211,"token":"abc"}""")
        assertNotNull(p)
        assertEquals("192.168.1.5", p!!.ip)
        assertEquals(48211, p.port)
        assertEquals("abc", p.token)
    }

    @Test fun garbage_rejected() {
        assertNull(QrPayload.parse("not json"))
        assertNull(QrPayload.parse("{}"))
        assertNull(QrPayload.parse("""{"ip":"","port":48211,"token":"x"}"""))
        assertNull(QrPayload.parse("""{"ip":"h","port":0,"token":"x"}"""))
        assertNull(QrPayload.parse("""{"ip":"h","port":70000,"token":"x"}"""))
        assertNull(QrPayload.parse("""{"ip":"h","port":48211,"token":""}"""))
    }
}
