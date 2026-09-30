package com.syncnote.v2.ui

import android.content.Intent
import android.os.Bundle
import android.util.Log
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.TextView
import androidx.fragment.app.Fragment
import com.syncnote.v2.R
import com.syncnote.v2.data.AndroidDbInitializer
import com.syncnote.v2.data.TrustedDeviceStore
import com.syncnote.v2.data.ensureDeviceId
import com.syncnote.v2.data.openDb

// Вкладка «Настройки»: устройство, схема, доверенное устройство, сопряжение.
class SettingsFragment : Fragment() {
    override fun onCreateView(
        inflater: LayoutInflater, container: ViewGroup?, state: Bundle?
    ): View? {
        return inflater.inflate(R.layout.fragment_settings, container, false)
    }

    override fun onViewCreated(view: View, state: Bundle?) {
        view.findViewById<Button>(R.id.btnPairSettings).setOnClickListener {
            startActivity(Intent(requireContext(), PairingActivity::class.java))
        }
    }

    override fun onResume() {
        super.onResume()
        Thread {
            var deviceId = "—"
            var schema = 0
            var trusted: String? = null
            try {
                val ctx = context ?: return@Thread
                schema = AndroidDbInitializer.getCurrentVersion(ctx)
                val db = openDb(ctx.getDatabasePath(AndroidDbInitializer.DB_NAME))
                try {
                    deviceId = ensureDeviceId(db)
                    trusted = TrustedDeviceStore.get(db)?.let {
                        it.ip + ":" + it.port +
                            (if (it.lastSeen.isNotEmpty()) "\n" + it.lastSeen else "")
                    }
                } finally {
                    db.close()
                }
            } catch (e: Exception) {
                Log.e("SyncNoteV2", "Settings load failed", e)
                return@Thread
            }
            val d = deviceId
            val s = schema
            val t = trusted
            activity?.runOnUiThread {
                view?.findViewById<TextView>(R.id.deviceIdText)?.text = "ID: $d"
                view?.findViewById<TextView>(R.id.schemaText)?.text = "Схема БД: v$s"
                view?.findViewById<TextView>(R.id.trustedDeviceText)?.text =
                    t ?: "Нет — отсканируйте QR для сопряжения"
            }
        }.start()
    }
}
