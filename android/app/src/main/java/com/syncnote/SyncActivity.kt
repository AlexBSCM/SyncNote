package com.syncnote

import android.content.Intent
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
    companion object {
        const val ACTION_SYNC_NOW = "com.syncnote.action.SYNC_NOW"
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_TOKEN = "token"
    }
    private lateinit var repo: NotesRepository
    private lateinit var p2p: P2pConnector
    private var peers: List<WifiP2pDevice> = emptyList()

    private val qrLauncher = registerForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.StartActivityForResult()) { res ->
        if (res.resultCode == RESULT_OK && res.data != null) {
            val host = res.data!!.getStringExtra(QrScanActivity.EXTRA_HOST) ?: ""
            val port = res.data!!.getIntExtra(QrScanActivity.EXTRA_PORT, 0)
            findViewById<EditText>(R.id.hostBox).setText(host)
            findViewById<EditText>(R.id.portBox).setText(port.toString())
            findViewById<EditText>(R.id.tokenBox).setText(
                res.data!!.getStringExtra(QrScanActivity.EXTRA_TOKEN))
            pendingHosts = res.data!!.getStringArrayListExtra(QrScanActivity.EXTRA_HOSTS)
                ?.filter { it.isNotBlank() } ?: listOf(host)
            if (host.isNotBlank() && port > 0)
                SyncAuto.saveProfile(this, host, port, pendingHosts)
            setState("QR принят. Нажмите «Синхронизировать».")
        }
    }

    private var pendingHosts: List<String> = emptyList()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_sync)
        repo = NotesRepository(this)
        p2p = P2pConnector(this)
        p2p.listener = this
        p2p.start()

        findViewById<Button>(R.id.findButton).setOnClickListener { ensureP2pPermissionAndDiscover() }
        findViewById<Button>(R.id.syncButton).setOnClickListener { runSync() }
        findViewById<Button>(R.id.scanButton).setOnClickListener {
            qrLauncher.launch(Intent(this, QrScanActivity::class.java))
        }
        findViewById<Button>(R.id.btButton).setOnClickListener { pickBluetoothDevice() }
        findViewById<ListView>(R.id.peersList)?.setOnItemClickListener { _, _, pos, _ ->
            p2p.connect(peers[pos])
        }
        SyncAuto.profile(this)?.let { (host, port) ->
            findViewById<EditText>(R.id.hostBox).setText(host)
            findViewById<EditText>(R.id.portBox).setText(port.toString())
            setState("Узел запомнен. Токен нужен только один раз.")
        }
        // Автотесты железа: am start -a com.syncnote.action.SYNC_NOW
        // [--es host H --ei port P --es token T] — полный цикл без taps.
        handleSyncIntent(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleSyncIntent(intent)
    }

    private fun handleSyncIntent(intent: Intent?) {
        if (intent?.action != ACTION_SYNC_NOW) return
        intent.getStringExtra(EXTRA_HOST)?.let {
            findViewById<EditText>(R.id.hostBox).setText(it)
        }
        val p = intent.getIntExtra(EXTRA_PORT, 0)
        if (p > 0) findViewById<EditText>(R.id.portBox).setText(p.toString())
        intent.getStringExtra(EXTRA_TOKEN)?.let {
            findViewById<EditText>(R.id.tokenBox).setText(it)
        }
        findViewById<Button>(R.id.syncButton).post { runSync() }
    }

    override fun onDestroy() {
        p2p.stop()
        super.onDestroy()
    }

    private fun setState(t: String) = runOnUiThread {
        findViewById<TextView>(R.id.stateText).text = t
    }

    private val p2pPermissionLauncher = registerForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.RequestMultiplePermissions()
    ) { grants ->
        if (grants.values.all { it }) p2p.discover()
        else setState("Без разрешений поиск невозможен. Включите Wi-Fi и геолокацию.")
    }

    private fun ensureP2pPermissionAndDiscover() {
        val perms = if (android.os.Build.VERSION.SDK_INT >= 33)
            arrayOf(android.Manifest.permission.NEARBY_WIFI_DEVICES)
        else
            arrayOf(android.Manifest.permission.ACCESS_FINE_LOCATION)
        val missing = perms.filter {
            androidx.core.content.ContextCompat.checkSelfPermission(this, it) !=
                android.content.pm.PackageManager.PERMISSION_GRANTED
        }
        if (missing.isEmpty()) p2p.discover()
        else p2pPermissionLauncher.launch(missing.toTypedArray())
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
                val hosts = (pendingHosts + host).distinct().filter { it.isNotBlank() }
                val r = session.tryHosts(repo, cacheDir, hosts)
                android.util.Log.i("SyncNote",
                    "sync done pushed=${r.pushed} pulled=${r.pulled} conflicts=${r.conflicts}")
                SyncAuto.saveProfile(this@SyncActivity, host, port, hosts)
                runOnUiThread {
                    // Токен одноразовый: устройство теперь доверенное.
                    findViewById<EditText>(R.id.tokenBox).text.clear()
                }
                setState("Готово: отправлено ${r.pushed}, получено ${r.pulled}, " +
                    "конфликтов ${r.conflicts}. Узел запомнен.")
            } catch (e: HelloRejectedException) {
                android.util.Log.i("SyncNote", "sync rejected: ${e.message}")
                setState("Ошибка: ${e.message}")
            } catch (e: Throwable) {
                android.util.Log.i("SyncNote",
                    "sync error ${e.javaClass.simpleName}: ${e.message}")
                setState("Ошибка: ${e.javaClass.simpleName}: ${e.message}")
            }
        }.start()
    }

    private fun pickBluetoothDevice() {
        val devices = BluetoothConnector.bonded(this)
        if (devices.isEmpty()) {
            setState("Нет спаренных устройств. Спарьте ПК в настройках Bluetooth.")
            return
        }
        val names = devices.map { "${it.name} (${it.address})" }.toTypedArray()
        androidx.appcompat.app.AlertDialog.Builder(this)
            .setTitle("ПК для синхронизации")
            .setItems(names) { _, which -> runBtSync(devices[which]) }
            .show()
    }

    private fun runBtSync(device: android.bluetooth.BluetoothDevice) {
        val token = findViewById<EditText>(R.id.tokenBox).text.toString()
            .trim().ifEmpty { null }
        setState("Подключаемся по Bluetooth к ${device.name}…")
        Thread {
            try {
                val (inp, out) = BluetoothConnector.connect(device)
                inp.use {
                    out.use {
                        val session = SyncSession("bt", 0, repo.deviceId(),
                            android.os.Build.MODEL, token)
                        val r = session.runOverStreams(repo, inp, out)
                        setState("Готово по Bluetooth: отправлено ${r.pushed}, " +
                            "получено ${r.pulled}, конфликтов ${r.conflicts}.")
                    }
                }
            } catch (e: HelloRejectedException) {
                setState("Ошибка: ${e.message}")
            } catch (e: Exception) {
                setState("Ошибка Bluetooth: ${e.message}")
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
