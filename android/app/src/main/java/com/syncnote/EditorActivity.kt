package com.syncnote

import android.net.Uri
import android.os.Bundle
import android.provider.OpenableColumns
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import com.syncnote.databinding.ItemAttachmentRowBinding
import com.syncnote.databinding.ItemChecklistRowBinding
import java.io.File

class EditorActivity : AppCompatActivity() {
    companion object {
        const val EXTRA_NOTE_ID = "note_id"
    }

    private lateinit var repo: NotesRepository
    private var noteId: String? = null

    private val pickFile = registerForActivityResult(ActivityResultContracts.GetContent()) { uri: Uri? ->
        uri ?: return@registerForActivityResult
        val id = noteId ?: return@registerForActivityResult
        try {
            val (name, mime, size) = describe(uri)
            if (size > 100L * 1024 * 1024) {
                toast("Файл $name превышает лимит 100 МБ.")
                return@registerForActivityResult
            }
            val bytes = contentResolver.openInputStream(uri)?.use { it.readBytes() }
                ?: throw java.io.IOException("Не удалось прочитать файл.")
            repo.addAttachmentBytes(id, name, mime, bytes)
            loadAttachments()
            SyncAuto.trigger(this@EditorActivity)
        } catch (e: Exception) {
            toast("Не удалось добавить вложение: ${e.message}")
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_editor)
        repo = NotesRepository(this)
        noteId = intent.getStringExtra(EXTRA_NOTE_ID)

        val note = noteId?.let { repo.get(it) }
        findViewById<EditText>(R.id.titleBox).setText(note?.title ?: "")
        findViewById<EditText>(R.id.bodyBox).setText(note?.body ?: "")
        loadChecklist()
        loadAttachments()

        findViewById<Button>(R.id.saveButton).setOnClickListener {
            val id = noteId ?: return@setOnClickListener
            val current = repo.get(id) ?: return@setOnClickListener
            current.title = findViewById<EditText>(R.id.titleBox).text.toString()
            current.body = findViewById<EditText>(R.id.bodyBox).text.toString()
            repo.update(current)
            SyncAuto.trigger(this)
            finish()
        }
        findViewById<Button>(R.id.deleteButton).setOnClickListener {
            noteId?.let { repo.delete(it) }
            SyncAuto.trigger(this)
            finish()
        }
        findViewById<Button>(R.id.addItemButton).setOnClickListener {
            val id = noteId ?: return@setOnClickListener
            val box = findViewById<EditText>(R.id.newItemBox)
            if (box.text.isNotBlank()) {
                repo.addChecklistItem(id, box.text.toString().trim())
                box.text.clear()
                loadChecklist()
                SyncAuto.trigger(this@EditorActivity)
            }
        }
        findViewById<Button>(R.id.addFileButton).setOnClickListener {
            pickFile.launch("*/*")
        }
    }

    // Бэклог 1.d: строка пункта приходит из item_checklist_row.xml
    // (ViewBinding) вместо программной сборки LinearLayout+CheckBox+Button.
    // Логика та же: чекбокс переключает пункт, «×» удаляет его.
    private fun loadChecklist() {
        val id = noteId ?: return
        val box = findViewById<LinearLayout>(R.id.checklistBox)
        box.removeAllViews()
        val inflater = layoutInflater
        for (item in repo.checklist(id)) {
            val b = ItemChecklistRowBinding.inflate(inflater, box, false)
            b.checkItemBox.apply {
                text = item.text
                isChecked = item.isChecked
                setOnCheckedChangeListener { _, checked ->
                    item.isChecked = checked
                    repo.updateChecklistItem(item)
                }
            }
            b.deleteCheckBtn.setOnClickListener {
                repo.deleteChecklistItem(item.id, id)
                loadChecklist()
            }
            box.addView(b.root)
        }
    }

    // Бэклог 1.d: строка вложения из item_attachment_row.xml.
    // Иконка и размер файла берутся из FileUiModel — те же, что на
    // вкладке «Файлы», чтобы вид совпадал.
    private fun loadAttachments() {
        val id = noteId ?: return
        val box = findViewById<LinearLayout>(R.id.attachmentsBox)
        box.removeAllViews()
        val inflater = layoutInflater
        for (att in repo.attachments(id)) {
            val b = ItemAttachmentRowBinding.inflate(inflater, box, false)
            val emoji = FileUiModel.iconFor(att.mimeType).first
            b.attachIcon.text = emoji
            b.fileName.text = att.fileName
            b.fileMeta.text = "${FileUiModel.formatSize(att.sizeBytes)} • ${att.mimeType}"
            b.btnOpen.setOnClickListener { openAttachment(att) }
            b.btnSave.setOnClickListener { downloadAttachment(att) }
            b.btnDelete.setOnClickListener {
                repo.deleteAttachment(att.id)
                loadAttachments()
            }
            box.addView(b.root)
        }
    }

    private var pendingDownload: Attachment? = null

    private val saveFile = registerForActivityResult(
        ActivityResultContracts.CreateDocument("*/*")) { uri: Uri? ->
        val att = pendingDownload ?: return@registerForActivityResult
        pendingDownload = null
        if (uri == null) return@registerForActivityResult
        try {
            val src = File(repo.filesDir(), att.storedName)
            contentResolver.openOutputStream(uri)?.use { out ->
                src.inputStream().use { it.copyTo(out) }
            }
            toast("Сохранено: ${att.fileName}")
        } catch (e: Exception) {
            toast("Не удалось скачать: ${e.message}")
        }
    }

    private fun downloadAttachment(att: Attachment) {
        pendingDownload = att
        saveFile.launch(att.fileName)
    }

    private fun openAttachment(att: Attachment) {
        try {
            val file = File(repo.filesDir(), att.storedName)
            val uri = androidx.core.content.FileProvider.getUriForFile(
                this, "$packageName.provider", file)
            val intent = android.content.Intent(android.content.Intent.ACTION_VIEW)
                .setDataAndType(uri, att.mimeType)
                .addFlags(android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION)
            startActivity(android.content.Intent.createChooser(intent, "Открыть"))
        } catch (e: Exception) {
            toast("Не удалось открыть вложение: ${e.message}")
        }
    }

    private fun describe(uri: Uri): Triple<String, String, Long> {
        var name = "file"
        var size = -1L
        contentResolver.query(uri, null, null, null, null)?.use { c ->
            if (c.moveToFirst()) {
                val ni = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                if (ni >= 0) name = c.getString(ni)
                val si = c.getColumnIndex(OpenableColumns.SIZE)
                if (si >= 0) size = c.getLong(si)
            }
        }
        val mime = contentResolver.getType(uri) ?: "application/octet-stream"
        return Triple(name, mime, size)
    }

    private fun toast(t: String) =
        Toast.makeText(this, t, Toast.LENGTH_LONG).show()
}
