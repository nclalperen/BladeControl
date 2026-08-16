using System.Runtime.InteropServices;

namespace BladeControl.Hardware.Windows.Interop;

internal static class NativeSecurityMethods
{
    internal const uint ScManagerConnect = 0x0001;
    internal const uint ServiceQueryStatus = 0x0004;
    internal const int ScStatusProcessInfo = 0;
    internal const uint WtdUiNone = 2;
    internal const uint WtdRevokeNone = 0;
    internal const uint WtdChoiceFile = 1;
    internal const uint WtdChoiceCatalog = 2;
    internal const uint WtdStateActionVerify = 1;
    internal const uint WtdStateActionClose = 2;
    internal const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

    internal static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static readonly Guid WinTrustActionDriverVerify =
        new("F750E6C3-38EE-11D1-85E5-00C04FC295EE");

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeServiceHandle OpenSCManagerW(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeServiceHandle OpenServiceW(
        SafeServiceHandle serviceManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceStatusEx(
        SafeServiceHandle service,
        int infoLevel,
        out ServiceStatusProcess buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseServiceHandle(IntPtr serviceHandle);

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int WinVerifyTrust(
        IntPtr window,
        ref Guid actionId,
        ref WinTrustData trustData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    internal static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    internal static extern IntPtr WTHelperGetProvSignerFromChain(
        IntPtr providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptCATAdminAcquireContext2(
        out IntPtr catalogAdmin,
        ref Guid subsystem,
        string? hashAlgorithm,
        IntPtr strongHashPolicy,
        uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptCATAdminCalcHashFromFileHandle2(
        IntPtr catalogAdmin,
        IntPtr file,
        ref uint hashLength,
        [Out] byte[]? hash,
        uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr CryptCATAdminEnumCatalogFromHash(
        IntPtr catalogAdmin,
        [In] byte[] hash,
        uint hashLength,
        uint flags,
        ref IntPtr previousCatalog);

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptCATCatalogInfoFromContext(
        IntPtr catalog,
        ref CatalogInfo catalogInfo,
        uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptCATAdminReleaseCatalogContext(
        IntPtr catalogAdmin,
        IntPtr catalog,
        uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptCATAdminReleaseContext(
        IntPtr catalogAdmin,
        uint flags);
}

[StructLayout(LayoutKind.Sequential)]
internal struct ServiceStatusProcess
{
    internal uint ServiceType;
    internal uint CurrentState;
    internal uint ControlsAccepted;
    internal uint Win32ExitCode;
    internal uint ServiceSpecificExitCode;
    internal uint CheckPoint;
    internal uint WaitHint;
    internal uint ProcessId;
    internal uint ServiceFlags;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WinTrustFileInfo
{
    internal uint StructSize;
    internal IntPtr FilePath;
    internal IntPtr FileHandle;
    internal IntPtr KnownSubject;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WinTrustData
{
    internal uint StructSize;
    internal IntPtr PolicyCallbackData;
    internal IntPtr SipClientData;
    internal uint UiChoice;
    internal uint RevocationChecks;
    internal uint UnionChoice;
    internal IntPtr FileInfo;
    internal uint StateAction;
    internal IntPtr StateData;
    internal IntPtr UrlReference;
    internal uint ProviderFlags;
    internal uint UiContext;
    internal IntPtr SignatureSettings;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WinTrustCatalogInfo
{
    internal uint StructSize;
    internal uint CatalogVersion;
    internal IntPtr CatalogFilePath;
    internal IntPtr MemberTag;
    internal IntPtr MemberFilePath;
    internal IntPtr MemberFile;
    internal IntPtr CalculatedFileHash;
    internal uint CalculatedFileHashLength;
    internal IntPtr CatalogContext;
    internal IntPtr CatalogAdmin;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct CatalogInfo
{
    internal uint StructSize;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    internal string CatalogFile;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CryptProviderSigner
{
    internal uint StructSize;
    internal System.Runtime.InteropServices.ComTypes.FILETIME VerifyAsOf;
    internal uint CertificateChainCount;
    internal IntPtr CertificateChain;
    internal uint SignerType;
    internal IntPtr Signer;
    internal uint Error;
    internal uint CounterSignerCount;
    internal IntPtr CounterSigners;
    internal IntPtr ChainContext;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CryptProviderCertificateHeader
{
    internal uint StructSize;
    internal IntPtr CertificateContext;
}
