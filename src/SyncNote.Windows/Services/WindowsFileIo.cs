using System.IO;
using SyncNote.Core.Interfaces;
using SyncNote.Core.Utils;

namespace SyncNote.Windows.Services;

// Content-addressed хранилище: %LOCALAPPDATA%\SyncNote_v2\files\{sha256_hex}.
// Дедуп: повторный импорт того же контента физическую копию не создаёт.
public sealed class WindowsFileIo : IFileIo
{
    private readonly string _root;

    public WindowsFileIo()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncNote_v2", "files"))
    {
    }

    public WindowsFileIo(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public string ComputeSha256(Stream stream) => HashUtils.ComputeSha256(stream);

    public string ComputeSha256(byte[] data) => HashUtils.ComputeSha256(data);

    public async Task<(string sha256, long size)> ImportAsync(
        string sourcePath, CancellationToken ct = default)
    {
        string sha;
        long size;
        string tmp = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            await using (var src = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, useAsync: true))
            {
                await using (var dst = new FileStream(
                    tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1024 * 1024, useAsync: true))
                {
                    using var hash = System.Security.Cryptography.SHA256.Create();
                    var buf = new byte[1024 * 1024];
                    size = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf.AsMemory(), ct)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        hash.TransformBlock(buf, 0, n, null, 0);
                        await dst.WriteAsync(buf.AsMemory(0, n), ct);
                        size += n;
                    }
                    hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    sha = Convert.ToHexString(hash.Hash!).ToLowerInvariant();
                }
            }
            string dest = GetStoragePath(sha);
            if (!File.Exists(dest))
                File.Move(tmp, dest);
            else
                File.Delete(tmp); // дедуп: байты уже есть
            return (sha, size);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    public bool ExistsInStorage(string sha256)
    {
        try { return File.Exists(GetStoragePath(sha256)); }
        catch { return false; }
    }

    public Stream OpenRead(string sha256) =>
        new FileStream(GetStoragePath(sha256),
            FileMode.Open, FileAccess.Read, FileShare.Read);

    public void DeleteFromStorage(string sha256)
    {
        try
        {
            string path = GetStoragePath(sha256);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    public string GetStoragePath(string sha256)
    {
        string norm = (sha256 ?? "").ToLowerInvariant();
        if (norm.Length != 64 || !norm.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            throw new ArgumentException($"Некорректный sha256: {sha256}.", nameof(sha256));
        return Path.Combine(_root, norm);
    }
}
