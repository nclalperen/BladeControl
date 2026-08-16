using System.Runtime.InteropServices;
using BladeControl.Hardware.Windows.Interop;
using BladeControl.Razer;
using Microsoft.Win32.SafeHandles;

namespace BladeControl.Hardware.Windows;

internal sealed class WindowsRazerHidTransport : IRazerTransport
{
    private const ushort RazerVendorId = 0x1532;
    private const ushort Blade16ProductId = 0x029F;
    private const ushort ProtocolFeatureReportLength = 91;
    private const int RequestSettleDelayMilliseconds = 1;
    private const int ResponseReadyDelayMilliseconds = 2;

    private readonly object _sync = new();
    private readonly SafeFileHandle _hidDevice;
    private bool _disposed;

    private WindowsRazerHidTransport(
        SafeFileHandle hidDevice,
        RazerDeviceInfo deviceInfo)
    {
        _hidDevice = hidDevice;
        DeviceInfo = deviceInfo;
    }

    public RazerDeviceInfo DeviceInfo { get; }

    internal static WindowsRazerHidTransport Open()
    {
        var enumerationWarnings = new List<string>();
        IReadOnlyList<HidDeviceInfo> devices = HidEnumerator.Enumerate(enumerationWarnings);
        HidDeviceInfo[] exactMatches = devices
            .Where(device =>
                device.VendorId == RazerVendorId &&
                device.ProductId == Blade16ProductId &&
                device.FeatureReportByteLength == ProtocolFeatureReportLength)
            .ToArray();

        if (exactMatches.Length != 1)
        {
            HidDeviceInfo[] candidates = exactMatches.Length > 0
                ? exactMatches
                : devices
                    .Where(device =>
                        device.VendorId == RazerVendorId &&
                        device.ProductId == Blade16ProductId)
                    .ToArray();
            throw new WindowsRazerDeviceSelectionException(
                exactMatches.Length,
                candidates,
                enumerationWarnings.ToArray());
        }

        HidDeviceInfo selected = exactMatches[0];
        if (string.IsNullOrWhiteSpace(selected.DevicePath))
        {
            throw WindowsRazerTransportException.FromValidationFailure(
                "HID interface selection",
                "The unique matching interface has no usable device path. No feature report was sent.");
        }

        SafeFileHandle hidDevice = NativeMethods.CreateFileW(
            selected.DevicePath,
            desiredAccess: 0,
            shareMode: NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            securityAttributes: IntPtr.Zero,
            creationDisposition: NativeMethods.OpenExisting,
            flagsAndAttributes: 0,
            templateFile: IntPtr.Zero);

        if (hidDevice.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            hidDevice.Dispose();
            throw WindowsRazerTransportException.FromNativeError(
                "CreateFileW for the selected Razer HID interface",
                error);
        }

        try
        {
            RazerDeviceInfo verifiedDevice = VerifySelectedHandle(
                hidDevice,
                selected.DevicePath);
            return new WindowsRazerHidTransport(hidDevice, verifiedDevice);
        }
        catch
        {
            hidDevice.Dispose();
            throw;
        }
    }

    public RazerTransportResponse Exchange(RazerTransportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[] requestReport = request.FeatureReport.ToArray();
        if (requestReport.Length != ProtocolFeatureReportLength ||
            requestReport[0] != 0x00)
        {
            throw WindowsRazerTransportException.FromValidationFailure(
                "Whitelisted GET request",
                $"Expected a {ProtocolFeatureReportLength}-byte feature report " +
                "with report ID 0x00. No feature report was sent.",
                requestReport);
        }

        var responseReport = new byte[ProtocolFeatureReportLength];
        responseReport[0] = 0x00;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Thread.Sleep(RequestSettleDelayMilliseconds);

            if (!NativeMethods.HidD_SetFeature(
                    _hidDevice,
                    requestReport,
                    checked((uint)requestReport.Length)))
            {
                int error = Marshal.GetLastWin32Error();
                throw WindowsRazerTransportException.FromNativeError(
                    "HidD_SetFeature for a whitelisted GET request",
                    error,
                    requestReport);
            }

            Thread.Sleep(ResponseReadyDelayMilliseconds);

            if (!NativeMethods.HidD_GetFeature(
                    _hidDevice,
                    responseReport,
                    checked((uint)responseReport.Length)))
            {
                int error = Marshal.GetLastWin32Error();
                throw WindowsRazerTransportException.FromNativeError(
                    "HidD_GetFeature for a whitelisted GET request",
                    error,
                    requestReport);
            }

            if (responseReport[0] != 0x00)
            {
                throw WindowsRazerTransportException.FromValidationFailure(
                    "HidD_GetFeature response",
                    $"Expected report ID 0x00, received 0x{responseReport[0]:X2}. No retry was attempted.",
                    requestReport,
                    responseReport);
            }

            return new RazerTransportResponse(responseReport);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _hidDevice.Dispose();
            _disposed = true;
        }
    }

    private static RazerDeviceInfo VerifySelectedHandle(
        SafeFileHandle hidDevice,
        string devicePath)
    {
        var attributes = new HiddAttributes
        {
            Size = checked((uint)Marshal.SizeOf<HiddAttributes>())
        };
        if (!NativeMethods.HidD_GetAttributes(hidDevice, ref attributes))
        {
            int error = Marshal.GetLastWin32Error();
            throw WindowsRazerTransportException.FromNativeError(
                "HidD_GetAttributes recheck on the selected interface",
                error);
        }

        if (attributes.VendorId != RazerVendorId ||
            attributes.ProductId != Blade16ProductId)
        {
            throw WindowsRazerTransportException.FromValidationFailure(
                "Selected HID interface recheck",
                $"Expected VID:PID {RazerVendorId:X4}:{Blade16ProductId:X4}, " +
                $"received {attributes.VendorId:X4}:{attributes.ProductId:X4}. " +
                "No feature report was sent.");
        }

        if (!NativeMethods.HidD_GetPreparsedData(hidDevice, out IntPtr preparsedData))
        {
            int error = Marshal.GetLastWin32Error();
            throw WindowsRazerTransportException.FromNativeError(
                "HidD_GetPreparsedData recheck on the selected interface",
                error);
        }

        HidpCaps capabilities;
        int status;
        bool freeSucceeded;
        int freeError;
        try
        {
            status = NativeMethods.HidP_GetCaps(preparsedData, out capabilities);
        }
        finally
        {
            freeSucceeded = NativeMethods.HidD_FreePreparsedData(preparsedData);
            freeError = freeSucceeded ? 0 : Marshal.GetLastWin32Error();
        }

        if (!freeSucceeded)
        {
            throw WindowsRazerTransportException.FromNativeError(
                "HidD_FreePreparsedData after selected-interface recheck",
                freeError);
        }

        if (status != NativeMethods.HidpStatusSuccess)
        {
            throw WindowsRazerTransportException.FromValidationFailure(
                "HidP_GetCaps recheck on the selected interface",
                $"HID status was 0x{status:X8}. No feature report was sent.");
        }

        if (capabilities.FeatureReportByteLength != ProtocolFeatureReportLength)
        {
            throw WindowsRazerTransportException.FromValidationFailure(
                "Selected HID interface recheck",
                $"Expected feature-report length {ProtocolFeatureReportLength}, " +
                $"received {capabilities.FeatureReportByteLength}. No feature report was sent.");
        }

        return new RazerDeviceInfo(
            devicePath,
            attributes.VendorId,
            attributes.ProductId,
            capabilities.UsagePage,
            capabilities.Usage,
            capabilities.FeatureReportByteLength);
    }
}
