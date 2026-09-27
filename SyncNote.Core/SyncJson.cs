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

    public static SyncNoteDto FromNode(JsonNode node)
    {
        var obj = node.AsObject();
        var dto = new SyncNoteDto
        {
            Id = Guid.Parse(obj["Id"]!.GetValue<string>()),
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
