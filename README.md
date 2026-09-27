# SyncNote — заметки Windows ↔ Android (Wi-Fi Direct)

Локальная синхронизация заметок между ПК Windows (главное хранилище)
и Android-телефоном. Без интернета и облака.

- ТЗ: `opencode-brief.md`
- Архитектура и ограничения Wi-Fi Direct: `docs/architecture.md`
- Протокол сопряжения и синхронизации: `protocol/pairing-and-sync.md`
- Ручные тесты на двух устройствах: `docs/manual-test.md`

## Что реализовано

Windows (WPF, `net8.0-windows`): список + поиск, редактор, форматирование
`**жирный**`/`*курсив*` с предпросмотром, чек-листы, вложения любых типов
(лимит 100 МБ, sha256, явные ошибки), QR сопряжения с одноразовым токеном,
клиент/сервер синхронизации, экран конфликтов (оставить текущую / взять
копию / оставить обе), состояния соединения, отмена.

Android (Kotlin, minSdk 26, targetSdk 34): список + поиск + редактор,
чек-листы, вложения (FileProvider), QR-сканер (CameraX + ML Kit), поиск ПК
по Wi-Fi Direct, клиент синхронизации, экран конфликтов.

Протокол: кадры длина+JSON, handshake по токену (5 минут, одноразовый),
доверенные устройства, идемпотентное применение, tombstone-удаления,
передача файлов чанками с sha256, обрыв не портит хранилище.

## Проверенное окружение

- Windows 11 Pro 10.0.28000 x64
- .NET SDK 8.0.425, Microsoft OpenJDK 17.0.20.1
- Android SDK: cmdline-tools latest, platform-tools 37.0.1, build-tools 34.0.0,
  platforms;android-34; Gradle 8.7, AGP 8.5.2, Kotlin 1.9.24

## Сборка и тесты

```powershell
$env:PATH="C:\Program Files\dotnet;"+$env:PATH
cd D:\OpenCode\SyncNote
dotnet build SyncNote.sln   # 0 ошибок
dotnet test SyncNote.sln    # 36/36
```

```powershell
$env:JAVA_HOME="C:\Program Files\Microsoft\jdk-17.0.20.101-hotspot"
cd D:\OpenCode\SyncNote\android
.\gradlew.bat assembleDebug testDebugUnitTest  # APK + 4/4 JVM-теста
```

Проверено фактически: сборка решения, 36 .NET-тестов (модель, поиск,
сериализация JSON, идемпотентность, конфликты, транспорт), запуск окна,
миграции БД v1→v5 на боевой базе, debug-APK 5.7 МБ, 4 JVM-теста движка.

## Сопряжение и синхронизация (петля для проверки)

1. Windows: «Сопряжение…» → сервер стартует, QR с `{host, port, token, exp}`.
2. Windows: «Синхронизировать…» → host 127.0.0.1, порт из окна сопряжения,
   токен из QR → «Готово».
3. Android на эмуляторе: «Синхронизация» → host 10.0.2.2, порт проброшен
   через `adb reverse tcp:ПОРТ tcp:ПОРТ`.

Настоящий Wi-Fi Direct (адрес GO вместо 127.0.0.1) — только на железе,
см. `docs/manual-test.md` T6.

## Известные ограничения

- Реальное P2P-соединение Windows↔Android не проверялось (нужны физические
  устройства; эмулятор P2P не поддерживает). Места помечены `NOT-VERIFIED`.
- Bluetooth-резерв не реализован (спека в `docs/architecture.md` §4).
- Статус «подключено/готово» показывается только после фактической передачи;
  loopback-сессии честно названы петлёй.
- Релизный APK не подписывается — только `assembleDebug`.
- В логах запрещены содержимое заметок, токены и ключи.
