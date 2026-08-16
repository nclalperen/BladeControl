using Microsoft.Win32.SafeHandles;

namespace BladeControl.Hardware.Windows.Interop;

internal sealed class SafeDeviceInfoSetHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeDeviceInfoSetHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() =>
        NativeMethods.SetupDiDestroyDeviceInfoList(handle);
}
