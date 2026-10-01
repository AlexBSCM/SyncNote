package com.syncnote

import android.os.Bundle
import android.view.LayoutInflater
import android.view.ViewGroup
import android.widget.Button
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.syncnote.databinding.ItemConflictPairBinding
import java.text.DateFormat
import java.util.Date

// Разрешение конфликтов: пары по суффиксу копии (зеркало Conflicts C#).
// Все действия явные, скрытых потерь нет.
class ConflictsActivity : AppCompatActivity() {
    private lateinit var repo: NotesRepository
    private var pairs: List<Pair<Note, Note>> = emptyList()
    private var selected = 0
    private lateinit var adapter: PairsAdapter

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_conflicts)
        repo = NotesRepository(this)
        adapter = PairsAdapter { pos ->
            selected = pos
            showDetail()
        }
        findViewById<RecyclerView>(R.id.pairsList).also {
            it.layoutManager = LinearLayoutManager(this)
            it.adapter = adapter
        }
        findViewById<Button>(R.id.keepOriginalButton).setOnClickListener {
            current()?.let { (_, copy) ->
                repo.delete(copy.id)
                refresh()
            }
        }
        findViewById<Button>(R.id.takeCopyButton).setOnClickListener {
            current()?.let { (orig, copy) ->
                repo.delete(orig.id)
                copy.title = orig.title
                repo.update(copy)
                refresh()
            }
        }
        findViewById<Button>(R.id.keepBothButton).setOnClickListener {
            current()?.let { (_, copy) ->
                copy.title = copy.title.removeSuffix(SyncEngine.COPY_SUFFIX) + " (вторая версия)"
                repo.update(copy)
                refresh()
            }
        }
        refresh()
    }

    private fun current(): Pair<Note, Note>? =
        if (selected in pairs.indices) pairs[selected] else null

    private fun refresh() {
        pairs = findPairs()
        adapter.submit(pairs)
        if (pairs.isEmpty()) {
            finish()
            return
        }
        selected = selected.coerceIn(pairs.indices)
        showDetail()
    }

    private fun showDetail() {
        val (orig, copy) = current() ?: return
        findViewById<TextView>(R.id.detailBox).text =
            "ТЕКУЩАЯ:\n${orig.title}\n${orig.body}\n\nКОПИЯ:\n${copy.title}\n${copy.body}"
    }

    private fun findPairs(): List<Pair<Note, Note>> {
        val notes = repo.list()
        val out = mutableListOf<Pair<Note, Note>>()
        for (copy in notes.filter { it.title.endsWith(SyncEngine.COPY_SUFFIX) }) {
            val base = copy.title.removeSuffix(SyncEngine.COPY_SUFFIX)
            val orig = notes.firstOrNull {
                !it.title.endsWith(SyncEngine.COPY_SUFFIX) && it.title == base
            } ?: continue
            out += orig to copy
        }
        return out
    }

    private class PairsAdapter(val onClick: (Int) -> Unit) :
        RecyclerView.Adapter<PairsAdapter.Holder>() {
        private var items: List<Pair<Note, Note>> = emptyList()

        // Бэклог 1.b: был android.R.layout.simple_list_item_1 — системный layout.
        class Holder(val b: ItemConflictPairBinding) : RecyclerView.ViewHolder(b.root)

        fun submit(p: List<Pair<Note, Note>>) {
            items = p
            notifyDataSetChanged()
        }

        override fun onCreateViewHolder(p: ViewGroup, v: Int) = Holder(
            ItemConflictPairBinding.inflate(
                LayoutInflater.from(p.context), p, false))

        override fun getItemCount() = items.size

        override fun onBindViewHolder(h: Holder, pos: Int) {
            val (orig, copy) = items[pos]
            h.b.pairTitle.text = orig.title
            h.b.localPreview.text = orig.body.ifBlank { "—" }
            h.b.remotePreview.text = copy.body.ifBlank { "—" }
            h.b.pairDate.text = DateFormat.getDateTimeInstance(DateFormat.SHORT,
                DateFormat.SHORT).format(Date(copy.updatedAt))
            h.itemView.setOnClickListener { onClick(pos) }
        }
    }
}
