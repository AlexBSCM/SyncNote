namespace SyncNote.Core;

// Глобальный флаг подсистемы «Отдельные файлы» (этап F).
// false = поведение как до этапа: новые пути кода отказываются работать,
// старые пути (заметки, вложения) не меняются.
// Переменная окружения SYNCNOTE_ENABLE_SEPARATE_FILES=0/false выключает
// флаг на старте (нужно UI-тестам: перезапустить процесс с env проще,
// чем менять static из другого процесса).
public static class FeatureFlags
{
    private static bool _enableSeparateFiles = ReadEnv();

    public static bool EnableSeparateFiles
    {
        get => _enableSeparateFiles;
        set => _enableSeparateFiles = value;
    }

    private static bool ReadEnv()
    {
        var v = Environment.GetEnvironmentVariable("SYNCNOTE_ENABLE_SEPARATE_FILES");
        return !(v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase));
    }
}
