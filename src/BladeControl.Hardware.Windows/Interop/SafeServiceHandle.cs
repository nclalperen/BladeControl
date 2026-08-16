using Microsoft.Win32.SafeHandles;

namespace BladeControl.Hardware.Windows.Interop;

internal sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeServiceHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() =>
        NativeSecurityMethods.CloseServiceHandle(handle);
}
