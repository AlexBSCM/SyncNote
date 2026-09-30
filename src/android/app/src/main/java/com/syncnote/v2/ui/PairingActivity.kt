package com.syncnote.v2.ui

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.graphics.Point
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.util.Log
import android.util.Size
import android.view.View
import android.widget.Button
import android.widget.ProgressBar
import android.widget.TextView
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import androidx.lifecycle.LifecycleOwner
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.barcode.BarcodeScanner
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.common.InputImage
import com.syncnote.v2.R
import com.syncnote.v2.data.SyncRunner
import com.syncnote.v2.data.AndroidDbInitializer
import com.syncnote.v2.data.TrustedDevice
import com.syncnote.v2.data.TrustedDeviceStore
import com.syncnote.v2.data.openDb
import com.syncnote.v2.domain.QrPayload
import org.json.JSONObject
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

class PairingActivity : AppCompatActivity() {
    private lateinit var previewView: PreviewView
    private lateinit var statusText: TextView
    private lateinit var scanLine: View
    private lateinit var scanFrame: View
    private lateinit var permissionDenied: TextView
    private lateinit var btnManualEntry: Button
    private lateinit var loadingProgress: ProgressBar
    private lateinit var loadingText: TextView

    private var cameraProvider: ProcessCameraProvider? = null
    private var barcodeScanner: BarcodeScanner? = null
    private var cameraExecutor: ExecutorService = Executors.newSingleThreadExecutor()
    private var scanLineAnimator: Handler = Handler(Looper.getMainLooper())
    private var scanLineDirection = 1
    private var isScanning = false
    private var permissionGranted = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_pairing)

        previewView = findViewById(R.id.previewView)
        statusText = findViewById(R.id.statusText)
        scanLine = findViewById(R.id.scanLine)
        scanFrame = findViewById(R.id.scanFrame)
        permissionDenied = findViewById(R.id.permissionDenied)
        btnManualEntry = findViewById(R.id.btnManualEntry)
        loadingProgress = findViewById(R.id.loadingProgress)
        loadingText = findViewById(R.id.loadingText)

        btnManualEntry.setOnClickListener { showManualEntryDialog() }
        permissionDenied.setOnClickListener {
            val intent = Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS)
            intent.data = Uri.fromParts("package", packageName, null)
            startActivity(intent)
        }

        // Initialize ML Kit barcode scanner
        barcodeScanner = BarcodeScanning.getClient()

        // Check camera permission
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA)
            == PackageManager.PERMISSION_GRANTED) {
            startCamera()
        } else {
            ActivityCompat.requestPermissions(this,
                arrayOf(Manifest.permission.CAMERA),
                CAMERA_PERMISSION_REQUEST_CODE)
        }

        btnManualEntry.setOnClickListener { showManualEntryDialog() }
    }

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == CAMERA_PERMISSION_REQUEST_CODE) {
            if (grantResults.isNotEmpty() && grantResults[0] == PackageManager.PERMISSION_GRANTED) {
                permissionGranted = true
                permissionDenied.visibility = View.GONE
                previewView.visibility = View.VISIBLE
                scanFrame.visibility = View.VISIBLE
                scanLine.visibility = View.VISIBLE
                statusText.visibility = View.VISIBLE
                startCamera()
            } else {
                permissionGranted = false
                permissionDenied.visibility = View.VISIBLE
                previewView.visibility = View.GONE
                scanFrame.visibility = View.GONE
                scanLine.visibility = View.GONE
                statusText.visibility = View.GONE
            }
        }
    }

    private fun startCamera() {
        val cameraProviderFuture = ProcessCameraProvider.getInstance(this)
        cameraProviderFuture.addListener({
            try {
                cameraProvider = cameraProviderFuture.get()
                bindCameraUseCases(cameraProvider!!)
            } catch (e: Exception) {
                Log.e("PairingActivity", "Camera start failed", e)
            }
        }, ContextCompat.getMainExecutor(this))
    }

    private fun bindCameraUseCases(cameraProvider: ProcessCameraProvider) {
        val preview = Preview.Builder()
            .setTargetRotation(previewView.display.rotation)
            .build()
            .also { it.setSurfaceProvider(previewView.surfaceProvider) }

        val imageAnalysis = ImageAnalysis.Builder()
            .setTargetRotation(previewView.display.rotation)
            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
            .build()
            .also {
                it.setAnalyzer(cameraExecutor) { imageProxy ->
                    analyzeImage(imageProxy)
                }
            }

        val cameraSelector = CameraSelector.DEFAULT_BACK_CAMERA
        try {
            cameraProvider.unbindAll()
            cameraProvider.bindToLifecycle(
                this as LifecycleOwner, cameraSelector, preview, imageAnalysis)
        } catch (e: Exception) {
            Log.e("PairingActivity", "Camera bind failed", e)
        }
    }

    private fun analyzeImage(imageProxy: ImageProxy) {
        if (!isScanning) {
            imageProxy.close()
            return
        }

        val mediaImage = imageProxy.image
        if (mediaImage != null) {
            val image = InputImage.fromMediaImage(
                mediaImage, imageProxy.imageInfo.rotationDegrees)
            val scanner = barcodeScanner
            if (scanner == null) {
                imageProxy.close()
                return
            }
            scanner.process(image)
                .addOnSuccessListener { barcodes ->
                    if (isScanning && barcodes.isNotEmpty()) {
                        handleQrResult(barcodes[0])
                    }
                    imageProxy.close()
                }
                .addOnFailureListener { e ->
                    Log.e("PairingActivity", "Barcode scanning failed", e)
                    imageProxy.close()
                }
                .addOnCompleteListener {
                    imageProxy.close()
                }
        } else {
            imageProxy.close()
        }
    }

    private var lastScanTime = 0L
    private fun handleQrResult(barcode: Barcode) {
        val currentTime = System.currentTimeMillis()
        if (currentTime - lastScanTime < 2000) return
        lastScanTime = currentTime

        barcode.rawValue?.let { rawValue ->
            if (QrPayload.parse(rawValue) != null) {
                isScanning = false
                scanLineAnimator.removeCallbacksAndMessages(null)
                scanLine.visibility = View.INVISIBLE
                launchSync(rawValue)
            }
        }
    }

    private fun launchSync(payloadJson: String) {
        val parsed = QrPayload.parse(payloadJson)
        runOnUiThread {
            statusText.text = if (parsed != null)
                "Подключение к ${parsed.ip}:${parsed.port}..."
            else
                "Подключение..."
            statusText.visibility = View.VISIBLE
            loadingProgress.visibility = View.VISIBLE
            loadingText.visibility = View.VISIBLE
            previewView.visibility = View.INVISIBLE
            scanFrame.visibility = View.INVISIBLE
            scanLine.visibility = View.INVISIBLE
            btnManualEntry.visibility = View.INVISIBLE
        }

        try {
            SyncRunner(applicationContext).syncWithQr(payloadJson) { result ->
                runOnUiThread {
                    loadingProgress.visibility = View.GONE
                    loadingText.visibility = View.GONE
                    if (result.isSuccess) {
                        val r = result.getOrNull()
                        saveTrustedDevice(parsed)
                        showResultDialog("Успех", "Синхронизация завершена:\nОтправлено: ${r?.pushed ?: 0}\nПолучено: ${r?.pulled ?: 0}\nКонфликтов: ${r?.conflicts ?: 0}")
                    } else {
                        showErrorDialog("Ошибка", result.exceptionOrNull()?.message ?: "Неизвестная ошибка")
                    }
                }
            }
        } catch (e: Exception) {
            Log.e("PairingActivity", "Sync failed", e)
            runOnUiThread {
                loadingProgress.visibility = View.GONE
                loadingText.visibility = View.GONE
                showErrorDialog("Ошибка", e.message ?: "Неизвестная ошибка")
            }
        }
    }

    // Успешный синк фиксируем: адрес подставится в ручной ввод,
    // MainActivity покажет устройство для повторного сопряжения.
    // Токен одноразовый и здесь не сохраняется.
    private fun saveTrustedDevice(parsed: QrPayload?) {
        if (parsed == null) return
        try {
            val dbFile = getDatabasePath(AndroidDbInitializer.DB_NAME)
            val db = openDb(dbFile)
            try {
                TrustedDeviceStore.save(db,
                    TrustedDevice(parsed.ip, parsed.port, utcNowStr()))
            } finally {
                db.close()
            }
        } catch (e: Exception) {
            Log.e("PairingActivity", "Trusted device save failed", e)
        }
    }

    private fun showResultDialog(title: String, message: String) {
        AlertDialog.Builder(this)
            .setTitle(title)
            .setMessage(message)
            .setPositiveButton("OK") { _, _ -> finish() }
            .setCancelable(false)
            .show()
    }

    private fun showErrorDialog(title: String, message: String) {
        AlertDialog.Builder(this)
            .setTitle(title)
            .setMessage(message)
            .setPositiveButton("OK") { _, _ -> finish() }
            .setNegativeButton("Повторить") { _, _ -> restartScanning() }
            .show()
    }

    private fun restartScanning() {
        isScanning = true
        scanLine.visibility = View.VISIBLE
        startScanLineAnimation()
        statusText.text = "Наведите камеру на QR-код"
        previewView.visibility = View.VISIBLE
        scanFrame.visibility = View.VISIBLE
        scanLine.visibility = View.VISIBLE
        statusText.visibility = View.VISIBLE
        btnManualEntry.visibility = View.VISIBLE
        loadingProgress.visibility = View.GONE
        loadingText.visibility = View.GONE
        startScanLineAnimation()
    }

    private fun utcNowStr(): String =
        java.time.Instant.now()
            .truncatedTo(java.time.temporal.ChronoUnit.SECONDS).toString()

    private fun loadTrustedDevice(): TrustedDevice? {
        return try {
            val dbFile = getDatabasePath(AndroidDbInitializer.DB_NAME)
            val db = openDb(dbFile)
            try {
                TrustedDeviceStore.get(db)
            } finally {
                db.close()
            }
        } catch (e: Exception) {
            Log.e("PairingActivity", "Trusted device read failed", e)
            null
        }
    }

    private fun showManualEntryDialog() {
        val saved = loadTrustedDevice()
        val ipInput = android.widget.EditText(this).apply {
            hint = "IP адрес (например, 192.168.1.5)"
            saved?.let { setText(it.ip) }
        }
        val portInput = android.widget.EditText(this).apply {
            hint = "Порт"
            inputType = android.text.InputType.TYPE_CLASS_NUMBER
            setText(saved?.port?.toString() ?: "48211")
        }
        val tokenInput = android.widget.EditText(this).apply { hint = "Токен" }

        AlertDialog.Builder(this)
            .setTitle("Ручное подключение")
            .setView(android.widget.LinearLayout(this).apply {
                orientation = android.widget.LinearLayout.VERTICAL
                setPadding(24, 24, 24, 24)
                addView(ipInput)
                addView(portInput)
                addView(tokenInput)
            })
            .setPositiveButton("Подключиться") { _, _ ->
                val ip = ipInput.text.toString().trim()
                val portStr = portInput.text.toString().trim()
                val token = tokenInput.text.toString().trim()
                if (ip.isNotEmpty() && portStr.isNotEmpty() && token.isNotEmpty()) {
                    val port = portStr.toIntOrNull() ?: 48211
                    val payloadJson = JSONObject()
                        .put("ip", ip)
                        .put("port", port)
                        .put("token", token)
                        .toString()
                    launchSync(payloadJson)
                }
            }
            .setNegativeButton("Отмена", null)
            .show()
    }

    private fun startScanLineAnimation() {
        scanLineAnimator.removeCallbacksAndMessages(null)
        val frameHeight = scanFrame.height
        if (frameHeight == 0) {
            scanFrame.post { startScanLineAnimation() }
            return
        }
        val scanLineHeight = scanLine.height
        val maxTranslation = frameHeight - scanLineHeight
        val duration = 1500L

        val animator = android.animation.ValueAnimator.ofFloat(0f, maxTranslation.toFloat())
        animator.duration = duration
        animator.repeatCount = android.animation.ValueAnimator.INFINITE
        animator.repeatMode = android.animation.ValueAnimator.REVERSE
        animator.addUpdateListener { animation ->
            val value = animation.animatedValue as Float
            scanLine.translationY = value
        }
        animator.start()
    }

    override fun onPause() {
        super.onPause()
        isScanning = false
        scanLineAnimator.removeCallbacksAndMessages(null)
        scanLine.visibility = View.INVISIBLE
        barcodeScanner?.close()
    }

    override fun onDestroy() {
        super.onDestroy()
        barcodeScanner?.close()
        cameraExecutor.shutdown()
    }

    companion object {
        const val CAMERA_PERMISSION_REQUEST_CODE = 101
    }
}