using BladeControl.Razer.Protocol;

namespace BladeControl.Razer.Tests;

internal sealed class SequenceTransactionIdSource : ITransactionIdSource
{
    private readonly Queue<byte> _values;

    internal SequenceTransactionIdSource(params byte[] values)
    {
        _values = new Queue<byte>(values);
    }

    public byte NextTransactionId()
    {
        return _values.Count > 0
            ? _values.Dequeue()
            : throw new InvalidOperationException("No test transaction ID remains.");
    }
}

internal sealed class ScriptedRazerTransport : IRazerTransport
{
    private readonly Func<int, RazerPacket, RazerPacket>? _responseFactory;

    internal ScriptedRazerTransport(
        Func<int, RazerPacket, RazerPacket>? responseFactory = null)
    {
        _responseFactory = responseFactory;
    }

    public RazerDeviceInfo DeviceInfo { get; } = new(
        @"\\?\hid#test",
        0x1532,
        0x029F,
        0x0001,
        0x0002,
        91);

    internal List<byte[]> Requests { get; } = [];

    internal int CallCount => Requests.Count;

    internal bool CorruptResponseChecksum { get; init; }

    public RazerTransportResponse Exchange(RazerTransportRequest request)
    {
        byte[] report = request.FeatureReport.ToArray();
        Requests.Add(report);
        RazerPacket requestPacket = RazerPacketCodec.DecodeHidFeatureReport(report);
        RazerPacket response = _responseFactory?.Invoke(CallCount, requestPacket) ??
            CreateSuccessfulResponse(requestPacket);

        byte[] responseReport = RazerPacketCodec.EncodeHidFeatureReport(response);
        if (CorruptResponseChecksum)
        {
            responseReport[89] = 0x00;
        }

        return new RazerTransportResponse(responseReport);
    }

    public void Dispose()
    {
    }

    internal static RazerPacket CreateSuccessfulResponse(RazerPacket request)
    {
        byte[] arguments = request.Arguments.ToArray();
        if (request.CommandId == RazerCommands.GetFanRpmCommandId)
        {
            arguments[2] = arguments[1] == (byte)RazerZone.Zone1
                ? (byte)23
                : (byte)24;
        }
        else if (request.CommandId == RazerCommands.GetPerformanceAndFanModeCommandId)
        {
            arguments[2] = 0x00;
            arguments[3] = 0x00;
        }
        else
        {
            arguments[2] = arguments[1] == (byte)RazerPerformanceCluster.Cpu
                ? (byte)0x03
                : (byte)0x02;
        }

        return CreateResponse(request, arguments: arguments);
    }

    internal static RazerPacket CreateResponse(
        RazerPacket request,
        byte status = (byte)RazerResponseStatus.Success,
        byte? transactionId = null,
        ushort? remainingPackets = null,
        byte? commandClass = null,
        byte? commandId = null,
        byte? dataSize = null,
        byte[]? arguments = null)
    {
        return new RazerPacket(
            status,
            transactionId ?? request.TransactionId,
            remainingPackets ?? request.RemainingPackets,
            request.ProtocolType,
            dataSize ?? request.DataSize,
            commandClass ?? request.CommandClass,
            commandId ?? request.CommandId,
            arguments ?? request.Arguments.ToArray(),
            crc: 0,
            reserved: 0);
    }
}
