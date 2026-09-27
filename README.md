# SyncNote — заметки Windows ↔ Android (Wi-Fi Direct)

Локальная синхронизация заметок между ПК Windows (главное хранилище)
и Android-телефоном. Без интернета и облака. Подробности решения —
`docs/architecture.md`, исходное ТЗ — `opencode-brief.md`.

Статус: этап 1 — окружение и каркас. Проверено фактически (2026-09-26).

## Проверенное окружение

- Windows 11 Pro 10.0.28000 x64
- .NET SDK 8.0.425 — `dotnet --info`
- Microsoft OpenJDK 17.0.20.1 (`JAVA_HOME`)
- Android SDK (`ANDROID_HOME`): cmdline-tools latest, platform-tools 37.0.1
  (adb 1.0.41), build-tools 34.0.0, platforms;android-34
- Gradle 8.7 (через `android/gradlew.bat`, скачивается при первом запуске)
- Kotlin 1.9.24, AGP 8.5.2, compile/targetSdk 34, minSdk 26

## Структура

- `SyncNote.sln`, `SyncNote.Core/` (net8.0, модель/хранение/синхронизация),
  `SyncNote.Windows/` (WPF, net8.0-windows), `SyncNote.Core.Tests/` (MSTest)
- `android/` — нативный Kotlin-клиент (`com.syncnote`)
- `protocol/` — спецификация sync-протокола и QR-сопряжения (этап 2+)
- `docs/architecture.md` — решение по стеку, Wi-Fi Direct, ограничения

## Сборка и тесты (проверено)

```powershell
$env:PATH="C:\Program Files\dotnet;"+$env:PATH
cd D:\OpenCode\SyncNote
dotnet build SyncNote.sln   # успешно: 0 предупреждений, 0 ошибок
dotnet test SyncNote.sln    # успешно: 1/1
```

```powershell
$env:JAVA_HOME="C:\Program Files\Microsoft\jdk-17.0.20.101-hotspot"
cd D:\OpenCode\SyncNote\android
.\gradlew.bat assembleDebug  # требует интернета для Gradle/AGP
```

## Известные ограничения

- Реальное Wi-Fi Direct соединение Windows↔Android не проверялось —
  нужны физический ПК с поддерживающим адаптером и физический телефон
  (эмулятор P2P не поддерживает). Места помечены `NOT-VERIFIED: needs hardware`.
- WinUI3/.NET MAUI намеренно не используются: нужен неустановленный Windows SDK.
- Релизный APK не подписывается (нет ключей) — только `assembleDebug`.
- В логах запрещены содержимое заметок, токены сопряжения и ключи.
