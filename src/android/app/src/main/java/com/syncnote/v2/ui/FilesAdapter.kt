package com.syncnote.v2.ui

import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.TextView
import androidx.recyclerview.widget.RecyclerView
import com.syncnote.v2.R
import com.syncnote.v2.domain.FileEntry

// Список файлов: имя + размер/mime + короткий sha.
class FilesAdapter : RecyclerView.Adapter<FilesAdapter.Holder>() {
    private var items: List<FileEntry> = emptyList()

    class Holder(v: View) : RecyclerView.ViewHolder(v) {
        val name: TextView = v.findViewById(R.id.fileName)
        val meta: TextView = v.findViewById(R.id.fileMeta)
        val sha: TextView = v.findViewById(R.id.fileSha)
    }

    fun submit(list: List<FileEntry>) {
        items = list
        notifyDataSetChanged()
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Holder {
        val v = LayoutInflater.from(parent.context)
            .inflate(R.layout.item_file, parent, false)
        return Holder(v)
    }

    override fun onBindViewHolder(h: Holder, position: Int) {
        val f = items[position]
        h.name.text = f.name.ifBlank { "(без имени)" }
        h.meta.text = humanSize(f.sizeBytes) + " · " + f.mime + " · rev " + f.rev
        h.sha.text = "sha256: " + f.sha256.take(16) + "…"
    }

    override fun getItemCount(): Int = items.size

    private fun humanSize(bytes: Long): String {
        if (bytes < 1024) return "$bytes Б"
        val kb = bytes / 1024.0
        if (kb < 1024) return "%.1f КБ".format(kb)
        val mb = kb / 1024.0
        if (mb < 1024) return "%.1f МБ".format(mb)
        return "%.2f ГБ".format(mb / 1024.0)
    }
}
