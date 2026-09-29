package com.syncnote

import android.os.Bundle
import androidx.appcompat.app.AppCompatActivity
import com.google.android.material.bottomnavigation.BottomNavigationView
import androidx.fragment.app.Fragment

class MainActivity : AppCompatActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        if (savedInstanceState == null) {
            showTab(NotesFragment())
        }
        val bottomNav = findViewById<BottomNavigationView>(R.id.bottomNav)
        // Вкладка «Файлы» — только при включённом флаге (этап F.7).
        val filesOn = BuildConfig.ENABLE_SEPARATE_FILES && FeatureFlags.enableSeparateFiles
        if (!filesOn) bottomNav.menu.removeItem(R.id.tab_files)
        bottomNav.setOnItemSelectedListener { item ->
            when (item.itemId) {
                R.id.tab_settings -> showTab(SettingsFragment())
                R.id.tab_files -> showTab(FilesListFragment())
                else -> showTab(NotesFragment())
            }
            true
        }
    }

    private fun showTab(f: Fragment) {
        supportFragmentManager.beginTransaction()
            .replace(R.id.fragmentBox, f)
            .commit()
    }

    fun selectTab(itemId: Int) {
        findViewById<BottomNavigationView>(R.id.bottomNav).selectedItemId = itemId
    }
}
