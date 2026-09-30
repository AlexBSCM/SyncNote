package com.syncnote.v2.ui

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.util.Log
import android.widget.Button
import android.widget.TextView
import com.syncnote.v2.R
import com.syncnote.v2.data.AndroidDbInitializer
import com.syncnote.v2.data.TrustedDeviceStore
import com.syncnote.v2.data.openDb

// Точка входа v2: инициализация БД, переход в сопряжение,
// показ последнего устройства для повторного синка.
class MainActivity : Activity() {
    override fun onCreate(state: Bundle?) {
        super.onCreate(state)
        try {
            AndroidDbInitializer.initialize(this)
            Log.i("SyncNoteV2", "DB ready, schema v" +
                AndroidDbInitializer.getCurrentVersion(this))
        } catch (e: Exception) {
            Log.e("SyncNoteV2", "DB init failed", e)
            throw RuntimeException("Database initialization failed", e)
        }
        setContentView(R.layout.activity_main)

        findViewById<Button>(R.id.btnPair).setOnClickListener {
            startActivity(Intent(this, PairingActivity::class.java))
        }
    }

    override fun onResume() {
        super.onResume()
        val label = try {
            val dbFile = getDatabasePath(AndroidDbInitializer.DB_NAME)
            val db = openDb(dbFile)
            try {
                TrustedDeviceStore.get(db)?.let {
                    "Последнее устройство: ${it.ip}:${it.port}" +
                        (if (it.lastSeen.isNotEmpty()) "\n${it.lastSeen}" else "")
                } ?: "Устройств пока нет — отсканируйте QR на ПК"
            } finally {
                db.close()
            }
        } catch (e: Exception) {
            Log.e("SyncNoteV2", "Trusted device read failed", e)
            "Устройств пока нет — отсканируйте QR на ПК"
        }
        findViewById<TextView>(R.id.lastDeviceText).text = label
    }
}
