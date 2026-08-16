using BladeControl.Razer.Protocol;

namespace BladeControl.Razer;

internal interface IRazerTransport : IDisposable
{
    RazerDeviceInfo DeviceInfo { get; }

    RazerTransportResponse Exchange(RazerTransportRequest request);
}

internal sealed class RazerTransportRequest
{
    private readonly byte[] _featureReport;

    internal RazerTransportRequest(RazerPacket packet)
    {
        RazerCommands.EnsureAllowed(packet);
        _featureReport = RazerPacketCodec.EncodeHidFeatureReport(packet);
    }

    internal ReadOnlyMemory<byte> FeatureReport => _featureReport;
}

internal sealed class RazerTransportResponse
{
    private readonly byte[] _featureReport;

    internal RazerTransportResponse(ReadOnlySpan<byte> featureReport)
    {
        if (featureReport.Length != RazerPacketCodec.HidFeatureReportLength)
        {
            throw new ArgumentException(
                $"The transport response must be exactly " +
                $"{RazerPacketCodec.HidFeatureReportLength} bytes.",
                nameof(featureReport));
        }

        _featureReport = featureReport.ToArray();
    }

    internal ReadOnlyMemory<byte> FeatureReport => _featureReport;
}
