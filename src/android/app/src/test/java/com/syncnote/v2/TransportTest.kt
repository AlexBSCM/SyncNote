package com.syncnote.v2.network

import org.junit.Assert.*
import org.junit.Test
import java.net.ServerSocket

// JVM-тесты транспорта: обмен, усечение, большой кадр. Без Android-рантайма.
class TransportTest {
    private fun pair(): Pair<AndroidTcpTransport, AndroidTcpTransport> {
        val server = ServerSocket(0)
        val port = server.localPort
        var accepted: AndroidTcpTransport? = null
        val t = Thread { accepted = AndroidTcpTransport.wrap(server.accept()) }
        t.isDaemon = true
        t.start()
        val client = AndroidTcpTransport.connect("127.0.0.1", port)
        t.join(10000)
        server.close()
        return client to accepted!!
    }

    @Test fun helloAck_roundTrip() {
        val (client, server) = pair()
        try {
            client.send("""{"t":"hello","deviceId":"a"}""")
            assertEquals("""{"t":"hello","deviceId":"a"}""", server.receive())
            server.send("""{"t":"hello_ok"}""")
            assertEquals("""{"t":"hello_ok"}""", client.receive())
        } finally {
            client.close()
            server.close()
        }
    }

    @Test fun truncatedHeader_throwsNotHangs() {
        val server = ServerSocket(0)
        val port = server.localPort
        val t = Thread {
            val s = server.accept()
            s.getOutputStream().write(byteArrayOf(0x00, 0x01)) // 2 из 4 байт
            s.close()
        }
        t.isDaemon = true
        t.start()
        val client = AndroidTcpTransport.connect("127.0.0.1", port)
        try {
            client.receive()
            fail("expected SyncNetworkException")
        } catch (e: SyncNetworkException) {
            assertTrue(e.message!!.contains("усеч"))
        } finally {
            client.close()
            server.close()
        }
    }

    @Test fun largePayload_reassembled() {
        val (client, server) = pair()
        try {
            val big = """{"t":"x","d":"${"0123456789".repeat(20000)}"}""" // ~200 КБ
            client.send(big)
            assertEquals(big, server.receive())
        } finally {
            client.close()
            server.close()
        }
    }

    @Test fun cleanClose_returnsNull() {
        val server = ServerSocket(0)
        val port = server.localPort
        val t = Thread { server.accept().close() }
        t.isDaemon = true
        t.start()
        val client = AndroidTcpTransport.connect("127.0.0.1", port)
        try {
            assertNull(client.receive())
        } finally {
            client.close()
            server.close()
        }
    }
}
