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
import com.syncnote.v2.data.AndroidFileIo
import com.syncnote.v2.data.SqliteFileRepo

// Вкладка «Файлы»: метаданные из SQL-репо, байты не трогаем.
class FilesFragment : Fragment() {
    private val adapter = FilesAdapter()

    override fun onCreateView(
        inflater: LayoutInflater, container: ViewGroup?, state: Bundle?
    ): View? {
        return inflater.inflate(R.layout.fragment_files, container, false)
    }

    override fun onViewCreated(view: View, state: Bundle?) {
        val list = view.findViewById<RecyclerView>(R.id.filesList)
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
                val io = AndroidFileIo(ctx.filesDir)
                SqliteFileRepo(dbFile, io).getAll(includeDeleted = false)
            } catch (e: Exception) {
                Log.e("SyncNoteV2", "Files load failed", e)
                return@Thread
            }
            activity?.runOnUiThread {
                adapter.submit(items)
                view?.findViewById<TextView>(R.id.filesEmpty)?.visibility =
                    if (items.isEmpty()) View.VISIBLE else View.GONE
            }
        }.start()
    }
}
