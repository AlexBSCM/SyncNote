package com.syncnote

import android.content.Intent
import android.os.Bundle
import android.text.Editable
import android.text.TextWatcher
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.TextView
import android.widget.Toast
import androidx.fragment.app.Fragment
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import java.text.DateFormat
import java.util.Date

// Вкладка «Заметки»: список + поиск + переход в редактор/синхронизацию.
class NotesFragment : Fragment() {
    private lateinit var repo: NotesRepository
    private lateinit var adapter: NotesAdapter

    override fun onCreateView(
        inflater: LayoutInflater, parent: ViewGroup?, state: Bundle?
    ): View = inflater.inflate(R.layout.fragment_notes, parent, false)

    override fun onViewCreated(view: View, state: Bundle?) {
        repo = NotesRepository(requireContext())
        adapter = NotesAdapter(
            onClick = { note ->
                startActivity(Intent(requireContext(), EditorActivity::class.java)
                    .putExtra(EditorActivity.EXTRA_NOTE_ID, note.id))
            },
            onCheckChange = { updateDeleteButton() }
        )
        view.findViewById<RecyclerView>(R.id.notesList).also {
            it.layoutManager = LinearLayoutManager(requireContext())
            it.adapter = adapter
        }
        view.findViewById<EditText>(R.id.searchBox)
            .addTextChangedListener(object : TextWatcher {
                override fun afterTextChanged(s: Editable?) = refresh()
                override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            })
        view.findViewById<Button>(R.id.addButton).setOnClickListener {
            val n = repo.add("", "")
            startActivity(Intent(requireContext(), EditorActivity::class.java)
                .putExtra(EditorActivity.EXTRA_NOTE_ID, n.id))
        }
        view.findViewById<CheckBox>(R.id.selectAllBox).setOnCheckedChangeListener { _, v ->
            if (suppressSelectAll) return@setOnCheckedChangeListener
            adapter.setAll(v)
            updateDeleteButton()
        }
        view.findViewById<Button>(R.id.deleteCheckedButton).setOnClickListener {
            val ids = adapter.selectedIds()
            if (ids.isEmpty()) {
                Toast.makeText(requireContext(), "Ничего не выбрано.", Toast.LENGTH_SHORT).show()
                return@setOnClickListener
            }
            androidx.appcompat.app.AlertDialog.Builder(requireContext())
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
    }

    override fun onResume() {
        super.onResume()
        refresh()
        // Подтягиваем правки с ПК при возврате в список + опрос, пока открыты.
        SyncAuto.trigger(requireContext())
        pollHandler.postDelayed(poller, 2000)
    }

    override fun onPause() {
        pollHandler.removeCallbacks(poller)
        super.onPause()
    }

    private val pollHandler = android.os.Handler(android.os.Looper.getMainLooper())
    private val poller = object : Runnable {
        override fun run() {
            context?.let {
                SyncAuto.trigger(it, quiet = true)
                refresh()
                pollHandler.postDelayed(this, 2000)
            }
        }
    }

    private fun refresh() {
        val v = view ?: return
        val q = v.findViewById<EditText>(R.id.searchBox).text.toString()
        adapter.submit(if (q.isBlank()) repo.list() else repo.search(q))
        updateDeleteButton()
    }

    private var suppressSelectAll = false

    private fun updateDeleteButton() {
        val v = view ?: return
        val sel = adapter.selectedIds().size
        val total = adapter.itemCount
        v.findViewById<Button>(R.id.deleteCheckedButton).text =
            if (sel == 0) "Удалить выбранные" else "Удалить выбранные ($sel)"
        suppressSelectAll = true
        v.findViewById<CheckBox>(R.id.selectAllBox).isChecked = total > 0 && sel == total
        suppressSelectAll = false
    }

    private class NotesAdapter(
        val onClick: (Note) -> Unit,
        val onCheckChange: () -> Unit
    ) : RecyclerView.Adapter<NotesAdapter.Holder>() {
        private var items: List<Note> = emptyList()
        private val checked = mutableSetOf<String>()

        class Holder(parent: ViewGroup) : RecyclerView.ViewHolder(
            LayoutInflater.from(parent.context)
                .inflate(R.layout.item_note_check, parent, false)) {
            val box: CheckBox = itemView.findViewById(R.id.rowCheck)
            val title: TextView = itemView.findViewById(R.id.itemTitle)
            val date: TextView = itemView.findViewById(R.id.itemDate)
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
            h.title.text = n.title.ifBlank { "(без заголовка)" }
            h.date.text = DateFormat.getDateTimeInstance().format(Date(n.updatedAt))
            h.box.setOnCheckedChangeListener(null)
            h.box.isChecked = n.id in checked
            h.box.setOnCheckedChangeListener { _, v ->
                if (v) checked.add(n.id) else checked.remove(n.id)
                onCheckChange()
            }
            h.itemView.setOnClickListener { onClick(n) }
        }
    }
}
