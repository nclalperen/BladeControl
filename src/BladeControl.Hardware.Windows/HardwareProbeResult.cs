namespace BladeControl.Hardware.Windows;

public sealed class HardwareProbeResult
{
    internal HardwareProbeResult(
        SystemBiosInfo systemBios,
        IReadOnlyList<HidDeviceInfo> hidDevices,
        IReadOnlyList<string> warnings)
    {
        SystemBios = systemBios;
        HidDevices = hidDevices;
        Warnings = warnings;
    }

    public SystemBiosInfo SystemBios { get; }

    public IReadOnlyList<HidDeviceInfo> HidDevices { get; }

    public IReadOnlyList<string> Warnings { get; }
}
