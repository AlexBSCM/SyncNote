using System;
using System.Threading;
using System.Threading.Tasks;

namespace SyncNote.Core.Interfaces;

// Тупой JSON-канал: длина int32 BE + UTF-8. Сериализация — дело движка,
// фрейминг — дело реализации (TCP, Bluetooth, тесты).
public interface ITransport : IAsyncDisposable
{
    Task SendAsync(string json, CancellationToken ct = default);
    Task<string> ReceiveAsync(CancellationToken ct = default);
}
