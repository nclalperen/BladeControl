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

    internal int? CorruptResponseChecksumOnCall { get; init; }

    public RazerTransportResponse Exchange(RazerTransportRequest request)
    {
        byte[] report = request.FeatureReport.ToArray();
        Requests.Add(report);
        RazerPacket requestPacket = RazerPacketCodec.DecodeHidFeatureReport(report);
        RazerPacket response = _responseFactory?.Invoke(CallCount, requestPacket) ??
            CreateSuccessfulResponse(requestPacket);

        byte[] responseReport = RazerPacketCodec.EncodeHidFeatureReport(response);
        if (CorruptResponseChecksum || CorruptResponseChecksumOnCall == CallCount)
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
        else if (request.CommandId == RazerCommands.GetPerformanceBoostLevelCommandId)
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

internal sealed class StatefulPerformanceTransport : IRazerTransport
{
    private int _writeCount;

    internal StatefulPerformanceTransport(
        RazerPerformanceMode mode,
        RazerCpuPerformanceLevel cpu,
        RazerGpuPerformanceLevel gpu)
    {
        Zone1Mode = mode;
        Zone2Mode = mode;
        CpuLevel = cpu;
        GpuLevel = gpu;
    }

    public RazerDeviceInfo DeviceInfo { get; } = new(
        @"\\?\hid#stateful-test",
        0x1532,
        0x029F,
        0x0001,
        0x0002,
        91);

    internal RazerPerformanceMode Zone1Mode { get; set; }

    internal RazerPerformanceMode Zone2Mode { get; set; }

    internal RazerFanMode Zone1FanMode { get; set; } = RazerFanMode.Auto;

    internal RazerFanMode Zone2FanMode { get; set; } = RazerFanMode.Auto;

    internal RazerCpuPerformanceLevel CpuLevel { get; set; }

    internal RazerGpuPerformanceLevel GpuLevel { get; set; }

    internal HashSet<int> FailWriteNumbers { get; } = [];

    internal HashSet<int> FailCallNumbers { get; } = [];

    internal HashSet<int> CorruptWriteResponseNumbers { get; } = [];

    internal HashSet<int> IgnoreWriteNumbers { get; } = [];

    internal HashSet<int> WrongEchoWriteNumbers { get; } = [];

    internal List<RazerPacket> Requests { get; } = [];

    internal IReadOnlyList<RazerPacket> WriteRequests => Requests
        .Where(packet => packet.CommandId is
            RazerCommands.WriteBackPerformanceAndFanModeCommandId or
            RazerCommands.WriteBackPerformanceLevelCommandId)
        .ToArray();

    internal int WriteCount => _writeCount;

    public RazerTransportResponse Exchange(RazerTransportRequest request)
    {
        RazerPacket packet = RazerPacketCodec.DecodeHidFeatureReport(
            request.FeatureReport.Span);
        Requests.Add(packet);
        bool isWrite = packet.CommandId is
            RazerCommands.WriteBackPerformanceAndFanModeCommandId or
            RazerCommands.WriteBackPerformanceLevelCommandId;
        if (isWrite)
        {
            _writeCount++;
        }

        bool fail = FailCallNumbers.Contains(Requests.Count) ||
            (isWrite && FailWriteNumbers.Contains(_writeCount));
        if (isWrite && !fail && !IgnoreWriteNumbers.Contains(_writeCount))
        {
            ApplyWrite(packet);
        }

        byte[] arguments = CreateResponseArguments(packet);
        if (isWrite && WrongEchoWriteNumbers.Contains(_writeCount))
        {
            arguments[2] ^= 0x01;
        }
        RazerPacket response = ScriptedRazerTransport.CreateResponse(
            packet,
            status: fail
                ? (byte)RazerResponseStatus.Failure
                : (byte)RazerResponseStatus.Success,
            arguments: arguments);
        byte[] report = RazerPacketCodec.EncodeHidFeatureReport(response);
        if (isWrite && CorruptWriteResponseNumbers.Contains(_writeCount))
        {
            report[89] ^= 0xFF;
        }

        return new RazerTransportResponse(report);
    }

    public void Dispose()
    {
    }

    private void ApplyWrite(RazerPacket packet)
    {
        if (packet.CommandId == RazerCommands.WriteBackPerformanceAndFanModeCommandId)
        {
            var mode = new RazerPerformanceMode(packet.Arguments[2]);
            var fanMode = new RazerFanMode(packet.Arguments[3]);
            if (packet.Arguments[1] == (byte)RazerZone.Zone1)
            {
                Zone1Mode = mode;
                Zone1FanMode = fanMode;
            }
            else
            {
                Zone2Mode = mode;
                Zone2FanMode = fanMode;
            }
        }
        else if (packet.Arguments[1] == (byte)RazerPerformanceCluster.Cpu)
        {
            CpuLevel = new RazerCpuPerformanceLevel(packet.Arguments[2]);
        }
        else
        {
            GpuLevel = new RazerGpuPerformanceLevel(packet.Arguments[2]);
        }
    }

    private byte[] CreateResponseArguments(RazerPacket request)
    {
        byte[] arguments = request.Arguments.ToArray();
        if (request.CommandId == RazerCommands.GetFanRpmCommandId)
        {
            arguments[2] = 20;
        }
        else if (request.CommandId == RazerCommands.GetPerformanceAndFanModeCommandId)
        {
            bool zone1 = request.Arguments[1] == (byte)RazerZone.Zone1;
            arguments[2] = zone1 ? Zone1Mode.Value : Zone2Mode.Value;
            arguments[3] = zone1 ? Zone1FanMode.Value : Zone2FanMode.Value;
        }
        else if (request.CommandId == RazerCommands.GetPerformanceBoostLevelCommandId)
        {
            arguments[2] = request.Arguments[1] == (byte)RazerPerformanceCluster.Cpu
                ? CpuLevel.Value
                : GpuLevel.Value;
        }

        return arguments;
    }
}
