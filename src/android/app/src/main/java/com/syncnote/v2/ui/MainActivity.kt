package com.syncnote.v2.ui

import android.os.Bundle
import android.util.Log
import android.widget.Button
import androidx.appcompat.app.AppCompatActivity
import androidx.fragment.app.Fragment
import com.syncnote.v2.R
import com.syncnote.v2.data.AndroidDbInitializer

// Главный экран v2: вкладки «Заметки / Файлы / Настройки».
// Сопряжение живёт в PairingActivity (кнопки — в настройках и списке).
class MainActivity : AppCompatActivity() {
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

        findViewById<Button>(R.id.tabNotes).setOnClickListener {
            show(NotesFragment())
        }
        findViewById<Button>(R.id.tabFiles).setOnClickListener {
            show(FilesFragment())
        }
        findViewById<Button>(R.id.tabSettings).setOnClickListener {
            show(SettingsFragment())
        }
        if (state == null) show(NotesFragment())
    }

    private fun show(f: Fragment) {
        supportFragmentManager.beginTransaction()
            .replace(R.id.fragmentContainer, f)
            .commit()
    }
}
