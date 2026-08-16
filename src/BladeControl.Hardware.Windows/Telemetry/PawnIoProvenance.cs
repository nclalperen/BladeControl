using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BladeControl.Hardware.Windows.Interop;
using LibreHardwareMonitor.PawnIo;
using Microsoft.Win32;

namespace BladeControl.Hardware.Windows.Telemetry;

public sealed record PawnIoProvenance(
    bool Installed,
    string Version,
    string DriverPath,
    string ServiceState,
    string FileVersion,
    string AuthenticodeStatus,
    string SignerSubject,
    string Sha256,
    bool IsSafeForThermalOwnership,
    IReadOnlyList<string> Diagnostics);

internal static class PawnIoProvenanceReader
{
    private const string ServiceName = "PawnIO";
    private const string ServiceRegistryPath =
        @"SYSTEM\CurrentControlSet\Services\PawnIO";

    internal static PawnIoProvenance Read()
    {
        var diagnostics = new List<string>();
        bool installed;
        string version;
        string serviceState;
        try
        {
            installed = PawnIo.IsInstalled;
            version = PawnIo.Version?.ToString() ?? "unavailable";
            serviceState = QueryServiceState();
        }
        catch (Exception exception)
        {
            installed = false;
            version = "unavailable";
            serviceState = "unavailable";
            diagnostics.Add($"PawnIO API query failed: {exception.Message}");
        }

        string path = ReadDriverPath(diagnostics);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            diagnostics.Add("PawnIO driver image could not be located.");
            return new PawnIoProvenance(
                installed,
                version,
                string.IsNullOrWhiteSpace(path) ? "unavailable" : path,
                serviceState,
                "unavailable",
                "Unavailable",
                "unavailable",
                "unavailable",
                false,
                diagnostics);
        }

        string fileVersion = "unavailable";
        string sha256 = "unavailable";
        bool valid = false;
        string signatureStatus = "Unavailable";
        string signer = "unavailable";
        try
        {
            fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unavailable";
            using (FileStream stream = File.OpenRead(path))
            {
                sha256 = Convert.ToHexString(SHA256.HashData(stream));
            }

            (valid, signatureStatus) = VerifyAuthenticode(path);
            signer = ReadSigner(path, diagnostics);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or CryptographicException or
            System.ComponentModel.Win32Exception)
        {
            diagnostics.Add($"PawnIO file provenance query failed: {exception.Message}");
        }

        bool safe = installed && valid;
        if (!valid)
        {
            diagnostics.Add(
                "PawnIO Authenticode validation did not succeed; authoritative CPU telemetry is disabled for thermal ownership.");
        }

        return new PawnIoProvenance(
            installed,
            version,
            path,
            serviceState,
            fileVersion,
            signatureStatus,
            signer,
            sha256,
            safe,
            diagnostics);
    }

    private static string ReadDriverPath(ICollection<string> diagnostics)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using RegistryKey? serviceKey = baseKey.OpenSubKey(ServiceRegistryPath, writable: false);
            string? raw = serviceKey?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                as string;
            return raw is null ? "unavailable" : NormalizeDriverPath(raw);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or
            IOException or System.Security.SecurityException)
        {
            diagnostics.Add($"PawnIO service registry query failed: {exception.Message}");
            return "unavailable";
        }
    }

    private static string NormalizeDriverPath(string raw)
    {
        string value = raw.Trim().Trim('"');
        if (value.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            value = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                value[12..]);
        }
        else if (value.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            value = value[4..];
        }

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
    }

    private static string QueryServiceState()
    {
        using SafeServiceHandle manager = NativeSecurityMethods.OpenSCManagerW(
            null,
            null,
            NativeSecurityMethods.ScManagerConnect);
        if (manager.IsInvalid)
        {
            return $"unavailable (Win32 {Marshal.GetLastWin32Error()})";
        }

        using SafeServiceHandle service = NativeSecurityMethods.OpenServiceW(
            manager,
            ServiceName,
            NativeSecurityMethods.ServiceQueryStatus);
        if (service.IsInvalid)
        {
            return $"unavailable (Win32 {Marshal.GetLastWin32Error()})";
        }

        if (!NativeSecurityMethods.QueryServiceStatusEx(
                service,
                NativeSecurityMethods.ScStatusProcessInfo,
                out ServiceStatusProcess status,
                checked((uint)Marshal.SizeOf<ServiceStatusProcess>()),
                out _))
        {
            return $"unavailable (Win32 {Marshal.GetLastWin32Error()})";
        }

        return status.CurrentState switch
        {
            1 => "Stopped",
            2 => "StartPending",
            3 => "StopPending",
            4 => "Running",
            5 => "ContinuePending",
            6 => "PausePending",
            7 => "Paused",
            _ => $"Unknown({status.CurrentState})"
        };
    }

    private static (bool Valid, string Status) VerifyAuthenticode(string path)
    {
        IntPtr pathPointer = IntPtr.Zero;
        IntPtr fileInfoPointer = IntPtr.Zero;
        try
        {
            pathPointer = Marshal.StringToCoTaskMemUni(path);
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
                FilePath = pathPointer
            };
            fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var trustData = new WinTrustData
            {
                StructSize = checked((uint)Marshal.SizeOf<WinTrustData>()),
                UiChoice = NativeSecurityMethods.WtdUiNone,
                RevocationChecks = NativeSecurityMethods.WtdRevokeNone,
                UnionChoice = NativeSecurityMethods.WtdChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = NativeSecurityMethods.WtdStateActionIgnore,
                ProviderFlags = NativeSecurityMethods.WtdCacheOnlyUrlRetrieval
            };
            Guid action = NativeSecurityMethods.WinTrustActionGenericVerifyV2;
            int status = NativeSecurityMethods.WinVerifyTrust(
                new IntPtr(-1),
                ref action,
                ref trustData);
            return status == 0
                ? (true, "Valid")
                : (false, $"Invalid(0x{unchecked((uint)status):X8})");
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(fileInfoPointer);
            }

            if (pathPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
    }

    private static string ReadSigner(string path, ICollection<string> diagnostics)
    {
        try
        {
#pragma warning disable SYSLIB0057 // Read-only extraction; trust is independently validated by WinVerifyTrust.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch (CryptographicException exception)
        {
            diagnostics.Add($"PawnIO signer extraction failed: {exception.Message}");
            return "unavailable";
        }
    }
}
