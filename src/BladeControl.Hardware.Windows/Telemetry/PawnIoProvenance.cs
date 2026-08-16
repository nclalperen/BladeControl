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
    string WindowsTrustedSignerSubject,
    string EmbeddedSignerSubject,
    string TimestampSignerSubject,
    string SignatureSource,
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
                "unavailable",
                "Unavailable",
                "unavailable",
                false,
                diagnostics);
        }

        string fileVersion = "unavailable";
        string sha256 = "unavailable";
        AuthenticodeVerification verification = AuthenticodeVerification.Unavailable;
        string embeddedSigner = "unavailable";
        try
        {
            fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unavailable";
            using (FileStream stream = File.OpenRead(path))
            {
                sha256 = Convert.ToHexString(SHA256.HashData(stream));
            }

            verification = VerifyAuthenticode(path, diagnostics);
            embeddedSigner = ReadEmbeddedSigner(path, diagnostics);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or CryptographicException or
            System.ComponentModel.Win32Exception)
        {
            diagnostics.Add($"PawnIO file provenance query failed: {exception.Message}");
        }

        bool safe = IsSafeForThermalOwnership(installed, verification.Valid);
        if (!verification.Valid)
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
            verification.Status,
            verification.WindowsTrustedSignerSubject,
            embeddedSigner,
            verification.TimestampSignerSubject,
            verification.SignatureSource,
            sha256,
            safe,
            diagnostics);
    }

    internal static bool IsSafeForThermalOwnership(
        bool installed,
        bool windowsTrustValid) => installed && windowsTrustValid;

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

    private static AuthenticodeVerification VerifyAuthenticode(
        string path,
        ICollection<string> diagnostics)
    {
        IntPtr catalogAdmin = IntPtr.Zero;
        IntPtr catalog = IntPtr.Zero;
        try
        {
            Guid subsystem = NativeSecurityMethods.WinTrustActionDriverVerify;
            if (!NativeSecurityMethods.CryptCATAdminAcquireContext2(
                    out catalogAdmin,
                    ref subsystem,
                    null,
                    IntPtr.Zero,
                    0))
            {
                diagnostics.Add(
                    $"Windows catalog context acquisition failed with Win32 {Marshal.GetLastWin32Error()}; " +
                    "falling back to Windows file trust verification.");
                return VerifyFileTrust(path);
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            uint hashLength = 0;
            _ = NativeSecurityMethods.CryptCATAdminCalcHashFromFileHandle2(
                catalogAdmin,
                stream.SafeFileHandle.DangerousGetHandle(),
                ref hashLength,
                null,
                0);
            if (hashLength == 0)
            {
                diagnostics.Add(
                    $"Windows catalog hash sizing failed with Win32 {Marshal.GetLastWin32Error()}; " +
                    "falling back to Windows file trust verification.");
                return VerifyFileTrust(path);
            }

            var hash = new byte[hashLength];
            if (!NativeSecurityMethods.CryptCATAdminCalcHashFromFileHandle2(
                    catalogAdmin,
                    stream.SafeFileHandle.DangerousGetHandle(),
                    ref hashLength,
                    hash,
                    0))
            {
                diagnostics.Add(
                    $"Windows catalog hash calculation failed with Win32 {Marshal.GetLastWin32Error()}; " +
                    "falling back to Windows file trust verification.");
                return VerifyFileTrust(path);
            }

            IntPtr previous = IntPtr.Zero;
            catalog = NativeSecurityMethods.CryptCATAdminEnumCatalogFromHash(
                catalogAdmin,
                hash,
                hashLength,
                0,
                ref previous);
            if (catalog == IntPtr.Zero)
            {
                diagnostics.Add(
                    "No Windows catalog membership was found; Windows file trust verification was used.");
                return VerifyFileTrust(path);
            }

            var catalogInfo = new CatalogInfo
            {
                StructSize = checked((uint)Marshal.SizeOf<CatalogInfo>()),
                CatalogFile = string.Empty
            };
            if (!NativeSecurityMethods.CryptCATCatalogInfoFromContext(
                    catalog,
                    ref catalogInfo,
                    0))
            {
                return new AuthenticodeVerification(
                    false,
                    $"CatalogInfoFailed(Win32 {Marshal.GetLastWin32Error()})",
                    "unavailable",
                    "unavailable",
                    "WindowsCatalog");
            }

            return VerifyCatalogTrust(
                path,
                stream.SafeFileHandle.DangerousGetHandle(),
                catalogInfo.CatalogFile,
                hash,
                catalogAdmin,
                catalog);
        }
        finally
        {
            if (catalog != IntPtr.Zero)
            {
                _ = NativeSecurityMethods.CryptCATAdminReleaseCatalogContext(
                    catalogAdmin,
                    catalog,
                    0);
            }

            if (catalogAdmin != IntPtr.Zero)
            {
                _ = NativeSecurityMethods.CryptCATAdminReleaseContext(catalogAdmin, 0);
            }
        }
    }

    private static AuthenticodeVerification VerifyFileTrust(string path)
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
                StateAction = NativeSecurityMethods.WtdStateActionVerify,
                ProviderFlags = NativeSecurityMethods.WtdCacheOnlyUrlRetrieval
            };
            Guid action = NativeSecurityMethods.WinTrustActionGenericVerifyV2;
            return RunWinTrust(ref action, ref trustData, "EmbeddedAuthenticode");
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

    private static AuthenticodeVerification VerifyCatalogTrust(
        string path,
        IntPtr fileHandle,
        string catalogPath,
        byte[] hash,
        IntPtr catalogAdmin,
        IntPtr catalog)
    {
        IntPtr catalogPathPointer = IntPtr.Zero;
        IntPtr memberTagPointer = IntPtr.Zero;
        IntPtr memberPathPointer = IntPtr.Zero;
        IntPtr hashPointer = IntPtr.Zero;
        IntPtr catalogInfoPointer = IntPtr.Zero;
        try
        {
            catalogPathPointer = Marshal.StringToCoTaskMemUni(catalogPath);
            memberTagPointer = Marshal.StringToCoTaskMemUni(Convert.ToHexString(hash));
            memberPathPointer = Marshal.StringToCoTaskMemUni(path);
            hashPointer = Marshal.AllocCoTaskMem(hash.Length);
            Marshal.Copy(hash, 0, hashPointer, hash.Length);
            var catalogInfo = new WinTrustCatalogInfo
            {
                StructSize = checked((uint)Marshal.SizeOf<WinTrustCatalogInfo>()),
                CatalogFilePath = catalogPathPointer,
                MemberTag = memberTagPointer,
                MemberFilePath = memberPathPointer,
                MemberFile = fileHandle,
                CalculatedFileHash = hashPointer,
                CalculatedFileHashLength = checked((uint)hash.Length),
                CatalogContext = catalog,
                CatalogAdmin = catalogAdmin
            };
            catalogInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustCatalogInfo>());
            Marshal.StructureToPtr(catalogInfo, catalogInfoPointer, fDeleteOld: false);
            var trustData = new WinTrustData
            {
                StructSize = checked((uint)Marshal.SizeOf<WinTrustData>()),
                UiChoice = NativeSecurityMethods.WtdUiNone,
                RevocationChecks = NativeSecurityMethods.WtdRevokeNone,
                UnionChoice = NativeSecurityMethods.WtdChoiceCatalog,
                FileInfo = catalogInfoPointer,
                StateAction = NativeSecurityMethods.WtdStateActionVerify,
                ProviderFlags = NativeSecurityMethods.WtdCacheOnlyUrlRetrieval
            };
            Guid action = NativeSecurityMethods.WinTrustActionDriverVerify;
            return RunWinTrust(ref action, ref trustData, "WindowsCatalog");
        }
        finally
        {
            if (catalogInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(catalogInfoPointer);
            }

            if (hashPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(hashPointer);
            }

            if (memberPathPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(memberPathPointer);
            }

            if (memberTagPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(memberTagPointer);
            }

            if (catalogPathPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(catalogPathPointer);
            }
        }
    }

    private static AuthenticodeVerification RunWinTrust(
        ref Guid action,
        ref WinTrustData trustData,
        string signatureSource)
    {
        int status = NativeSecurityMethods.WinVerifyTrust(
            new IntPtr(-1),
            ref action,
            ref trustData);
        try
        {
            string trustedSigner = ReadTrustProviderSigner(
                trustData.StateData,
                counterSigner: false);
            string timestampSigner = ReadTrustProviderSigner(
                trustData.StateData,
                counterSigner: true);
            return new AuthenticodeVerification(
                status == 0,
                status == 0
                    ? "Valid"
                    : $"Invalid(0x{unchecked((uint)status):X8})",
                trustedSigner,
                timestampSigner,
                signatureSource);
        }
        finally
        {
            if (trustData.StateData != IntPtr.Zero)
            {
                trustData.StateAction = NativeSecurityMethods.WtdStateActionClose;
                _ = NativeSecurityMethods.WinVerifyTrust(
                    new IntPtr(-1),
                    ref action,
                    ref trustData);
            }
        }
    }

    private static string ReadTrustProviderSigner(
        IntPtr stateData,
        bool counterSigner)
    {
        if (stateData == IntPtr.Zero)
        {
            return "unavailable";
        }

        try
        {
            IntPtr providerData = NativeSecurityMethods.WTHelperProvDataFromStateData(stateData);
            IntPtr signerPointer = providerData == IntPtr.Zero
                ? IntPtr.Zero
                : NativeSecurityMethods.WTHelperGetProvSignerFromChain(
                    providerData,
                    0,
                    counterSigner,
                    0);
            if (signerPointer == IntPtr.Zero)
            {
                return "unavailable";
            }

            CryptProviderSigner signer = Marshal.PtrToStructure<CryptProviderSigner>(
                signerPointer);
            if (signer.CertificateChainCount == 0 || signer.CertificateChain == IntPtr.Zero)
            {
                return "unavailable";
            }

            CryptProviderCertificateHeader certificate =
                Marshal.PtrToStructure<CryptProviderCertificateHeader>(signer.CertificateChain);
            if (certificate.CertificateContext == IntPtr.Zero)
            {
                return "unavailable";
            }

#pragma warning disable SYSLIB0057 // Read-only subject extraction from WinTrust's certificate context.
            using var signerCertificate = new X509Certificate2(certificate.CertificateContext);
#pragma warning restore SYSLIB0057
            return signerCertificate.Subject;
        }
        catch (CryptographicException)
        {
            return "unavailable";
        }
    }

    private static string ReadEmbeddedSigner(string path, ICollection<string> diagnostics)
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

    private sealed record AuthenticodeVerification(
        bool Valid,
        string Status,
        string WindowsTrustedSignerSubject,
        string TimestampSignerSubject,
        string SignatureSource)
    {
        internal static AuthenticodeVerification Unavailable { get; } = new(
            false,
            "Unavailable",
            "unavailable",
            "unavailable",
            "Unavailable");
    }
}
