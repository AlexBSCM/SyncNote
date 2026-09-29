"""Проверка docs/schema.sql: синтаксис, таблицы, индексы, сид версии
и ключевое свойство — две живые строки с одним sha (копия конфликта)
не должны нарушать схему (поэтому idx_files_sha НЕ unique).

Запуск: python tests/check_schema.py (нужен только Python 3 stdlib).
"""
import os
import sqlite3
import sys

SCHEMA = os.path.join(os.path.dirname(__file__), "..", "docs", "schema.sql")


def main() -> int:
    with open(SCHEMA, encoding="utf-8") as f:
        script = f.read()
    db = sqlite3.connect(":memory:")
    db.executescript(script)

    tables = {
        r[0] for r in db.execute(
            "SELECT name FROM sqlite_master WHERE type='table'").fetchall()
    }
    for t in ("notes", "files", "attachments", "sync_state",
              "seen_conflicts", "keyvalue"):
        assert t in tables, f"missing table {t}"

    indexes = {
        r[0] for r in db.execute(
            "SELECT name FROM sqlite_master WHERE type='index'").fetchall()
    }
    for i in ("idx_notes_updated", "idx_notes_deleted",
              "idx_files_sha", "idx_files_updated", "idx_files_deleted",
              "idx_attachments_note", "idx_attachments_sha"):
        assert i in indexes, f"missing index {i}"

    ver = db.execute(
        "SELECT value FROM keyvalue WHERE key='schema_version'").fetchone()
    assert ver is not None and ver[0] == "1", "schema_version must be '1'"

    # Конфликт-копия с тем же sha обязана вставляться (обе живые).
    db.execute(
        "INSERT INTO files(id, name, mime, size, sha256, stored_name,"
        " rev, updated_at, author_device, is_deleted)"
        " VALUES('a', 'a.bin', 'application/octet-stream', 3,"
        " 'ab', 'ab', 1, '2026-09-29T10:00:00Z', 'pc', 0)")
    db.execute(
        "INSERT INTO files(id, name, mime, size, sha256, stored_name,"
        " rev, updated_at, author_device, is_deleted)"
        " VALUES('b', 'a.bin (конфликт)', 'application/octet-stream', 3,"
        " 'ab', 'ab', 1, '2026-09-29T10:00:00Z', 'pc', 0)")
    n = db.execute(
        "SELECT COUNT(*) FROM files WHERE sha256='ab'"
        " AND is_deleted=0").fetchone()[0]
    assert n == 2, "conflict copy with same sha must be insertable"

    # FK attachments -> notes.
    db.execute(
        "INSERT INTO notes(id, title, body, rev, updated_at,"
        " author_device, is_deleted) VALUES('n', 't', 'b', 1,"
        " '2026-09-29T10:00:00Z', 'pc', 0)")
    db.execute(
        "INSERT INTO attachments(id, note_id, name, mime, size, sha256,"
        " stored_name, rev, updated_at, author_device, is_deleted)"
        " VALUES('a1', 'n', 'f', 'text/plain', 1, 'cd', 'cd', 1,"
        " '2026-09-29T10:00:00Z', 'pc', 0)")
    print("schema OK: 6 tables, 7 indexes, version=1, conflict-copy insertable")
    return 0


if __name__ == "__main__":
    sys.exit(main())
