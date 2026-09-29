package com.syncnote.v2.domain

import org.junit.Assert.*
import org.junit.Test
import org.json.JSONObject

class JsonProbeTest {
    @Test fun probe() {
        val o = JSONObject("""{"t":"hello"}""")
        println("PROBE type=" + o.optString("t", "?"))
        assertEquals("hello", o.optString("t"))
    }
}
