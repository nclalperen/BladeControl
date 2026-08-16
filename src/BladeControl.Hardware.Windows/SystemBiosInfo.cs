namespace BladeControl.Hardware.Windows;

public sealed class SystemBiosInfo
{
    internal SystemBiosInfo(
        string? systemManufacturer,
        string? systemProductName,
        string? systemSku,
        string? biosVersion)
    {
        SystemManufacturer = systemManufacturer;
        SystemProductName = systemProductName;
        SystemSku = systemSku;
        BiosVersion = biosVersion;
    }

    public string? SystemManufacturer { get; }

    public string? SystemProductName { get; }

    public string? SystemSku { get; }

    public string? BiosVersion { get; }

    internal static SystemBiosInfo Empty { get; } = new(null, null, null, null);
}
