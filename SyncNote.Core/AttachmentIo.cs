using System.Security.Cryptography;

namespace SyncNote.Core;

// Явный лимит вложения. Превышение — исключение, никогда молчаливый отказ.
public sealed class AttachmentTooLargeException(string fileName, long size, long limit)
    : IOException($"Файл '{fileName}' ({size} байт) превышает лимит {limit} байт.")
{
    public string FileName { get; } = fileName;
    public long Size { get; } = size;
    public long Limit { get; } = limit;
}

public static class AttachmentIo
{
    public const long MaxAttachmentBytes = 100L * 1024 * 1024;

    public static string SanitizeFileName(string name)
    {
        var clean = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
        return string.IsNullOrWhiteSpace(clean) ? "file" : clean;
    }

    public static string MimeByExtension(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".html" or ".htm" => "text/html",
            ".json" => "application/json",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".mp3" => "audio/mpeg",
            ".mp4" => "video/mp4",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };

    // Копирует файл в каталог хранилища атомарно (temp + move), считает sha256.
    // Content-addressed: физическое имя ВСЕГДА равно hex sha256 содержимого,
    // повторный импорт того же контента байты не копирует (дедуп).
    // Возвращает (storedName == sha256hex, size, sha256). Ошибки — наружу.
    public static (string StoredName, long Size, string Sha256) CopyIn(
        string filesDir, Guid attachmentId, string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Файл не найден: {sourcePath}", sourcePath);
        var size = new FileInfo(sourcePath).Length;
        var fileName = Path.GetFileName(sourcePath);
        if (size > MaxAttachmentBytes)
            throw new AttachmentTooLargeException(fileName, size, MaxAttachmentBytes);
        Directory.CreateDirectory(filesDir);
        string hex;
        using (var sha = SHA256.Create())
        using (var src = File.OpenRead(sourcePath))
        {
            var buf = new byte[1024 * 1024];
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
                sha.TransformBlock(buf, 0, n, null, 0);
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            hex = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
        var dest = Path.Combine(filesDir, hex);
        if (File.Exists(dest))
            return (hex, size, hex);
        var tmp = Path.Combine(filesDir, hex + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.Copy(sourcePath, tmp);
            File.Move(tmp, dest);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        return (hex, size, hex);
    }
}
