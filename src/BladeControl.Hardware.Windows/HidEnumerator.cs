using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using BladeControl.Hardware.Windows.Interop;

namespace BladeControl.Hardware.Windows;

internal static class HidEnumerator
{
    private const int HidStringBufferByteLength = 512;

    private delegate bool HidStringReader(
        SafeFileHandle hidDevice,
        IntPtr buffer,
        uint bufferLength);

    internal static IReadOnlyList<HidDeviceInfo> Enumerate(ICollection<string> probeWarnings)
    {
        var devices = new List<HidDeviceInfo>();
        NativeMethods.HidD_GetHidGuid(out Guid hidGuid);

        using SafeDeviceInfoSetHandle deviceInfoSet = NativeMethods.SetupDiGetClassDevsW(
            ref hidGuid,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.DigcfPresent | NativeMethods.DigcfDeviceInterface);

        if (deviceInfoSet.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            probeWarnings.Add(DescribeWin32Failure("SetupDiGetClassDevsW", error));
            return devices.AsReadOnly();
        }

        for (uint memberIndex = 0; ; memberIndex++)
        {
            var interfaceData = new SpDeviceInterfaceData
            {
                Size = checked((uint)Marshal.SizeOf<SpDeviceInterfaceData>())
            };

            if (!NativeMethods.SetupDiEnumDeviceInterfaces(
                    deviceInfoSet,
                    IntPtr.Zero,
                    ref hidGuid,
                    memberIndex,
                    ref interfaceData))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != NativeMethods.ErrorNoMoreItems)
                {
                    probeWarnings.Add(
                        DescribeWin32Failure(
                            $"SetupDiEnumDeviceInterfaces at index {memberIndex}",
                            error));
                }

                break;
            }

