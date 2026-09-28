using System.Security.Cryptography;

namespace SyncNote.Core;

// Ввод-вывод отдельных файлов (этап F.2).
// Ключевые решения:
// - Дедуп: физическое имя файла на диске ВСЕГДА равно hex sha256 содержимого.
//   Повторный импорт того же контента не копирует байты.
// - Стриминг: хеш считается инкрементально чанками (1 МБ), файл целиком
//   в RAM не грузится. Сначала проход хеширования, затем — только если
//   файла ещё нет — проход копирования. Два последовательных чтения
//   диска вместо удержания 100 МБ в памяти.
// - Атомарность: запись во временный файл + File.Move.
// - Лимит 100 МБ проверяется ДО чтения (по FileInfo) и во время стриминга.
public sealed class FileTooLargeException(string fileName, long size, long limit)
    : IOException($"Файл '{fileName}' ({size} байт) превышает лимит {limit} байт.")
{
    public string FileName { get; } = fileName;
    public long Size { get; } = size;
    public long Limit { get; } = limit;
}

public sealed record FileImportResult(string StoredName, long SizeBytes, string Sha256Hex);

public static class FileIo
{
    public const long MaxFileSizeBytes = 100L * 1024 * 1024;

    // Размер чанка потокового чтения: баланс syscall/память.
    private const int ChunkBytes = 1024 * 1024;

    // Импорт файла с дедупликацией. Возвращает (storedName == sha256hex, size, sha256hex).
    // Если контент уже есть на диске — байты не копируются.
    public static FileImportResult ImportFromFile(string filesDir, string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Файл не найден: {sourcePath}", sourcePath);
        var fileName = Path.GetFileName(sourcePath);

        // Быстрый отказ по размеру до чтения.
        var declared = new FileInfo(sourcePath).Length;
        if (declared > MaxFileSizeBytes)
            throw new FileTooLargeException(fileName, declared, MaxFileSizeBytes);

        // Проход 1: инкрементальный хеш чанками, с контролем лимита на случай
        // растущего файла (TOCTOU между FileInfo и чтением).
        string hex;
        long size;
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        using (var src = File.OpenRead(sourcePath))
        {
            var buf = new byte[ChunkBytes];
            size = 0;
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                size += n;
                if (size > MaxFileSizeBytes)
                    throw new FileTooLargeException(fileName, size, MaxFileSizeBytes);
                hash.AppendData(buf, 0, n);
            }
            hex = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        Directory.CreateDirectory(filesDir);
        var dest = Path.Combine(filesDir, hex);

        // Дедуп: файл уже есть — копирование пропускаем.
        if (File.Exists(dest))
            return new FileImportResult(hex, size, hex);

        // Проход 2: атомарное копирование через временный файл.
        // Уникальный суффикс защищает от гонки двух импортов одного контента:
        // победитель делает Move, проигравший чистит свой tmp.
        var tmp = Path.Combine(filesDir, hex + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var src = File.OpenRead(sourcePath))
            using (var dst = File.Create(tmp))
            {
                src.CopyTo(dst);
            }
            File.Move(tmp, dest);
        }
        catch
        {
            // Не оставляем повреждённый temp-файл (целостность хранилища).
            try { File.Delete(tmp); } catch { }
            throw;
        }
        return new FileImportResult(hex, size, hex);
    }

    // Полный путь к хранимому файлу по хешу. Кидает FileNotFoundException,
    // если физического файла нет (повреждение хранилища — явно, не молча).
    public static string ResolveStoragePath(string filesDir, string sha256Hex)
    {
        // Нормализация: только hex, без разделителей — защита от path traversal.
        if (sha256Hex.Length != 64 || !sha256Hex.All(Uri.IsHexDigit))
            throw new ArgumentException($"Некорректный sha256: {sha256Hex}", nameof(sha256Hex));
        var path = Path.Combine(filesDir, sha256Hex.ToLowerInvariant());
        if (!File.Exists(path))
            throw new FileNotFoundException($"Файл хранилища отсутствует: {sha256Hex}", path);
        return path;
    }

    // Удаляет физический файл, только если на него нет живых ссылок.
    // Зависимость от БД — через callback, чтобы статик-класс не тянул Store.
    // Возвращает true, если файл был удалён.
    public static bool DeleteIfOrphaned(string filesDir, string sha256Hex, Func<string, bool> hasLiveRefs)
    {
        if (hasLiveRefs(sha256Hex))
            return false;
        var path = Path.Combine(filesDir, sha256Hex.ToLowerInvariant());
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }
}
