namespace BladeControl.Hardware.Windows;

public static class WindowsHardwareProbe
{
    public static HardwareProbeResult Probe()
    {
        var warnings = new List<string>();

        if (!OperatingSystem.IsWindows())
        {
            warnings.Add("The hardware probe is available only on Windows.");
            return new HardwareProbeResult(
                SystemBiosInfo.Empty,
                Array.Empty<HidDeviceInfo>(),
                warnings.AsReadOnly());
        }

        SystemBiosInfo systemBios = SystemBiosReader.Read(warnings);
        IReadOnlyList<HidDeviceInfo> hidDevices = HidEnumerator.Enumerate(warnings);

        return new HardwareProbeResult(
            systemBios,
            hidDevices,
            warnings.AsReadOnly());
    }
}
