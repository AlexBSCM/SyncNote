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
        findViewById<BottomNavigationView>(R.id.bottomNav)
            .setOnItemSelectedListener { item ->
                when (item.itemId) {
                    R.id.tab_settings -> showTab(SettingsFragment())
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
}
