package com.syncnote

import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.CheckBox
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.fragment.app.Fragment
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import java.io.File

// Вкладка «Настройки»: состояние хранилища и узла, управление базой
// (просмотр всех заметок, выбор всех/конкретных, удаление выбранных).
class SettingsFragment : Fragment() {
    private lateinit var repo: NotesRepository

    override fun onCreateView(
        inflater: LayoutInflater, parent: ViewGroup?, state: Bundle?
    ): View = inflater.inflate(R.layout.fragment_settings, parent, false)

    override fun onViewCreated(view: View, state: Bundle?) {
        repo = NotesRepository(requireContext())
        view.findViewById<Button>(R.id.forgetButton).setOnClickListener {
            SyncAuto.clearProfile(requireContext())
            refreshInfo()
            Toast.makeText(requireContext(), "Узел забыт.", Toast.LENGTH_SHORT).show()
        }
        refresh()
    }

    override fun onResume() {
        super.onResume()
        refresh()
    }

    private fun refresh() {
        refreshInfo()
    }

    private fun refreshInfo() {
        val v = view ?: return
        val notes = repo.listAll()
        val files = repo.filesDir().listFiles()?.filter { it.isFile } ?: emptyList()
        val dbSize = try {
            requireContext().getDatabasePath("notes.db").length()
        } catch (_: Exception) { -1 }
        v.findViewById<TextView>(R.id.storageInfo).text =
            "Заметок: ${notes.size}\n" +
            "Файлов: ${files.size} (${files.sumOf { it.length() / 1024 }} КБ)\n" +
            "База: ${if (dbSize >= 0) "${dbSize / 1024} КБ" else "—"}\n" +
            "Устройство: ${repo.deviceId().take(8)}…"
        val prof = SyncAuto.profile(requireContext())
        v.findViewById<TextView>(R.id.nodeInfo).text =
            if (prof != null) "Узел: ${prof.first}:${prof.second} (запомнен)"
            else "Узел не запомнен — отсканируйте QR."
    }
}
