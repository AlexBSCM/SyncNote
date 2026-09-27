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
    private lateinit var adapter: CheckAdapter

    override fun onCreateView(
        inflater: LayoutInflater, parent: ViewGroup?, state: Bundle?
    ): View = inflater.inflate(R.layout.fragment_settings, parent, false)

    override fun onViewCreated(view: View, state: Bundle?) {
        repo = NotesRepository(requireContext())
        adapter = CheckAdapter { refreshInfo() }
        view.findViewById<RecyclerView>(R.id.allNotesList).also {
            it.layoutManager = LinearLayoutManager(requireContext())
            it.adapter = adapter
        }
        view.findViewById<CheckBox>(R.id.selectAllBox).setOnCheckedChangeListener { _, checked ->
            adapter.setAll(checked)
            refreshInfo()
        }
        view.findViewById<Button>(R.id.deleteSelectedButton).setOnClickListener {
            val ids = adapter.selectedIds()
            if (ids.isEmpty()) {
                Toast.makeText(requireContext(), "Ничего не выбрано.", Toast.LENGTH_SHORT).show()
                return@setOnClickListener
            }
            AlertDialog.Builder(requireContext())
                .setTitle("Удалить ${ids.size} заметок?")
                .setMessage("Удалённые заметки пропадут и на другом устройстве при синхронизации.")
                .setPositiveButton("Удалить") { _, _ ->
                    repo.deleteNotes(ids)
                    SyncAuto.trigger(requireContext())
                    refresh()
                }
                .setNegativeButton("Отмена", null)
                .show()
        }
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
        adapter.submit(repo.listAll())
        view?.findViewById<CheckBox>(R.id.selectAllBox)?.isChecked = false
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
        val sel = adapter.selectedIds().size
        v.findViewById<Button>(R.id.deleteSelectedButton).text =
            if (sel == 0) "Удалить выбранные" else "Удалить выбранные ($sel)"
    }

    private class CheckAdapter(val onChange: () -> Unit) :
        RecyclerView.Adapter<CheckAdapter.Holder>() {
        private var items: List<Note> = emptyList()
        private val checked = mutableSetOf<String>()

        class Holder(parent: ViewGroup) : RecyclerView.ViewHolder(
            LayoutInflater.from(parent.context)
                .inflate(android.R.layout.simple_list_item_multiple_choice, parent, false)) {
            val box: CheckBox = itemView.findViewById(android.R.id.text1)
        }

        fun submit(notes: List<Note>) {
            items = notes
            checked.retainAll(notes.map { it.id }.toSet())
            notifyDataSetChanged()
        }

        fun setAll(v: Boolean) {
            if (v) checked.addAll(items.map { it.id }) else checked.clear()
            notifyDataSetChanged()
        }

        fun selectedIds(): List<String> = checked.toList()

        override fun onCreateViewHolder(p: ViewGroup, v: Int) = Holder(p)
        override fun getItemCount() = items.size
        override fun onBindViewHolder(h: Holder, pos: Int) {
            val n = items[pos]
            h.box.text = n.title.ifBlank { "(без заголовка)" }
            h.box.setOnCheckedChangeListener(null)
            h.box.isChecked = n.id in checked
            h.box.setOnCheckedChangeListener { _, v ->
                if (v) checked.add(n.id) else checked.remove(n.id)
                onChange()
            }
        }
    }
}
