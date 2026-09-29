using System;
using System.IO;
using System.Security.Cryptography;

namespace SyncNote.Core.Utils;

// Чистое хеширование без зависимостей платформы.
public static class HashUtils
{
    public static string ComputeSha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string ComputeSha256(Stream stream)
    {
        using var sha = SHA256.Create();
        var buf = new byte[1024 * 1024];
        int n;
        while ((n = stream.Read(buf, 0, buf.Length)) > 0)
            sha.TransformBlock(buf, 0, n, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}
