namespace SyncNote.Windows.ViewModels;

// Доверенное устройство после успешного сопряжения.
// Хранится JSON-списком в keyvalue (ключ TrustedDevices.Key).
public sealed class TrustedDevice
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public string LastSeen { get; set; } = "";

    public string Display => string.IsNullOrEmpty(Name)
        ? $"{Ip}:{Port}"
        : $"{Name} ({Ip}:{Port})";
}
