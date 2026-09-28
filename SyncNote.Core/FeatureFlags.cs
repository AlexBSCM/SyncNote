namespace SyncNote.Core;

// Глобальный флаг подсистемы «Отдельные файлы» (этап F).
// false = поведение как до этапа: новые пути кода отказываются работать,
// старые пути (заметки, вложения) не меняются.
public static class FeatureFlags
{
    public static bool EnableSeparateFiles { get; set; } = true;
}
