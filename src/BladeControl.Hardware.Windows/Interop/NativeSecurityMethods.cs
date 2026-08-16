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
    internal const uint WtdStateActionIgnore = 0;
    internal const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

    internal static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

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
