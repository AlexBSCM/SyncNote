-- SyncNote v2 Schema Definition (единый источник истины для WPF и Android).
-- Типы только стандартные SQLite: TEXT, INTEGER, BLOB.
-- updated_at: ISO8601 UTC (e.g. 2026-09-29T10:00:00Z).
PRAGMA foreign_keys = ON;

-- 1. Notes (текстовые заметки)
CREATE TABLE IF NOT EXISTS notes (
    id TEXT PRIMARY KEY,          -- UUID без дефисов
    title TEXT NOT NULL DEFAULT '',
    body TEXT NOT NULL DEFAULT '',
    rev INTEGER NOT NULL DEFAULT 1,
    updated_at TEXT NOT NULL,     -- ISO8601 UTC
    author_device TEXT NOT NULL,  -- Имя устройства автора последней правки
    is_deleted INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_notes_updated ON notes(updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_notes_deleted ON notes(is_deleted);

-- 2. Files (отдельные файлы вне заметок)
CREATE TABLE IF NOT EXISTS files (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,           -- Отображаемое имя
    mime TEXT NOT NULL DEFAULT 'application/octet-stream',
    size INTEGER NOT NULL DEFAULT 0,
    sha256 TEXT NOT NULL,         -- Хеш контента (ключ дедупликации)
    stored_name TEXT NOT NULL,    -- Имя файла на диске (= sha256.hex)
    rev INTEGER NOT NULL DEFAULT 1,
    updated_at TEXT NOT NULL,
    author_device TEXT NOT NULL,
    is_deleted INTEGER NOT NULL DEFAULT 0
);
-- ВНИМАНИЕ: индекс НЕ unique (отклонение от черновика ТЗ).
-- Копия конфликта хранит ТОТ ЖЕ sha256, что и оригинал, обе строки живые —
-- частичный UNIQUE INDEX ... WHERE is_deleted = 0 ронял бы INSERT копии
-- и убивал весь механизм конфликтов. Дедуп — на уровне приложения
-- (один физический файл на sha) и физического хранилища.
CREATE INDEX IF NOT EXISTS idx_files_sha ON files(sha256);
CREATE INDEX IF NOT EXISTS idx_files_updated ON files(updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_files_deleted ON files(is_deleted);

-- 3. Attachments (вложения внутри заметок)
CREATE TABLE IF NOT EXISTS attachments (
    id TEXT PRIMARY KEY,
    note_id TEXT NOT NULL,        -- Ссылка на notes.id
    name TEXT NOT NULL,
    mime TEXT NOT NULL,
    size INTEGER NOT NULL,
    sha256 TEXT NOT NULL,
    stored_name TEXT NOT NULL,    -- То же правило: имя файла = sha256
    rev INTEGER NOT NULL DEFAULT 1,
    updated_at TEXT NOT NULL,
    author_device TEXT NOT NULL,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY(note_id) REFERENCES notes(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_attachments_note ON attachments(note_id);
CREATE INDEX IF NOT EXISTS idx_attachments_sha ON attachments(sha256);

-- 4. Sync State (локальное состояние синхронизации)
CREATE TABLE IF NOT EXISTS sync_state (
    entity_type TEXT NOT NULL,    -- 'note' | 'file' | 'attachment'
    entity_id TEXT NOT NULL,      -- ID записи
    last_synced_rev INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(entity_type, entity_id)
);

-- 5. Seen Conflicts (идемпотентность конфликтов: одна копия на хеш)
CREATE TABLE IF NOT EXISTS seen_conflicts (
    conflict_id TEXT PRIMARY KEY, -- UUID самой копии конфликта
    original_entity_id TEXT NOT NULL,
    original_rev INTEGER NOT NULL,
    content_hash TEXT NOT NULL    -- SHA256 тела конфликта для проверки дубля
);

-- 6. Key Value Store (настройки приложения)
CREATE TABLE IF NOT EXISTS keyvalue (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
INSERT OR IGNORE INTO keyvalue (key, value) VALUES ('schema_version', '1');
INSERT OR IGNORE INTO keyvalue (key, value) VALUES ('device_id', ''); -- Заполняется при первом запуске
