package com.syncnote

import android.net.wifi.p2p.WifiP2pDevice
import android.os.Bundle
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.ListView
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity

// Экран синхронизации: ручной ввод узла/токена (петля, GO-адрес)
// + поиск ПК по Wi-Fi Direct (нужно железо). Состояния как в protocol/.
class SyncActivity : AppCompatActivity(), P2pConnector.Listener {
    private lateinit var repo: NotesRepository
    private lateinit var p2p: P2pConnector
    private var peers: List<WifiP2pDevice> = emptyList()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_sync)
        repo = NotesRepository(this)
        p2p = P2pConnector(this)
        p2p.listener = this
        p2p.start()

        findViewById<Button>(R.id.findButton).setOnClickListener { p2p.discover() }
        findViewById<Button>(R.id.syncButton).setOnClickListener { runSync() }
        findViewById<ListView>(R.id.peersList)?.setOnItemClickListener { _, _, pos, _ ->
            p2p.connect(peers[pos])
        }
    }

    override fun onDestroy() {
        p2p.stop()
        super.onDestroy()
    }

    private fun setState(t: String) = runOnUiThread {
        findViewById<TextView>(R.id.stateText).text = t
    }

    private fun runSync() {
        val host = findViewById<EditText>(R.id.hostBox).text.toString().trim()
        val port = findViewById<EditText>(R.id.portBox).text.toString().toIntOrNull()
        if (host.isEmpty() || port == null) {
            setState("Ошибка: укажите узел и порт.")
            return
        }
        val token = findViewById<EditText>(R.id.tokenBox).text.toString()
            .trim().ifEmpty { null }
        setState("Синхронизируется…")
        Thread {
            try {
                val session = SyncSession(host, port, repo.deviceId(),
                    android.os.Build.MODEL, token)
                val r = session.run(repo)
                setState("Готово: отправлено ${r.pushed}, получено ${r.pulled}, " +
                    "конфликтов ${r.conflicts}.")
            } catch (e: HelloRejectedException) {
                setState("Ошибка: ${e.message}")
            } catch (e: Exception) {
                setState("Ошибка: ${e.message}")
            }
        }.start()
    }

    // P2pConnector.Listener
    override fun onState(text: String) = setState(text)

    override fun onPeers(devices: List<WifiP2pDevice>) {
        peers = devices
        runOnUiThread {
            findViewById<TextView>(R.id.peersInfo).text =
                if (devices.isEmpty()) "Устройств рядом не найдено."
                else "Нажмите на устройство для подключения:"
            findViewById<ListView>(R.id.peersList).adapter = ArrayAdapter(
                this, android.R.layout.simple_list_item_1,
                devices.map { "${it.deviceName} (${it.status})" })
        }
    }

    override fun onGroupOwner(address: String) {
        runOnUiThread {
            findViewById<EditText>(R.id.hostBox).setText(address)
            setState("Подключено к GO $address. Введите токен из QR и синхронизируйте.")
        }
    }

    override fun onError(reason: String) = setState("Ошибка: $reason")
}
