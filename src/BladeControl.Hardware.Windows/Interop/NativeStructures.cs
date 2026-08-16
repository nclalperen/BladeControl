using System.Runtime.InteropServices;

namespace BladeControl.Hardware.Windows.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct SpDeviceInterfaceData
{
    internal uint Size;
    internal Guid InterfaceClassGuid;
    internal uint Flags;
    internal UIntPtr Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpDevInfoData
{
    internal uint Size;
    internal Guid ClassGuid;
    internal uint DeviceInstance;
    internal UIntPtr Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HiddAttributes
{
    internal uint Size;
    internal ushort VendorId;
    internal ushort ProductId;
    internal ushort VersionNumber;
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct HidpCaps
{
    [FieldOffset(0)]
    internal ushort Usage;

    [FieldOffset(2)]
    internal ushort UsagePage;

    [FieldOffset(4)]
    internal ushort InputReportByteLength;

    [FieldOffset(6)]
    internal ushort OutputReportByteLength;

    [FieldOffset(8)]
    internal ushort FeatureReportByteLength;
}