            devices.Add(ReadInterface(deviceInfoSet, ref interfaceData, memberIndex));
        }

        return devices.AsReadOnly();
    }

    private static HidDeviceInfo ReadInterface(
        SafeDeviceInfoSetHandle deviceInfoSet,
        ref SpDeviceInterfaceData interfaceData,
        uint memberIndex)
    {
        var warnings = new List<string>();
        string? devicePath = null;
        string? deviceInstanceId = null;
        var deviceInfoData = new SpDevInfoData
        {
            Size = checked((uint)Marshal.SizeOf<SpDevInfoData>())
        };

        bool sizeQuerySucceeded = NativeMethods.SetupDiGetDeviceInterfaceDetailW(
            deviceInfoSet,
            ref interfaceData,
            IntPtr.Zero,
            0,
            out uint requiredSize,
            ref deviceInfoData);
        int sizeQueryError = sizeQuerySucceeded ? 0 : Marshal.GetLastWin32Error();

        if (!sizeQuerySucceeded &&
            (sizeQueryError != NativeMethods.ErrorInsufficientBuffer || requiredSize == 0))
        {
            warnings.Add(
                DescribeWin32Failure(
                    $"SetupDiGetDeviceInterfaceDetailW size query for interface {memberIndex}",
                    sizeQueryError));
        }
        else if (requiredSize == 0)
        {
            warnings.Add(
                $"SetupDiGetDeviceInterfaceDetailW returned an empty path buffer for interface {memberIndex}.");
        }
        else
        {
            IntPtr detailBuffer = IntPtr.Zero;
            try
            {
                detailBuffer = Marshal.AllocHGlobal(checked((int)requiredSize));
                Marshal.WriteInt32(
                    detailBuffer,
                    NativeMethods.DeviceInterfaceDetailDataSize);

                deviceInfoData = new SpDevInfoData
                {
                    Size = checked((uint)Marshal.SizeOf<SpDevInfoData>())
                };

                if (NativeMethods.SetupDiGetDeviceInterfaceDetailW(
                        deviceInfoSet,
                        ref interfaceData,
                        detailBuffer,
                        requiredSize,
                        out _,
                        ref deviceInfoData))
                {
                    devicePath = Marshal.PtrToStringUni(
                        IntPtr.Add(detailBuffer, NativeMethods.DevicePathOffset));
                    deviceInstanceId = ReadDeviceInstanceId(
                        deviceInfoSet,
                        ref deviceInfoData,
                        warnings);
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    warnings.Add(
                        DescribeWin32Failure(
                            $"SetupDiGetDeviceInterfaceDetailW for interface {memberIndex}",
                            error));
                }
            }
            catch (Exception exception) when (
                exception is OverflowException or OutOfMemoryException)
            {
                warnings.Add(
                    $"Could not allocate the HID interface-detail buffer for interface {memberIndex}: " +
                    exception.Message);
            }
            finally
            {
                if (detailBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(detailBuffer);
                }
            }
        }

        return ReadMetadata(devicePath, deviceInstanceId, warnings);
    }

    private static string? ReadDeviceInstanceId(
        SafeDeviceInfoSetHandle deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        ICollection<string> warnings)
    {
        bool sizeQuerySucceeded = NativeMethods.SetupDiGetDeviceInstanceIdW(
            deviceInfoSet,
            ref deviceInfoData,
            IntPtr.Zero,
            0,
            out uint requiredCharacters);
        int sizeQueryError = sizeQuerySucceeded ? 0 : Marshal.GetLastWin32Error();

        if (!sizeQuerySucceeded &&
            (sizeQueryError != NativeMethods.ErrorInsufficientBuffer || requiredCharacters == 0))
        {
            warnings.Add(
                DescribeWin32Failure(
                    "SetupDiGetDeviceInstanceIdW size query",
                    sizeQueryError));
            return null;
        }

        if (requiredCharacters == 0)
        {
            warnings.Add("SetupDiGetDeviceInstanceIdW returned an empty instance-ID buffer.");
            return null;
        }

        IntPtr instanceIdBuffer = IntPtr.Zero;
        try
        {
            int byteLength = checked(checked((int)requiredCharacters) * sizeof(char));
            instanceIdBuffer = Marshal.AllocHGlobal(byteLength);

            if (!NativeMethods.SetupDiGetDeviceInstanceIdW(
                    deviceInfoSet,
                    ref deviceInfoData,
                    instanceIdBuffer,
                    requiredCharacters,
                    out _))
            {
                int error = Marshal.GetLastWin32Error();
                warnings.Add(DescribeWin32Failure("SetupDiGetDeviceInstanceIdW", error));
                return null;
            }

            return Marshal.PtrToStringUni(instanceIdBuffer);
        }
        catch (Exception exception) when (
            exception is OverflowException or OutOfMemoryException)
        {
            warnings.Add($"Could not allocate the HID instance-ID buffer: {exception.Message}");
            return null;
        }
        finally
        {
            if (instanceIdBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(instanceIdBuffer);
            }
        }
    }

    private static HidDeviceInfo ReadMetadata(
        string? devicePath,
        string? deviceInstanceId,
        List<string> warnings)
    {
        ushort? vendorId = ParseHexIdentifier(deviceInstanceId, devicePath, "VID_");
        ushort? productId = ParseHexIdentifier(deviceInstanceId, devicePath, "PID_");
        ushort? versionNumber = ParseHexIdentifier(deviceInstanceId, devicePath, "REV_");
        string? manufacturerString = null;
        string? productString = null;
        string? serialString = null;
        ushort? usagePage = null;
        ushort? usage = null;
        ushort? inputReportByteLength = null;
        ushort? outputReportByteLength = null;
        ushort? featureReportByteLength = null;

        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return CreateDeviceInfo();
        }

        using SafeFileHandle hidDevice = NativeMethods.CreateFileW(
            devicePath,
            desiredAccess: 0,
            shareMode: NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            securityAttributes: IntPtr.Zero,
            creationDisposition: NativeMethods.OpenExisting,
            flagsAndAttributes: 0,
            templateFile: IntPtr.Zero);

        if (hidDevice.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            warnings.Add(
                DescribeWin32Failure(
                    "CreateFileW metadata handle (desired access 0)",
                    error));
            return CreateDeviceInfo();
        }

        var attributes = new HiddAttributes
        {
            Size = checked((uint)Marshal.SizeOf<HiddAttributes>())
        };

        if (NativeMethods.HidD_GetAttributes(hidDevice, ref attributes))
        {
            vendorId = attributes.VendorId;
            productId = attributes.ProductId;
            versionNumber = attributes.VersionNumber;
        }
        else
        {
            int error = Marshal.GetLastWin32Error();
            warnings.Add(DescribeWin32Failure("HidD_GetAttributes", error));
        }

        manufacturerString = ReadHidString(
            hidDevice,
            NativeMethods.HidD_GetManufacturerString,
            "HidD_GetManufacturerString",
            warnings);
        productString = ReadHidString(
            hidDevice,
            NativeMethods.HidD_GetProductString,
            "HidD_GetProductString",
            warnings);
        serialString = ReadHidString(
            hidDevice,
            NativeMethods.HidD_GetSerialNumberString,
            "HidD_GetSerialNumberString",
            warnings);

        if (NativeMethods.HidD_GetPreparsedData(hidDevice, out IntPtr preparsedData))
        {
            try
            {
                int status = NativeMethods.HidP_GetCaps(preparsedData, out HidpCaps capabilities);
                if (status == NativeMethods.HidpStatusSuccess)
                {
                    usagePage = capabilities.UsagePage;
                    usage = capabilities.Usage;
                    inputReportByteLength = capabilities.InputReportByteLength;
                    outputReportByteLength = capabilities.OutputReportByteLength;
                    featureReportByteLength = capabilities.FeatureReportByteLength;
                }
                else
                {
                    warnings.Add($"HidP_GetCaps failed with HID status 0x{status:X8}.");
                }
            }
            finally
            {
                if (!NativeMethods.HidD_FreePreparsedData(preparsedData))
                {
                    int error = Marshal.GetLastWin32Error();
                    warnings.Add(DescribeWin32Failure("HidD_FreePreparsedData", error));
                }
            }
        }
        else
        {
            int error = Marshal.GetLastWin32Error();
            warnings.Add(DescribeWin32Failure("HidD_GetPreparsedData", error));
        }

        return CreateDeviceInfo();

        HidDeviceInfo CreateDeviceInfo() => new(
            devicePath,
            deviceInstanceId,
            vendorId,
            productId,
            versionNumber,
            manufacturerString,
            productString,
            serialString,
            usagePage,
            usage,
            inputReportByteLength,
            outputReportByteLength,
            featureReportByteLength,
            warnings.AsReadOnly());
    }

    private static string? ReadHidString(
        SafeFileHandle hidDevice,
        HidStringReader reader,
        string operation,
        ICollection<string> warnings)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            buffer = Marshal.AllocHGlobal(HidStringBufferByteLength);
            Marshal.Copy(
                new byte[HidStringBufferByteLength],
                0,
                buffer,
                HidStringBufferByteLength);

            if (!reader(hidDevice, buffer, HidStringBufferByteLength))
            {
                int error = Marshal.GetLastWin32Error();
                warnings.Add(DescribeWin32Failure(operation, error));
                return null;
            }

            string? value = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (OutOfMemoryException exception)
        {
            warnings.Add($"Could not allocate the buffer for {operation}: {exception.Message}");
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static ushort? ParseHexIdentifier(
        string? primaryText,
        string? secondaryText,
        string marker)
    {
        return TryParseHexIdentifier(primaryText, marker, out ushort primaryValue)
            ? primaryValue
            : TryParseHexIdentifier(secondaryText, marker, out ushort secondaryValue)
                ? secondaryValue
                : null;
    }

    private static bool TryParseHexIdentifier(
        string? text,
        string marker,
        out ushort value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        int markerIndex = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        int valueIndex = markerIndex + marker.Length;

        return markerIndex >= 0 &&
            text.Length >= valueIndex + 4 &&
            ushort.TryParse(
                text.AsSpan(valueIndex, 4),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static string DescribeWin32Failure(string operation, int error)
    {
        return error == 0
            ? $"{operation} failed; no Win32 error code was provided."
            : $"{operation} failed with Win32 error {error}: {new Win32Exception(error).Message}";
    }
}
