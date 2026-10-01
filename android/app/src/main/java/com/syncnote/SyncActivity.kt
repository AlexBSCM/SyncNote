package com.syncnote

import android.content.Context
import android.content.Intent
import android.content.res.ColorStateList
import android.net.wifi.p2p.WifiP2pDevice
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.ListView
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import com.syncnote.databinding.ItemPeerRowBinding

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

    // Бэклог 1.b: свой адаптер вместо ArrayAdapter + android.R.layout.simple_list_item_1.
    private val peerAdapter by lazy { PeerAdapter(this) }

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
                runOnUiThread {
                    Toast.makeText(this,
                        "Синхронизировано: ${r.pushed}↑ ${r.pulled}↓", Toast.LENGTH_SHORT).show()
                    finish()
                }
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
            // Бэклог 1.b: был ArrayAdapter с android.R.layout.simple_list_item_1.
            findViewById<ListView>(R.id.peersList).adapter = peerAdapter
            peerAdapter.submit(devices)
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

// Адаптер списка пиров (бэклог 1.b). Показывает карточку item_peer_row:
// имя устройства, IP-адрес, человекочитаемый статус и LED-индикатор связи.
// Бизнес-логику P2pConnector не меняет — только отображение.
private class PeerAdapter(private val ctx: Context) :
    ArrayAdapter<WifiP2pDevice>(ctx, 0, mutableListOf()) {

    fun submit(items: List<WifiP2pDevice>) {
        clear()
        addAll(items)
        notifyDataSetChanged()
    }

    override fun getView(position: Int, convertView: View?, parent: ViewGroup): View {
        val b = if (convertView == null)
            ItemPeerRowBinding.inflate(LayoutInflater.from(ctx), parent, false)
        else convertView.tag as ItemPeerRowBinding
        if (convertView == null) b.root.tag = b

        val d = getItem(position) ?: return b.root
        val connected = d.status == P2P_CONNECTED

        b.peerName.text = d.deviceName?.takeIf { it.isNotBlank() }
            ?: ctx.getString(R.string.peer_status_unavailable)
        val addr = d.deviceAddress?.takeIf { it.isNotBlank() }
            ?: ctx.getString(R.string.peer_address_unknown)
        b.peerStatus.text = ctx.getString(statusRes(d.status)) + " · " + addr
        b.peerStatus.setTextColor(ContextCompat.getColor(
            ctx,
            if (connected) R.color.text_secondary else R.color.text_hint))

        // LED: зелёный при подключении, серый иначе.
        b.peerLed.backgroundTintList = ColorStateList.valueOf(
            ContextCompat.getColor(
                ctx,
                if (connected) R.color.status_ok_fg else R.color.text_hint))
        return b.root
    }

    private fun statusRes(status: Int): Int = when (status) {
        P2P_CONNECTED -> R.string.peer_status_connected
        P2P_INVITED -> R.string.peer_status_invited
        P2P_FAILED -> R.string.peer_status_failed
        P2P_UNAVAILABLE -> R.string.peer_status_unavailable
        else -> R.string.peer_status_found
    }
}

// Константы WifiP2pDevice.WIFI_P2P_DEVICE_* помечены @hide и отсутствуют
// в публичном android.jar, поэтому значения зафиксированы здесь явно.
private const val P2P_DISCONNECTED = 0
private const val P2P_CONNECTED = 1
private const val P2P_INVITED = 2
private const val P2P_FAILED = 3
private const val P2P_UNAVAILABLE = 4
