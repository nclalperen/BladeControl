namespace BladeControl.Hardware.Windows;

public sealed class HidDeviceInfo
{
    internal HidDeviceInfo(
        string? devicePath,
        string? deviceInstanceId,
        ushort? vendorId,
        ushort? productId,
        ushort? versionNumber,
        string? manufacturerString,
        string? productString,
        string? serialString,
        ushort? usagePage,
        ushort? usage,
        ushort? inputReportByteLength,
        ushort? outputReportByteLength,
        ushort? featureReportByteLength,
        IReadOnlyList<string> warnings)
    {
        DevicePath = devicePath;
        DeviceInstanceId = deviceInstanceId;
        VendorId = vendorId;
        ProductId = productId;
        VersionNumber = versionNumber;
        ManufacturerString = manufacturerString;
        ProductString = productString;
        SerialString = serialString;
        UsagePage = usagePage;
        Usage = usage;
        InputReportByteLength = inputReportByteLength;
        OutputReportByteLength = outputReportByteLength;
        FeatureReportByteLength = featureReportByteLength;
        Warnings = warnings;
    }

    public string? DevicePath { get; }

    public string? DeviceInstanceId { get; }

    public ushort? VendorId { get; }

    public ushort? ProductId { get; }

    public ushort? VersionNumber { get; }

    public string? ManufacturerString { get; }

    public string? ProductString { get; }

    public string? SerialString { get; }

    public ushort? UsagePage { get; }

    public ushort? Usage { get; }

    public ushort? InputReportByteLength { get; }

    public ushort? OutputReportByteLength { get; }

    public ushort? FeatureReportByteLength { get; }

    public IReadOnlyList<string> Warnings { get; }

    public bool IsReferenceDevice => VendorId == 0x1532 && ProductId == 0x029F;

    public bool IsPossibleManagementProtocolInterface =>
        ProductId == 0x029F && FeatureReportByteLength == 91;
}
