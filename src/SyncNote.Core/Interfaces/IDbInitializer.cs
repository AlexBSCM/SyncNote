namespace SyncNote.Core.Interfaces;

public interface IDbInitializer
{
    /// <summary>
    /// Инициализирует базу данных по указанному пути.
    /// Применяет схему, если версия ниже целевой.
    /// </summary>
    void Initialize(string dbPath);

    /// <summary>
    /// Возвращает текущую версию схемы в БД.
    /// </summary>
    int GetCurrentVersion(string dbPath);
}
