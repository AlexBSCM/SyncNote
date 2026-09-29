namespace SyncNote.Core.Models.Dtos;

// DTO файла для провода. stored_name намеренно НЕТ: получатель выводит
// имя из sha256 (локальный путь не утекает, см. protocol_v2.md §4).
public sealed class FileDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mime { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public long Rev { get; set; }
    public string UpdatedAt { get; set; } = "";
    public string Author { get; set; } = "";
    public bool IsDeleted { get; set; }
    public long BaseRev { get; set; }
}
