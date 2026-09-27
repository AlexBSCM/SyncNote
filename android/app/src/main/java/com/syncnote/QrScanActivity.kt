package com.syncnote

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.common.InputImage
import org.json.JSONObject
import java.util.concurrent.Executors

// Сканирование QR сопряжения (CameraX + ML Kit).
// Возвращает host/port/token в SyncActivity. Посторонние QR отклоняются явно.
// NOT-VERIFIED на железе: только сборка; камера и модель ML Kit — на устройстве.
class QrScanActivity : AppCompatActivity() {
    companion object {
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_TOKEN = "token"
        private const val REQ_CAMERA = 41
    }

    private val scanner = BarcodeScanning.getClient()
    private val executor = Executors.newSingleThreadExecutor()
    private var done = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_qrscan)
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) ==
            PackageManager.PERMISSION_GRANTED) {
            startCamera()
        } else {
            ActivityCompat.requestPermissions(this,
                arrayOf(Manifest.permission.CAMERA), REQ_CAMERA)
        }
    }

    override fun onRequestPermissionsResult(
        code: Int, perms: Array<out String>, results: IntArray) {
        super.onRequestPermissionsResult(code, perms, results)
        if (code == REQ_CAMERA && results.firstOrNull() == PackageManager.PERMISSION_GRANTED)
            startCamera()
        else {
            Toast.makeText(this, "Без камеры сканирование невозможно.", Toast.LENGTH_LONG).show()
            finish()
        }
    }

    private fun startCamera() {
        val provider = ProcessCameraProvider.getInstance(this)
        provider.addListener({
            val preview = Preview.Builder().build().also {
                it.setSurfaceProvider(findViewById<PreviewView>(R.id.preview).surfaceProvider)
            }
            val analysis = ImageAnalysis.Builder()
                .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                .build()
                .also {
                    it.setAnalyzer(executor) { image ->
                        try {
                            val input = InputImage.fromMediaImage(
                                image.image!!, image.imageInfo.rotationDegrees)
                            scanner.process(input)
                                .addOnSuccessListener { codes ->
                                    for (c in codes) handle(c.rawValue)
                                }
                                .addOnCompleteListener { image.close() }
                        } catch (_: Exception) {
                            image.close()
                        }
                    }
                }
            provider.get().bindToLifecycle(this, CameraSelector.DEFAULT_BACK_CAMERA,
                preview, analysis)
        }, ContextCompat.getMainExecutor(this))
    }

    private fun handle(raw: String?) {
        if (done || raw.isNullOrBlank()) return
        try {
            val o = JSONObject(raw)
            if (o.optInt("v", 0) != 1) {
                hint("Чужой QR-код (не SyncNote).")
                return
            }
            val host = o.getString("host")
            val port = o.getInt("port")
            val token = o.getString("token")
            val exp = o.getLong("exp") * 1000
            if (System.currentTimeMillis() > exp) {
                hint("Срок кода истёк — покажите новый.")
                return
            }
            done = true
            setResult(Activity.RESULT_OK, Intent()
                .putExtra(EXTRA_HOST, host)
                .putExtra(EXTRA_PORT, port)
                .putExtra(EXTRA_TOKEN, token))
            finish()
        } catch (_: Exception) {
            hint("Не QR сопряжения SyncNote.")
        }
    }

    private fun hint(t: String) = runOnUiThread {
        findViewById<TextView>(R.id.hint).text = t
    }

    override fun onDestroy() {
        executor.shutdown()
        super.onDestroy()
    }
}
