package com.syncnote.v2.ui

import android.os.Bundle
import android.util.Log
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.TextView
import androidx.fragment.app.Fragment
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.syncnote.v2.R
import com.syncnote.v2.data.AndroidDbInitializer
import com.syncnote.v2.data.SqliteNoteRepo

// Вкладка «Заметки»: чтение из SQL-репо в фоне, отрисовка в UI.
class NotesFragment : Fragment() {
    private val adapter = NotesAdapter()

    override fun onCreateView(
        inflater: LayoutInflater, container: ViewGroup?, state: Bundle?
    ): View? {
        return inflater.inflate(R.layout.fragment_notes, container, false)
    }

    override fun onViewCreated(view: View, state: Bundle?) {
        val list = view.findViewById<RecyclerView>(R.id.notesList)
        list.layoutManager = LinearLayoutManager(requireContext())
        list.adapter = adapter
    }

    override fun onResume() {
        super.onResume()
        load()
    }

    private fun load() {
        Thread {
            val items = try {
                val ctx = context ?: return@Thread
                val dbFile = ctx.getDatabasePath(AndroidDbInitializer.DB_NAME)
                SqliteNoteRepo(dbFile).getAll(includeDeleted = false)
            } catch (e: Exception) {
                Log.e("SyncNoteV2", "Notes load failed", e)
                return@Thread
            }
            activity?.runOnUiThread {
                adapter.submit(items)
                view?.findViewById<TextView>(R.id.notesEmpty)?.visibility =
                    if (items.isEmpty()) View.VISIBLE else View.GONE
            }
        }.start()
    }
}
