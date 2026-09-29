package com.syncnote.v2.data

import android.content.ContentValues
import com.syncnote.v2.domain.SyncState
import java.io.File
import java.util.UUID

// SQL-реализация SyncState поверх той же БД. Паритет C# SqlSyncStateStore.
class SqlSyncStateStore(private val dbFile: File) : SyncState {
    override fun getSyncRev(entityType: String, entityId: String): Long {
        openDb(dbFile).use { db ->
            db.rawQuery(
                "SELECT last_synced_rev FROM sync_state" +
                    " WHERE entity_type=? AND entity_id=?",
                arrayOf(entityType, entityId)).use { c ->
                if (!c.moveToFirst()) return 0
                return c.getLong(0)
            }
        }
    }

    override fun setSyncRev(entityType: String, entityId: String, rev: Long) {
        openDb(dbFile).use { db ->
            val v = ContentValues().apply {
                put("entity_type", entityType)
                put("entity_id", entityId)
                put("last_synced_rev", rev)
            }
            val rows = db.update("sync_state", v,
                "entity_type=? AND entity_id=?", arrayOf(entityType, entityId))
            if (rows == 0) db.insertOrThrow("sync_state", null, v)
        }
    }

    override fun noteSeenConflict(
        originalId: String, rev: Long, contentHash: String
    ): Boolean {
        openDb(dbFile).use { db ->
            db.rawQuery(
                "SELECT 1 FROM seen_conflicts" +
                    " WHERE original_entity_id=? AND original_rev=? AND content_hash=?",
                arrayOf(originalId, rev.toString(), contentHash)).use { c ->
                if (c.count > 0) return true
            }
            val v = ContentValues().apply {
                put("conflict_id", UUID.randomUUID().toString().replace("-", ""))
                put("original_entity_id", originalId)
                put("original_rev", rev)
                put("content_hash", contentHash)
            }
            db.insert("seen_conflicts", null, v)
            return false
        }
    }

    override fun hasLiveFileRef(sha256: String): Boolean {
        openDb(dbFile).use { db ->
            db.rawQuery(
                "SELECT 1 FROM files WHERE sha256=? AND is_deleted=0 LIMIT 1",
                arrayOf(sha256.lowercase())).use { c ->
                return c.count > 0
            }
        }
    }
}
