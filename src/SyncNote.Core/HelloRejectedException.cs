using System;
using System.IO;

namespace SyncNote.Core;

// Отказ в рукопожатии: чужой/просроченный токен или версия.
// Отдельный тип, чтобы UI отличал «не пустили» от обрыва сети.
public sealed class HelloRejectedException(string reason) : IOException(reason);
