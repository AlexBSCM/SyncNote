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
hello_ok    { t, serverDeviceId }                 // сервер принял
hello_err   { t, reason }                         // token неверный/истёк/использован
trust_list  / untrust — управление доверенными (только локально на сервере)
sync_begin  { t, deviceId, baseRev }              // начало сессии
note_upsert { t, note, checklist, attachments[] } // note: id/rev/title/body/updatedAt/author/baseRev
note_delete { t, id, rev, author }
file_begin  { t, attachmentId, name, size, sha256 }
file_chunk  { t, attachmentId, offset, data_b64 }
file_end    { t, attachmentId }
sync_end    { t }
conflict    { t, id, keptRev, copyId }            // уведомление о сохранённой копии
error       { t, reason }                         // явная ошибка, без молчаливых отказов
```

## 3. Правила применения (идемпотентность, конфликты)

- `(id, rev)` уже применён → ответ ok без изменений (повторы безопасны).
- Неизвестный `id` → вставка как есть (rev/author сохраняются).
- Известный `id`, `incoming.rev > local.rev`, `incoming.baseRev >= local.syncRev`
  → fast-forward (замена + checklist/attachments).
- Известный `id`, обе стороны правили после общей базы
  (`incoming.baseRev < local.syncRev` и `local.rev > local.syncRev`)
  → конфликт: локальная версия остаётся, входящая сохраняется КОПИЕЙ
  с новым id и пометкой; удаление/выбор копии не теряет данные.
- Вложения: приём во временный файл, проверка sha256, затем атомарное
  перемещение. Обрыв середины не портит хранилище.
- В логах запрещены содержимое заметок, токены и ключи.

## 4. Статусы соединения (UI обеих платформ)

`не подключено → ищем устройство → ожидаем подтверждение →
синхронизируется → готово`, плюс явные ошибки и отмена.
Статус «готово» — только после фактически состоявшейся передачи.
