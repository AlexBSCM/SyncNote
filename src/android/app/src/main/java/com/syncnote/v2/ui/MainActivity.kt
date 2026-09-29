package com.syncnote.v2.ui

import android.app.Activity
import android.os.Bundle
import android.util.Log
import com.syncnote.v2.data.AndroidDbInitializer

// Минимальная точка входа v2: только инициализация БД (fail-fast).
// Экраны появятся на следующих шагах.
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
    }
}
