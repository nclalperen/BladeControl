using Microsoft.Win32;

namespace BladeControl.Hardware.Windows;

internal static class SystemBiosReader
{
    private const string BiosRegistryPath = @"HARDWARE\DESCRIPTION\System\BIOS";

    internal static SystemBiosInfo Read(ICollection<string> warnings)
    {
        try
        {
            using RegistryKey localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using RegistryKey? biosKey = localMachine.OpenSubKey(
                BiosRegistryPath,
                writable: false);

            if (biosKey is null)
            {
                warnings.Add($"Registry key HKLM\\{BiosRegistryPath} is unavailable.");
                return SystemBiosInfo.Empty;
            }

            return new SystemBiosInfo(
                ReadValue(biosKey, "SystemManufacturer", warnings),
                ReadValue(biosKey, "SystemProductName", warnings),
                ReadValue(biosKey, "SystemSKU", warnings),
                ReadValue(biosKey, "BIOSVersion", warnings));
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            warnings.Add($"Could not read HKLM\\{BiosRegistryPath}: {exception.Message}");
            return SystemBiosInfo.Empty;
        }
    }

    private static string? ReadValue(
        RegistryKey biosKey,
        string valueName,
        ICollection<string> warnings)
    {
        try
        {
            object? value = biosKey.GetValue(
                valueName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);

            return value switch
            {
                string text when !string.IsNullOrWhiteSpace(text) => text,
                string[] values => JoinNonEmpty(values),
                null => null,
                _ => value.ToString()
            };
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            warnings.Add($"Could not read BIOS registry value {valueName}: {exception.Message}");
            return null;
        }
    }

    private static string? JoinNonEmpty(IEnumerable<string> values)
    {
        string joined = string.Join(
            "; ",
            values.Where(value => !string.IsNullOrWhiteSpace(value)));

        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }
}
