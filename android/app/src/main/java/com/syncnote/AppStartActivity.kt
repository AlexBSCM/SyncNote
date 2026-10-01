package com.syncnote

import android.content.Intent
import android.os.Bundle
import android.util.Log
import android.view.View
import android.widget.Button
import android.widget.EditText
import android.widget.ProgressBar
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.ViewModelProvider
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.google.android.material.snackbar.Snackbar

// Точка входа приложения (бэклог 1.c), «Skip if Connected».
//
// Логика: если есть доверенное устройство — мгновенно уходим в MainActivity
// и finish(), чтобы «Назад» на главном экране не возвращал сюда.
// Если устройств нет — показываем Smart Start Screen со списком и QR.
//
// Fail-safe: любая ошибка проверки трактуется как «соединения нет».
class AppStartActivity : AppCompatActivity() {

    private lateinit var vm: SyncHomeViewModel
    private lateinit var adapter: TrustedDeviceAdapter

    private val qrLauncher = registerForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.StartActivityForResult()
    ) { res ->
        if (res.resultCode != RESULT_OK) return@registerForActivityResult
        val d = res.data ?: return@registerForActivityResult
        val host = d.getStringExtra(QrScanActivity.EXTRA_HOST).orEmpty()
        val port = d.getIntExtra(QrScanActivity.EXTRA_PORT, 0)
        if (host.isBlank() || port <= 0) {
            toast("QR не содержит адреса узла."); return@registerForActivityResult
        }
        // Регистрируем и сразу проверяем живым подключением: успех уводит
        // в MainActivity и помечает устройство как рабочее, ошибка оставляет
        // его в списке на этом экране.
        vm.addDevice(host, port)
        connect(TrustedDevice(
            id = TrustedDevice.idFor(host, port), host = host, port = port,
            name = host, lastUsed = 0L))
    }

    private val syncLauncher = registerForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.StartActivityForResult()
    ) {
        // Ручной ввод мог сохранить профиль — тогда уходим на главный экран.
        if (TrustedDevicesStore.hasActiveTrustedDevice(this)) goToMain() else vm.load()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Проверка ДО загрузки layout'а (правило Instant Redirect).
        if (isConnected()) {
            goToMain()
            return
        }

        setContentView(R.layout.fragment_sync_home)
        vm = ViewModelProvider(this)[SyncHomeViewModel::class.java]
        adapter = TrustedDeviceAdapter(
            onClick = { d -> connect(d) },
            onLongClick = { d -> askRename(d) })

        findViewById<RecyclerView>(R.id.devicesList).also {
            it.layoutManager = LinearLayoutManager(this)
            it.adapter = adapter
        }
        findViewById<Button>(R.id.scanQrButton).setOnClickListener {
            qrLauncher.launch(Intent(this, QrScanActivity::class.java))
        }
        findViewById<Button>(R.id.manualEntryButton).setOnClickListener {
            syncLauncher.launch(Intent(this, SyncActivity::class.java))
        }
        findViewById<Button>(R.id.addDeviceButton).setOnClickListener {
            qrLauncher.launch(Intent(this, QrScanActivity::class.java))
        }
        observe()
    }

    override fun onResume() {
        super.onResume()
        // Экран мог остаться в истории: список обновляем при возврате.
        if (::vm.isInitialized) {
            if (TrustedDevicesStore.hasActiveTrustedDevice(this) && !vm.isBusy()) {
                goToMain(); return
            }
            vm.load()
        }
    }

    // Fail-safe: ошибка -> false -> показываем Smart Screen.
    private fun isConnected(): Boolean = try {
        TrustedDevicesStore.hasActiveTrustedDevice(this)
    } catch (e: Exception) {
        Log.w("AppStart", "connection check failed, assume offline", e)
        false
    }

    private fun goToMain() {
        startActivity(Intent(this, MainActivity::class.java))
        finish()
    }

    private fun observe() {
        vm.devices.observe(this) { list ->
            adapter.submit(list)
            val has = list.isNotEmpty()
            findViewById<View>(R.id.emptyState).visibility =
                if (has) View.GONE else View.VISIBLE
            findViewById<View>(R.id.devicesList).visibility =
                if (has) View.VISIBLE else View.GONE
            findViewById<View>(R.id.addDeviceButton).visibility =
                if (has) View.VISIBLE else View.GONE
        }
        vm.connectingId.observe(this) { id ->
            adapter.setConnecting(id)
            val bar = findViewById<View>(R.id.connectingBar)
            bar.visibility = if (id == null) View.GONE else View.VISIBLE
            if (id != null) {
                val name = vm.devices.value?.firstOrNull { it.id == id }?.name ?: "устройству"
                findViewById<TextView>(R.id.connectingText).text =
                    "Подключение к «$name»…"
            }
        }
        vm.load()
    }

    private fun connect(d: TrustedDevice) {
        if (vm.isBusy()) return
        vm.connectAndSync(d.id,
            onSuccess = { r ->
                val extra = if (r.conflicts > 0) " Есть конфликты." else ""
                toast("Синхронизировано: +${r.pushed}/−${r.pulled}.$extra")
                goToMain()
            },
            onError = { msg ->
                snack("Не удалось подключиться к «${d.name}»: $msg")
                findViewById<View>(R.id.connectingBar).visibility = View.GONE
            })
    }

    private fun askRename(d: TrustedDevice) {
        val box = EditText(this).apply {
            setText(d.name)
            setSelection(d.name.length)
        }
        val pad = (16 * resources.displayMetrics.density).toInt()
        AlertDialog.Builder(this)
            .setTitle("Переименовать устройство")
            .setView(box)
            .setPositiveButton("Сохранить") { _, _ ->
                vm.rename(d.id, box.text.toString())
                toast("Имя сохранено.")
            }
            .setNegativeButton("Отмена", null)
            .show()
        box.setPadding(pad, pad / 2, pad, pad / 2)
    }

    private fun snack(text: String) {
        Snackbar.make(findViewById(android.R.id.content), text, Snackbar.LENGTH_LONG).show()
    }

    private fun toast(text: String) = Toast.makeText(this, text, Toast.LENGTH_SHORT).show()
}
