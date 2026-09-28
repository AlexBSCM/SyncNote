using System.Text.Json;
using System.Text.Json.Nodes;

namespace SyncNote.Core;

// Канонический JSON для протокола: UpdatedAt — unix-миллисекунды (long),
// чтобы C# (DateTime) и Kotlin (Long) понимали друг друга.
// При чтении принимаем и число, и ISO-строку.
public static class SyncJson
{
    public static JsonObject ToNode(SyncNoteDto dto)
    {
        var node = JsonSerializer.SerializeToNode(dto)!.AsObject();
        node["UpdatedAt"] = new DateTimeOffset(dto.UpdatedAt).ToUnixTimeMilliseconds();
        return node;
    }

    public static JsonObject ToFileNode(SyncFileDto dto)
    {
        return new JsonObject
        {
            ["Id"] = dto.Id.ToString("N"),
            ["Rev"] = dto.Rev,
            ["BaseRev"] = dto.BaseRev,
            ["Name"] = dto.Name,
            ["Mime"] = dto.Mime,
            ["Size"] = dto.Size,
            ["Sha256"] = dto.Sha256,
            ["UpdatedAt"] = new DateTimeOffset(dto.UpdatedAt).ToUnixTimeMilliseconds(),
            ["Author"] = dto.Author,
            ["IsDeleted"] = dto.IsDeleted,
        };
    }

    public static SyncFileDto FromFileNode(JsonNode node)
    {
        var obj = node.AsObject();
        var dto = new SyncFileDto
        {
            Id = Guid.Parse(obj["Id"]!.GetValue<string>()),
            Rev = obj["Rev"]!.GetValue<long>(),
            BaseRev = obj["BaseRev"]?.GetValue<long>() ?? 0,
            Name = obj["Name"]?.GetValue<string>() ?? string.Empty,
            Mime = obj["Mime"]?.GetValue<string>() ?? "application/octet-stream",
            Size = obj["Size"]?.GetValue<long>() ?? 0,
            Sha256 = obj["Sha256"]?.GetValue<string>() ?? string.Empty,
            Author = obj["Author"]?.GetValue<string>() ?? string.Empty,
            IsDeleted = obj["IsDeleted"]?.GetValue<bool>() ?? false,
        };
        var updated = obj["UpdatedAt"];
        if (updated is JsonValue v && v.TryGetValue<long>(out var millis))
            dto.UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime;
        else if (updated is not null)
            dto.UpdatedAt = DateTime.Parse(updated.GetValue<string>(), null,
                System.Globalization.DateTimeStyles.RoundtripKind);
        else
            dto.UpdatedAt = DateTime.UtcNow;
        return dto;
    }

    public static SyncNoteDto FromNode(JsonNode node)
    {
        var obj = node.AsObject();
        string rawId;
        try
        {
            rawId = obj["Id"]!.GetValue<string>();
        }
        catch (Exception ex)
        {
            throw new FormatException($"Id field unreadable: {ex.GetType().Name}");
        }
        Guid id;
        try
        {
            id = Guid.Parse(rawId);
        }
        catch (Exception)
        {
            var shown = rawId.Length > 40 ? rawId[..40] : rawId;
            throw new FormatException($"Bad Id len={rawId.Length} val='{shown}'");
        }
        var dto = new SyncNoteDto
        {
            Id = id,
            Rev = obj["Rev"]!.GetValue<long>(),
            BaseRev = obj["BaseRev"]!.GetValue<long>(),
            Title = obj["Title"]?.GetValue<string>() ?? string.Empty,
            Body = obj["Body"]?.GetValue<string>() ?? string.Empty,
            Author = obj["Author"]?.GetValue<string>() ?? string.Empty,
            IsDeleted = obj["IsDeleted"]?.GetValue<bool>() ?? false,
        };
        var updated = obj["UpdatedAt"];
        if (updated is JsonValue v && v.TryGetValue<long>(out var millis))
            dto.UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime;
        else
            dto.UpdatedAt = DateTime.Parse(updated!.GetValue<string>(), null,
                System.Globalization.DateTimeStyles.RoundtripKind);
        foreach (var c in obj["Checklist"]?.AsArray() ?? new JsonArray())
        {
            var co = c!.AsObject();
            dto.Checklist.Add(new ChecklistItemDto
            {
                Position = co["Position"]?.GetValue<int>() ?? 0,
                Text = co["Text"]?.GetValue<string>() ?? string.Empty,
                IsChecked = co["IsChecked"]?.GetValue<bool>() ?? false,
            });
        }
        foreach (var a in obj["Attachments"]?.AsArray() ?? new JsonArray())
        {
            var ao = a!.AsObject();
            dto.Attachments.Add(new AttachmentMetaDto
            {
                FileName = ao["FileName"]?.GetValue<string>() ?? string.Empty,
                MimeType = ao["MimeType"]?.GetValue<string>() ?? "application/octet-stream",
                SizeBytes = ao["SizeBytes"]?.GetValue<long>() ?? 0,
                Sha256 = ao["Sha256"]?.GetValue<string>() ?? string.Empty,
            });
        }
        return dto;
    }
}
