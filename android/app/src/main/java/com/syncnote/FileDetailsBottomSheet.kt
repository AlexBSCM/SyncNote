package com.syncnote

import android.content.Intent
import android.graphics.BitmapFactory
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.core.content.ContextCompat
import androidx.core.content.FileProvider
import androidx.lifecycle.lifecycleScope
import com.google.android.material.bottomsheet.BottomSheetDialogFragment
import com.syncnote.databinding.BottomSheetFileDetailsBinding
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

// Детали файла: шапка + превью + спойлер + действия.
// Открывается из FilesListFragment, обновляет его после удаления.
class FileDetailsBottomSheet : BottomSheetDialogFragment() {
    companion object {
        private const val ARG_ID = "fileId"
        fun newInstance(fileId: String) = FileDetailsBottomSheet().apply {
            arguments = Bundle().apply { putString(ARG_ID, fileId) }
        }
    }

    override fun getTheme() = R.style.Theme_SyncNote_FilesBottomSheet

    private var _b: BottomSheetFileDetailsBinding? = null
    private val b get() = _b!!
    private lateinit var repo: NotesRepository
    private var entry: FileEntry? = null
    private var infoOpen = false

    override fun onCreateView(
        inflater: LayoutInflater, parent: ViewGroup?, state: Bundle?
    ): View {
        _b = BottomSheetFileDetailsBinding.inflate(inflater, parent, false)
        return b.root
    }

    override fun onDestroyView() {
        _b = null
        super.onDestroyView()
    }

    override fun onViewCreated(view: View, state: Bundle?) {
        repo = NotesRepository(requireContext())
        val id = requireArguments().getString(ARG_ID)
        val e = id?.let { repo.tryGetFile(it) }
        if (e == null) {
            toast(getString(R.string.files_not_found))
            dismissAllowingStateLoss()
            return
        }
        entry = e
        val m = FileUiModel.from(e, repo.getFileSyncRev(e.id))
        val ctx = requireContext()
        b.detailIcon.text = m.iconEmoji
        b.detailIcon.background = roundedBox(ctx,
            ContextCompat.getColor(ctx, m.iconBgRes), 12f)
        b.detailIcon.setTextColor(ContextCompat.getColor(ctx, m.iconFgRes))
        b.detailName.text = e.name
        b.detailSub.text = "${e.mime} • ${FileUiModel.formatSize(e.sizeBytes)}"
        updateToggle()
        b.infoToggle.setOnClickListener {
            infoOpen = !infoOpen
            updateToggle()
        }
        b.infoBody.text = buildInfo(e, m)
        loadPreview(e)
        b.btnOpen.setOnClickListener { openFile(e) }
        b.btnSaveAs.setOnClickListener { saveDoc.launch(e.name) }
        b.btnDelete.setOnClickListener { confirmDelete(e) }
    }

    private fun updateToggle() {
        _b?.infoToggle?.text =
            getString(R.string.files_info_title) + if (infoOpen) " ▲" else " ▼"
        _b?.infoBody?.visibility = if (infoOpen) View.VISIBLE else View.GONE
    }

    private fun buildInfo(e: FileEntry, m: FileUiModel): String {
        val sha = e.sha256
        return "Размер: ${e.sizeBytes} байт (${FileUiModel.formatSize(e.sizeBytes)})\n" +
            "MIME: ${e.mime}\n" +
            "SHA256: ${sha.take(16)}...\n" +
            "Ревизия: #${e.rev} (синхр: #${repo.getFileSyncRev(e.id)})\n" +
            "Автор: ${e.authorDeviceId}\n" +
            "Изменён: ${FileUiModel.formatDate(e.updatedAt)}\n" +
            "Статус: ${getString(m.badge.textRes)}"
    }

    private fun loadPreview(e: FileEntry) {
        if (!e.mime.startsWith("image/")) return // заглушка остаётся
        viewLifecycleOwner.lifecycleScope.launch(Dispatchers.IO) {
            try {
                val f = File(repo.filesDir(), e.storedName)
                val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                BitmapFactory.decodeFile(f.absolutePath, bounds)
                var sample = 1
                while (maxOf(bounds.outWidth, bounds.outHeight) / sample > 1024) sample *= 2
                val opts = BitmapFactory.Options().apply { inSampleSize = sample }
                val bmp = BitmapFactory.decodeFile(f.absolutePath, opts)
                withContext(Dispatchers.Main) {
                    val vb = _b ?: return@withContext
                    if (bmp != null) {
                        vb.previewImage.setImageBitmap(bmp)
                        vb.previewImage.visibility = View.VISIBLE
                        vb.previewPlaceholder.visibility = View.GONE
                    }
                }
            } catch (_: Exception) {
                // Заглушка остаётся.
            }
        }
    }

    private fun openFile(e: FileEntry) {
        try {
            val file = File(repo.filesDir(), e.storedName)
            val uri = FileProvider.getUriForFile(
                requireContext(), "${requireContext().packageName}.provider", file)
            val intent = Intent(Intent.ACTION_VIEW)
                .setDataAndType(uri, e.mime)
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            startActivity(Intent.createChooser(intent, e.name))
        } catch (ex: Exception) {
            toast("${getString(R.string.files_open_failed)} ${ex.message}")
        }
    }

    private var pendingSave: FileEntry? = null

    private val saveDoc =
        registerForActivityResult(ActivityResultContracts.CreateDocument("*/*")) { uri ->
            val e = pendingSave ?: return@registerForActivityResult
            pendingSave = null
            if (uri == null) return@registerForActivityResult
            try {
                val src = File(repo.filesDir(), e.storedName)
                requireContext().contentResolver.openOutputStream(uri)?.use { out ->
                    src.inputStream().use { it.copyTo(out) }
                }
                toast(getString(R.string.files_saved, e.name))
            } catch (ex: Exception) {
                toast("${getString(R.string.files_save_failed)} ${ex.message}")
            }
        }

    private fun confirmDelete(e: FileEntry) {
        AlertDialog.Builder(requireContext())
            .setTitle(getString(R.string.files_delete_title))
            .setMessage(getString(R.string.files_delete_message))
            .setPositiveButton(getString(R.string.files_delete)) { _, _ ->
                try {
                    repo.deleteFile(e.id)
                    toast(getString(R.string.files_deleted))
                } catch (ex: Exception) {
                    toast(ex.message ?: getString(R.string.files_add_failed))
                }
                dismissAllowingStateLoss()
                (parentFragment as? FilesListFragment)?.loadFiles()
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun toast(t: String) {
        if (isAdded) Toast.makeText(requireContext(), t, Toast.LENGTH_LONG).show()
    }
}
