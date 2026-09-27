namespace SyncNote.Core;

// Диагностика сервера: только события и счётчики, БЕЗ содержимого заметок,
// токенов и ключей.
public static class ServerLog
{
    private static readonly object _lock = new();
    private static string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SyncNote", "server.log");

    public static void Line(string text)
    {
        try
        {
            lock (_lock)
                File.AppendAllText(_path,
                    $"{DateTime.UtcNow:HH:mm:ss} {text}{Environment.NewLine}");
        }
        catch { }
    }
}
