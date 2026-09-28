# SyncNote — протокол сопряжения и синхронизации v1

Транспорт: TCP-сокет поверх Wi-Fi Direct (GO) или Bluetooth RFCOMM.
Формат кадров: 4 байта big-endian длина + UTF-8 JSON. Файлы — отдельными
чанками с sha256 (см. «Передача вложений»).

Bluetooth: общий UUID сервиса `8f6d4b2a-1c3e-4a5f-9b6d-7e8f9a0b1c2d`
(SPP-подобный RFCOMM). ПК принимает (`BluetoothServer`), телефон
подключается после спаривания в настройках ОС. Сессия та же
(`StreamSession` / `runOverStreams`).

## 1. QR сопряжения (доверенное, одноразовое)

Windows показывает QR с JSON (пример):

```json
{
  "v": 1,
  "ssid": "DIRECT-AB-SyncNote",
  "token": "mK3f9X2vbD8sQ7wE4rT6yA",
  "exp": 1729948800
}
```

- `token` — 128-битный nonce (Base64Url, 22 символа), TTL 5 минут,
  одноразовый. Заметок и постоянных секретов в QR нет.
- `ssid` — подсказка для поиска P2P-устройства (не пароль).
- `exp` — unix time истечения.

## 2. Сообщения (JSON, поле `t` — тип)

```
hello       { t, deviceId, deviceName, token? }   // первое сообщение клиента
hello_ok    { t, serverDeviceId, caps: [...] }    // сервер принял; caps вида ["files-v1"]
hello_err   { t, reason }                         // token неверный/истёк/использован
sync_begin  { t, knowledge: [{id, rev}],          // ревизии клиента
              file_knowledge: [{id, rev}],        // ревизии файлов клиента
              caps: [...] }                       // возможности клиента
note_upsert { t, note, checklist, attachments[] } // note: id/rev/title/body/updatedAt/author/baseRev
note_delete { t, id, rev, author }                // УСТАРЕЛО: удаления едут как note_upsert с isDeleted
file_begin  { t, sha, name, mime, size }          // байты файла чанками...
file_chunk  { t, sha, data_b64 }                  // ...привязаны к sha, не к id
file_end    { t, sha }                            // сверка sha256, затем атомарный move
file_register { t, file: {...SyncFileDto...} }    // метаданные standalone-файла
query_has_hash { t, sha256 }                      // "есть ли у тебя такой контент?"
hash_response { t, sha256, has }                  // ответ
applied     { t, id, result, copyId? }            // квитанция
sync_end    { t }
conflict    { t, id, keptRev, copyId }            // уведомление о сохранённой копии
error       { t, reason }                         // явная ошибка, без молчаливых отказов
```

Совместимость (строго):
- Новые кадры (`file_register`, `query_has_hash`, `hash_response`,
  `file_knowledge`, `caps`) шлются только при совпадении caps `files-v1`
  с обеих сторон. Без совпадения сессия идёт ровно по старому протоколу.
- Неизвестный тип кадра — тихий игнор с Warning в лог, сессия продолжается.
  Отсутствующие поля (`caps`, `file_knowledge`) читаются как пустые.

## 3. Правила применения (идемпотентность, конфликты)

- `(id, rev)` уже применён → ответ ok без изменений (повторы безопасны).
- Неизвестный `id` → вставка как есть (rev/author сохраняются).
- Известный `id`, `incoming.rev > local.rev`, `incoming.baseRev >= local.syncRev`
  → fast-forward (замена + checklist/attachments).
- Известный `id`, обе стороны правили после общей базы
  (`incoming.baseRev < local.syncRev` и `local.rev > local.syncRev`)
  → конфликт: локальная версия остаётся, входящая сохраняется КОПИЕЙ
  с новым id и пометкой; удаление/выбор копии не теряет данные.
- Повтор той же конфликтной версии копий не плодит: запоминается
  `(id, rev, sha256 содержимого)` — seen_conflicts.
- Экономия трафика: клиент не шлёт заметки без правок (`rev <= syncRev`);
  сервер не шлёт то, что у клиента новее или взаимно синхронно;
  после квитанции `applied` обе стороны двигают `syncRev`.
- Вложения: приём во временный файл, проверка sha256, затем атомарное
  перемещение. Обрыв середины не портит хранилище.
- В логах запрещены содержимое заметок, токены и ключи.

## 4. Статусы соединения (UI обеих платформ)

`не подключено → ищем устройство → ожидаем подтверждение →
синхронизируется → готово`, плюс явные ошибки и отмена.
Статус «готово» — только после фактически состоявшейся передачи.
