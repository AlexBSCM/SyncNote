package com.syncnote

import android.net.Uri
import android.os.Bundle
import android.provider.OpenableColumns
import android.text.Editable
import android.text.TextWatcher
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.fragment.app.Fragment
import androidx.lifecycle.lifecycleScope
import androidx.recyclerview.widget.LinearLayoutManager
import com.syncnote.databinding.FragmentFilesListBinding
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.security.MessageDigest

// Экран «Файлы»: список + поиск + SAF-импорт. Прямые вызовы NotesRepository,
// как в NotesFragment (без ViewModel — стиль проекта).
class FilesListFragment : Fragment() {
    private var _b: FragmentFilesListBinding? = null
    private val b get() = _b!!
    private lateinit var repo: NotesRepository
    private lateinit var adapter: FilesAdapter
    private var all: List<FileUiModel> = emptyList()

    override fun onCreateView(
        inflater: LayoutInflater, parent: ViewGroup?, state: Bundle?
    ): View {
        _b = FragmentFilesListBinding.inflate(inflater, parent, false)
        return b.root
    }

    override fun onDestroyView() {
        _b = null
        super.onDestroyView()
    }

    override fun onViewCreated(view: View, state: Bundle?) {
        repo = NotesRepository(requireContext())
        adapter = FilesAdapter { id -> openDetails(id) }
        b.rvFiles.layoutManager = LinearLayoutManager(requireContext())
        b.rvFiles.adapter = adapter
        b.searchFiles.addTextChangedListener(object : TextWatcher {
            override fun afterTextChanged(s: Editable?) =
                applyFilter(s?.toString().orEmpty())
            override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
        })
        b.fabAdd.setOnClickListener { openDoc.launch(arrayOf("*/*")) }
        b.emptyAddButton.setOnClickListener { openDoc.launch(arrayOf("*/*")) }
    }

    override fun onResume() {
        super.onResume()
        loadFiles()
        SyncAuto.trigger(requireContext())
    }

    // Перечитать БД в адаптер + empty state. Вызывается из onResume,
    // после импорта/удаления и из FileDetailsBottomSheet.
    fun loadFiles() {
        val v = _b ?: return
        val entries = try {
            repo.getFiles(includeDeleted = false)
        } catch (e: Exception) {
            toast("${getString(R.string.files_load_failed)} ${e.message}")
            return
        }
        all = entries.map {
            try {
                FileUiModel.from(it, repo.getFileSyncRev(it.id))
            } catch (_: Exception) {
                FileUiModel.from(it, 0)
            }
        }
        applyFilter(v.searchFiles.text.toString())
    }

    private fun applyFilter(q: String) {
        val shown = if (q.isBlank()) all
            else all.filter { it.name.contains(q, ignoreCase = true) }
        adapter.submitList(shown)
        val empty = shown.isEmpty()
        b.emptyState.visibility = if (empty) View.VISIBLE else View.GONE
        b.rvFiles.visibility = if (empty) View.GONE else View.VISIBLE
    }

    private fun openDetails(id: String) {
        FileDetailsBottomSheet.newInstance(id)
            .show(childFragmentManager, "file-details")
    }

    private val openDoc =
        registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri: Uri? ->
            if (uri != null) importUri(uri)
        }

    private fun importUri(uri: Uri) {
        val ctx = requireContext()
        lifecycleScope.launch(Dispatchers.IO) {
            try {
                val cr = ctx.contentResolver
                var name = "file"
                var size = -1L
                cr.query(uri, null, null, null, null)?.use { c ->
                    if (c.moveToFirst()) {
                        val ni = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                        if (ni >= 0) c.getString(ni)?.let { name = it }
                        val si = c.getColumnIndex(OpenableColumns.SIZE)
                        if (si >= 0) size = c.getLong(si)
                    }
                }
                val mime = cr.getType(uri) ?: "application/octet-stream"
                if (size > FileIo.MAX_FILE_SIZE_BYTES)
                    throw FileTooLargeException(name, size, FileIo.MAX_FILE_SIZE_BYTES)
                val bytes = cr.openInputStream(uri)?.use { it.readBytes() }
                    ?: throw java.io.IOException("Не удалось открыть файл.")
                if (bytes.size > FileIo.MAX_FILE_SIZE_BYTES)
                    throw FileTooLargeException(name, bytes.size.toLong(), FileIo.MAX_FILE_SIZE_BYTES)
                val sha = MessageDigest.getInstance("SHA-256")
                    .digest(bytes).joinToString("") { "%02x".format(it) }
                // UI-дедуп: та же запись уже в списке — вторую строку не создаём
                // (физически байты и так общие через content-hash).
                if (repo.getFiles(includeDeleted = false)
                        .any { it.sha256.equals(sha, ignoreCase = true) && it.name == name }
                ) {
                    withContext(Dispatchers.Main) {
                        toast(getString(R.string.files_already_exists))
                        loadFiles()
                    }
                    return@launch
                }
                repo.addFile(name, mime, bytes)
                withContext(Dispatchers.Main) {
                    loadFiles()
                    toast(getString(R.string.files_added))
                }
            } catch (e: Exception) {
                withContext(Dispatchers.Main) {
                    toast("${getString(R.string.files_add_failed)} ${e.message}")
                }
            }
        }
    }

    private fun toast(t: String) {
        if (isAdded) Toast.makeText(requireContext(), t, Toast.LENGTH_LONG).show()
    }
}
