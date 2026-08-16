using System.ComponentModel;

namespace BladeControl.Hardware.Windows;

public sealed class WindowsRazerDeviceSelectionException : Exception
{
    internal WindowsRazerDeviceSelectionException(
        int exactMatchCount,
        IReadOnlyList<HidDeviceInfo> candidates,
        IReadOnlyList<string> enumerationWarnings)
        : base(
            $"Expected exactly one HID interface matching VID 0x1532, PID 0x029F, " +
            $"and FeatureReportByteLength 91; found {exactMatchCount}. " +
            "No feature report was sent.")
    {
        ExactMatchCount = exactMatchCount;
        Candidates = candidates.ToArray();
        EnumerationWarnings = enumerationWarnings.ToArray();
    }

    public int ExactMatchCount { get; }

    public IReadOnlyList<HidDeviceInfo> Candidates { get; }

    public IReadOnlyList<string> EnumerationWarnings { get; }
}

public sealed class WindowsRazerTransportException : Exception
{
    private readonly byte[] _requestReport;
    private readonly byte[] _responseReport;

    private WindowsRazerTransportException(
        string operation,
        string message,
        int? nativeErrorCode,
        ReadOnlySpan<byte> requestReport,
        ReadOnlySpan<byte> responseReport)
        : base(message)
    {
        Operation = operation;
        NativeErrorCode = nativeErrorCode;
        _requestReport = requestReport.ToArray();
        _responseReport = responseReport.ToArray();
    }

    public string Operation { get; }

    public int? NativeErrorCode { get; }

    public ReadOnlyMemory<byte> RequestReport => _requestReport;

    public ReadOnlyMemory<byte> ResponseReport => _responseReport;

    internal static WindowsRazerTransportException FromNativeError(
        string operation,
        int error,
        ReadOnlySpan<byte> requestReport = default)
    {
        string detail = error == 0
            ? "No Win32 error code was supplied."
            : $"Win32 error {error}: {new Win32Exception(error).Message}";
        return new WindowsRazerTransportException(
            operation,
            $"{operation} failed. {detail}",
            error == 0 ? null : error,
            requestReport,
            default);
    }

    internal static WindowsRazerTransportException FromValidationFailure(
        string operation,
        string detail,
        ReadOnlySpan<byte> requestReport = default,
        ReadOnlySpan<byte> responseReport = default)
    {
        return new WindowsRazerTransportException(
            operation,
            $"{operation} failed. {detail}",
            null,
            requestReport,
            responseReport);
    }
}
