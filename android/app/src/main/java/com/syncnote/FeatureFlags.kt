package com.syncnote

// Глобальный флаг подсистемы «Отдельные файлы» (этап F).
// false = поведение как до этапа: новые пути отказываются работать,
// старые пути (заметки, вложения) не меняются.
object FeatureFlags {
    @Volatile var enableSeparateFiles: Boolean = true
}
