package com.syncnote

import android.content.Intent
import android.os.Bundle
import android.text.Editable
import android.text.TextWatcher
import android.view.LayoutInflater
import android.view.ViewGroup
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import java.text.DateFormat
import java.util.Date

// Этап UI Android: список + поиск + переход в редактор.
// Синхронизация и QR — следующие этапы (см. docs/architecture.md).
class MainActivity : AppCompatActivity() {
    private lateinit var repo: NotesRepository
    private lateinit var adapter: NotesAdapter

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        repo = NotesRepository(this)
        adapter = NotesAdapter { note ->
            startActivity(Intent(this, EditorActivity::class.java)
                .putExtra(EditorActivity.EXTRA_NOTE_ID, note.id))
        }
        findViewById<RecyclerView>(R.id.notesList).also {
            it.layoutManager = LinearLayoutManager(this)
            it.adapter = adapter
        }
        findViewById<android.widget.EditText>(R.id.searchBox)
            .addTextChangedListener(object : TextWatcher {
                override fun afterTextChanged(s: Editable?) = refresh()
                override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            })
        findViewById<android.widget.Button>(R.id.addButton).setOnClickListener {
            val n = repo.add("Новая заметка", "")
            startActivity(Intent(this, EditorActivity::class.java)
                .putExtra(EditorActivity.EXTRA_NOTE_ID, n.id))
        }
    }

    override fun onResume() {
        super.onResume()
        refresh()
    }

    private fun refresh() {
        val q = findViewById<android.widget.EditText>(R.id.searchBox).text.toString()
        adapter.submit(if (q.isBlank()) repo.list() else repo.search(q))
    }

    private class NotesAdapter(val onClick: (Note) -> Unit) :
        RecyclerView.Adapter<NotesAdapter.Holder>() {
        private var items: List<Note> = emptyList()

        class Holder(parent: ViewGroup) : RecyclerView.ViewHolder(
            LayoutInflater.from(parent.context).inflate(R.layout.item_note, parent, false)) {
            val title: TextView = itemView.findViewById(R.id.itemTitle)
            val date: TextView = itemView.findViewById(R.id.itemDate)
        }

        fun submit(notes: List<Note>) {
            items = notes
            notifyDataSetChanged()
        }

        override fun onCreateViewHolder(p: ViewGroup, v: Int) = Holder(p)
        override fun getItemCount() = items.size
        override fun onBindViewHolder(h: Holder, pos: Int) {
            val n = items[pos]
            h.title.text = n.title.ifBlank { "(без заголовка)" }
            h.date.text = DateFormat.getDateTimeInstance().format(Date(n.updatedAt))
            h.itemView.setOnClickListener { onClick(n) }
        }
    }
}
