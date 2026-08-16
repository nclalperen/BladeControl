using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BladeControl.Hardware.Windows.Interop;

internal static class NativeMethods
{
    internal const uint DigcfPresent = 0x00000002;
    internal const uint DigcfDeviceInterface = 0x00000010;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorNoMoreItems = 259;
    internal const int HidpStatusSuccess = 0x00110000;
    internal const int DevicePathOffset = sizeof(uint);

    internal static int DeviceInterfaceDetailDataSize => IntPtr.Size == 8 ? 8 : 6;

    [DllImport("hid.dll", ExactSpelling = true)]
    internal static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetClassDevsW",
        ExactSpelling = true,
        SetLastError = true)]
    internal static extern SafeDeviceInfoSetHandle SetupDiGetClassDevsW(
        ref Guid classGuid,
        IntPtr enumerator,
        IntPtr window,
        uint flags);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInterfaces(
        SafeDeviceInfoSetHandle deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetDeviceInterfaceDetailW",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInterfaceDetailW(
        SafeDeviceInfoSetHandle deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        ref SpDevInfoData deviceInfoData);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetDeviceInstanceIdW",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInstanceIdW(
        SafeDeviceInfoSetHandle deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        IntPtr deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        ExactSpelling = true,
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetAttributes(
        SafeFileHandle hidDevice,
        ref HiddAttributes attributes);

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetManufacturerString(
        SafeFileHandle hidDevice,
        IntPtr buffer,
        uint bufferLength);

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetProductString(
        SafeFileHandle hidDevice,
        IntPtr buffer,
        uint bufferLength);

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetSerialNumberString(
        SafeFileHandle hidDevice,
        IntPtr buffer,
        uint bufferLength);

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetPreparsedData(
        SafeFileHandle hidDevice,
        out IntPtr preparsedData);

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_SetFeature(
        SafeFileHandle hidDevice,
        [In] byte[] reportBuffer,
        uint reportBufferLength);

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetFeature(
        SafeFileHandle hidDevice,
        [In, Out] byte[] reportBuffer,
        uint reportBufferLength);

    [DllImport("hid.dll", ExactSpelling = true)]
    internal static extern int HidP_GetCaps(
        IntPtr preparsedData,
        out HidpCaps capabilities);
}
