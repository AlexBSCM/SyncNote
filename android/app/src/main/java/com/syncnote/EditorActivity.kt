package com.syncnote

import android.net.Uri
import android.os.Bundle
import android.provider.OpenableColumns
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
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
            finish()
        }
        findViewById<Button>(R.id.deleteButton).setOnClickListener {
            noteId?.let { repo.delete(it) }
            finish()
        }
        findViewById<Button>(R.id.addItemButton).setOnClickListener {
            val id = noteId ?: return@setOnClickListener
            val box = findViewById<EditText>(R.id.newItemBox)
            if (box.text.isNotBlank()) {
                repo.addChecklistItem(id, box.text.toString().trim())
                box.text.clear()
                loadChecklist()
            }
        }
        findViewById<Button>(R.id.addFileButton).setOnClickListener {
            pickFile.launch("*/*")
        }
    }

    private fun loadChecklist() {
        val id = noteId ?: return
        val box = findViewById<LinearLayout>(R.id.checklistBox)
        box.removeAllViews()
        for (item in repo.checklist(id)) {
            val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
            val check = CheckBox(this).apply {
                text = item.text
                isChecked = item.isChecked
                layoutParams = LinearLayout.LayoutParams(0,
                    LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
                setOnCheckedChangeListener { _, checked ->
                    item.isChecked = checked
                    repo.updateChecklistItem(item)
                }
            }
            val del = Button(this).apply {
                text = "×"
                setOnClickListener {
                    repo.deleteChecklistItem(item.id, id)
                    loadChecklist()
                }
            }
            row.addView(check)
            row.addView(del)
            box.addView(row)
        }
    }

    private fun loadAttachments() {
        val id = noteId ?: return
        val box = findViewById<LinearLayout>(R.id.attachmentsBox)
        box.removeAllViews()
        for (att in repo.attachments(id)) {
            val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
            val label = TextView(this).apply {
                text = "${att.fileName} (${att.sizeBytes} байт)"
                layoutParams = LinearLayout.LayoutParams(0,
                    LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            }
            val open = Button(this).apply {
                text = "Открыть"
                setOnClickListener { openAttachment(att) }
            }
            val del = Button(this).apply {
                text = "×"
                setOnClickListener {
                    repo.deleteAttachment(att.id)
                    loadAttachments()
                }
            }
            row.addView(label)
            row.addView(open)
            row.addView(del)
            box.addView(row)
        }
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
