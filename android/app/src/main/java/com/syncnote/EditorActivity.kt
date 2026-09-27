package com.syncnote

import android.os.Bundle
import android.widget.Button
import android.widget.EditText
import androidx.appcompat.app.AppCompatActivity

class EditorActivity : AppCompatActivity() {
    companion object {
        const val EXTRA_NOTE_ID = "note_id"
    }

    private lateinit var repo: NotesRepository
    private var noteId: String? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_editor)
        repo = NotesRepository(this)
        noteId = intent.getStringExtra(EXTRA_NOTE_ID)

        val note = noteId?.let { repo.get(it) }
        findViewById<EditText>(R.id.titleBox).setText(note?.title ?: "")
        findViewById<EditText>(R.id.bodyBox).setText(note?.body ?: "")

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
    }
}
