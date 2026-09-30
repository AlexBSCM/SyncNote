package com.syncnote.v2.ui

import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.TextView
import androidx.recyclerview.widget.RecyclerView
import com.syncnote.v2.R
import com.syncnote.v2.domain.NoteEntry

// Список заметок: только чтение и отображение (CRUD-экраны — отдельно).
class NotesAdapter : RecyclerView.Adapter<NotesAdapter.Holder>() {
    private var items: List<NoteEntry> = emptyList()

    class Holder(v: View) : RecyclerView.ViewHolder(v) {
        val title: TextView = v.findViewById(R.id.noteTitle)
        val snippet: TextView = v.findViewById(R.id.noteSnippet)
        val date: TextView = v.findViewById(R.id.noteDate)
    }

    fun submit(list: List<NoteEntry>) {
        items = list
        notifyDataSetChanged()
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Holder {
        val v = LayoutInflater.from(parent.context)
            .inflate(R.layout.item_note, parent, false)
        return Holder(v)
    }

    override fun onBindViewHolder(h: Holder, position: Int) {
        val n = items[position]
        h.title.text = n.title.ifBlank { "(без названия)" }
        h.snippet.text = n.body.lines().firstOrNull().orEmpty()
        h.date.text = n.updatedAt + " · rev " + n.rev
    }

    override fun getItemCount(): Int = items.size
}
