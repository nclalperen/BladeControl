using BladeControl.Razer.Protocol;

namespace BladeControl.Razer;

public sealed partial class RazerClient
{
    private readonly IRazerTransport _transport;
    private readonly ITransactionIdSource _transactionIds;

    internal RazerClient(IRazerTransport transport)
        : this(transport, new SequentialTransactionIdSource())
    {
    }

    internal RazerClient(
        IRazerTransport transport,
        ITransactionIdSource transactionIds)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _transactionIds = transactionIds ?? throw new ArgumentNullException(nameof(transactionIds));
    }

    public RazerFanReading GetFanRpm(RazerZone zone)
    {
        byte transactionId = _transactionIds.NextTransactionId();
        RazerPacket request = RazerCommands.CreateGetFanRpm(transactionId, zone);
        (RazerPacket response, RazerExchangeTrace exchange) = ExchangeAndValidate(
            request,
            expectedSelector: (byte)zone,
            selectorName: "zone",
            minimumResponseDataSize: 3);

        int rpm = response.Arguments[2] * 100;
        return new RazerFanReading(zone, rpm, exchange);
    }

    public RazerModeReading GetPerformanceAndFanMode(RazerZone zone)
    {
        byte transactionId = _transactionIds.NextTransactionId();
        RazerPacket request = RazerCommands.CreateGetPerformanceAndFanMode(
            transactionId,
            zone);
        (RazerPacket response, RazerExchangeTrace exchange) = ExchangeAndValidate(
            request,
            expectedSelector: (byte)zone,
            selectorName: "zone",
            minimumResponseDataSize: 4);

        return new RazerModeReading(
            zone,
            new RazerPerformanceMode(response.Arguments[2]),
            new RazerFanMode(response.Arguments[3]),
            exchange);
    }

    public RazerStatusSnapshot GetStatus()
    {
        return ReadCompleteStatus(requireZoneAgreement: true);
    }

    public RazerModeWriteBackResult RunCustomAutoModeWriteBackTest()
    {
        RazerStatusSnapshot preWriteState =
            ReadCompleteStatus(requireZoneAgreement: false);
        ValidateWriteBackPreconditions(preWriteState);

        var writeExchanges = new List<RazerExchangeTrace>(capacity: 2);
        RazerExchangeTrace zone1Write;
        try
        {
            zone1Write = WriteBackCustomAutoMode(RazerZone.Zone1);
            writeExchanges.Add(zone1Write);
        }
        catch (RazerProtocolException exception)
        {
            throw CreateWriteBackValidationException(
                "Zone 1",
                preWriteState,
                writeExchanges,
                exception);
        }

        RazerExchangeTrace zone2Write;
        try
        {
            zone2Write = WriteBackCustomAutoMode(RazerZone.Zone2);
            writeExchanges.Add(zone2Write);
        }
        catch (RazerProtocolException exception)
        {
            throw CreateWriteBackValidationException(
                "Zone 2",
                preWriteState,
                writeExchanges,
                exception);
        }

        RazerStatusSnapshot postWriteState =
            ReadCompleteStatus(requireZoneAgreement: false);
        return new RazerModeWriteBackResult(
            preWriteState,
            zone1Write,
            zone2Write,
            postWriteState);
    }

    public RazerPerformanceLevelWriteBackResult RunPerformanceLevelWriteBackTest()
    {
        RazerStatusSnapshot preWriteState =
            ReadCompleteStatus(requireZoneAgreement: false);
        ValidatePerformanceLevelWriteBackPreconditions(preWriteState);

        var writeExchanges = new List<RazerExchangeTrace>(capacity: 2);
        RazerExchangeTrace cpuWrite;
        try
        {
            cpuWrite = WriteBackExpectedPerformanceLevel(
                RazerPerformanceCluster.Cpu);
            writeExchanges.Add(cpuWrite);
        }
        catch (RazerProtocolException exception)
        {
            throw CreatePerformanceLevelWriteBackValidationException(
                "CPU",
                preWriteState,
                writeExchanges,
                exception);
        }

        RazerExchangeTrace gpuWrite;
        try
        {
            gpuWrite = WriteBackExpectedPerformanceLevel(
                RazerPerformanceCluster.Gpu);
            writeExchanges.Add(gpuWrite);
        }
        catch (RazerProtocolException exception)
        {
            throw CreatePerformanceLevelWriteBackValidationException(
                "GPU",
                preWriteState,
                writeExchanges,
                exception);
        }

        RazerStatusSnapshot postWriteState =
            ReadCompleteStatus(requireZoneAgreement: false);
        return new RazerPerformanceLevelWriteBackResult(
            preWriteState,
            cpuWrite,
            gpuWrite,
            postWriteState);
    }

    private RazerStatusSnapshot ReadCompleteStatus(bool requireZoneAgreement)
    {
        RazerFanReading fan1 = GetFanRpm(RazerZone.Zone1);
        RazerFanReading fan2 = GetFanRpm(RazerZone.Zone2);
        RazerModeReading zone1Mode = GetPerformanceAndFanMode(RazerZone.Zone1);
        RazerModeReading zone2Mode = GetPerformanceAndFanMode(RazerZone.Zone2);

        if (requireZoneAgreement &&
            (zone1Mode.PerformanceMode != zone2Mode.PerformanceMode ||
            zone1Mode.FanMode != zone2Mode.FanMode)
           )
        {
            throw new RazerProtocolException(
                "Performance or fan mode differs between returned zones; no single firmware state can be reported.",
                [fan1.Exchange, fan2.Exchange, zone1Mode.Exchange, zone2Mode.Exchange]);
        }

        (byte cpuLevel, RazerExchangeTrace cpuExchange) =
            GetPerformanceBoostLevel(RazerPerformanceCluster.Cpu);
        (byte gpuLevel, RazerExchangeTrace gpuExchange) =
            GetPerformanceBoostLevel(RazerPerformanceCluster.Gpu);

        return new RazerStatusSnapshot(
            _transport.DeviceInfo,
            fan1,
            fan2,
            zone1Mode,
            zone2Mode,
            new RazerCpuPerformanceLevel(cpuLevel),
            new RazerGpuPerformanceLevel(gpuLevel),
            cpuExchange,
            gpuExchange);
    }

    private RazerExchangeTrace WriteBackCustomAutoMode(RazerZone zone)
    {
        return WritePerformanceMode(zone, RazerPerformanceMode.Custom);
    }

    private RazerExchangeTrace WriteBackExpectedPerformanceLevel(
        RazerPerformanceCluster cluster)
    {
        return cluster switch
        {
            RazerPerformanceCluster.Cpu => WriteCpuPerformanceLevel(
                RazerCpuPerformanceLevel.Medium),
            RazerPerformanceCluster.Gpu => WriteGpuPerformanceLevel(
                RazerGpuPerformanceLevel.Low),
            _ => throw new ArgumentOutOfRangeException(nameof(cluster), cluster, null)
        };
    }

    private static void ValidateWriteBackPreconditions(
        RazerStatusSnapshot preWriteState)
    {
        RazerModeReading zone1 = preWriteState.Zone1Mode;
        RazerModeReading zone2 = preWriteState.Zone2Mode;

        if (zone1.PerformanceMode != zone2.PerformanceMode ||
            zone1.FanMode != zone2.FanMode)
        {
            throw new RazerModeWriteBackPreconditionException(
                "Write-back precondition failed: performance or fan mode disagrees " +
                "between zones. No 0x0D02 packet was sent.",
                preWriteState);
        }

        if (zone1.PerformanceMode.Value != 0x04 ||
            zone2.PerformanceMode.Value != 0x04)
        {
            throw new RazerModeWriteBackPreconditionException(
                "Write-back precondition failed: both zones must already report " +
                "Custom performance mode (0x04). No 0x0D02 packet was sent.",
                preWriteState);
        }

        if (zone1.FanMode.Value != 0x00 ||
            zone2.FanMode.Value != 0x00)
        {
            throw new RazerModeWriteBackPreconditionException(
                "Write-back precondition failed: both zones must already report " +
                "Auto fan mode (0x00). No 0x0D02 packet was sent.",
                preWriteState);
        }
    }

    private static void ValidatePerformanceLevelWriteBackPreconditions(
        RazerStatusSnapshot preWriteState)
    {
        RazerModeReading zone1 = preWriteState.Zone1Mode;
        RazerModeReading zone2 = preWriteState.Zone2Mode;

        if (zone1.PerformanceMode != zone2.PerformanceMode ||
            zone1.FanMode != zone2.FanMode)
        {
            throw new RazerPerformanceLevelWriteBackPreconditionException(
                "Performance-level write-back precondition failed: performance " +
                "or fan mode disagrees between zones. No 0x0D07 packet was sent.",
                preWriteState);
        }

        if (zone1.PerformanceMode.Value != 0x04 ||
            zone2.PerformanceMode.Value != 0x04)
        {
            throw new RazerPerformanceLevelWriteBackPreconditionException(
                "Performance-level write-back precondition failed: both zones " +
                "must report Custom performance mode (0x04). " +
                "No 0x0D07 packet was sent.",
                preWriteState);
        }

        if (zone1.FanMode.Value != 0x00 ||
            zone2.FanMode.Value != 0x00)
        {
            throw new RazerPerformanceLevelWriteBackPreconditionException(
                "Performance-level write-back precondition failed: both zones " +
                "must report Auto fan mode (0x00). No 0x0D07 packet was sent.",
                preWriteState);
        }

        if (preWriteState.CpuPerformanceLevel.Value != 0x01)
        {
            throw new RazerPerformanceLevelWriteBackPreconditionException(
                "Performance-level write-back precondition failed: CPU must " +
                "report Medium (0x01). No 0x0D07 packet was sent.",
                preWriteState);
        }

        if (preWriteState.GpuPerformanceLevel.Value != 0x00)
        {
            throw new RazerPerformanceLevelWriteBackPreconditionException(
                "Performance-level write-back precondition failed: GPU must " +
                "report Low (0x00). No 0x0D07 packet was sent.",
                preWriteState);
        }
    }

    private static RazerModeWriteBackValidationException
        CreateWriteBackValidationException(
            string stage,
            RazerStatusSnapshot preWriteState,
            IReadOnlyList<RazerExchangeTrace> completedWriteExchanges,
            RazerProtocolException exception)
    {
        RazerExchangeTrace[] writeExchanges =
        [
            .. completedWriteExchanges,
            .. exception.Exchanges
        ];
        return new RazerModeWriteBackValidationException(
            stage,
            preWriteState,
            writeExchanges,
            exception);
    }

    private static RazerPerformanceLevelWriteBackValidationException
        CreatePerformanceLevelWriteBackValidationException(
            string stage,
            RazerStatusSnapshot preWriteState,
            IReadOnlyList<RazerExchangeTrace> completedWriteExchanges,
            RazerProtocolException exception)
    {
        RazerExchangeTrace[] writeExchanges =
        [
            .. completedWriteExchanges,
            .. exception.Exchanges
        ];
        return new RazerPerformanceLevelWriteBackValidationException(
            stage,
            preWriteState,
            writeExchanges,
            exception);
    }

    private static string FormatHex(ReadOnlySpan<byte> values)
    {
        return string.Join(' ', values.ToArray().Select(value => value.ToString("X2")));
    }

    private (byte Level, RazerExchangeTrace Exchange) GetPerformanceBoostLevel(
        RazerPerformanceCluster cluster)
    {
        byte transactionId = _transactionIds.NextTransactionId();
        RazerPacket request = RazerCommands.CreateGetPerformanceBoostLevel(
            transactionId,
            cluster);
        (RazerPacket response, RazerExchangeTrace exchange) = ExchangeAndValidate(
            request,
            expectedSelector: (byte)cluster,
            selectorName: "cluster",
            minimumResponseDataSize: 3);

        return (response.Arguments[2], exchange);
    }

    private (RazerPacket Response, RazerExchangeTrace Exchange) ExchangeAndValidate(
        RazerPacket request,
        byte expectedSelector,
        string selectorName,
        byte minimumResponseDataSize)
    {
        RazerCommands.EnsureAllowed(request);
        var transportRequest = new RazerTransportRequest(request);
        RazerTransportResponse transportResponse = _transport.Exchange(transportRequest);

        byte[] requestReport = transportRequest.FeatureReport.ToArray();
        byte[] responseReport = transportResponse.FeatureReport.ToArray();
        var exchange = new RazerExchangeTrace(
            request.TransactionId,
            request.CommandClass,
            request.CommandId,
            requestReport,
            responseReport);

        RazerPacket response;
        try
        {
            response = RazerPacketCodec.DecodeHidFeatureReport(responseReport);
        }
        catch (ArgumentException exception)
        {
            throw new RazerProtocolException(
                $"Malformed response for command 0x{request.CombinedCommand:X4}, " +
                $"requested {selectorName} 0x{expectedSelector:X2}: {exception.Message}",
                [exchange],
                exception);
        }

        ValidateResponse(
            request,
            response,
            expectedSelector,
            selectorName,
            minimumResponseDataSize,
            exchange);
        return (response, exchange);
    }

    private static void ValidateResponse(
        RazerPacket request,
        RazerPacket response,
        byte expectedSelector,
        string selectorName,
        byte minimumResponseDataSize,
        RazerExchangeTrace exchange)
    {
        if (response.Status != (byte)RazerResponseStatus.Success)
        {
            throw ValidationFailure(
                request,
                exchange,
                "response status",
                "Success (0x02)",
                $"{RazerResponseStatusFormatter.Format(response.Status)} (0x{response.Status:X2})");
        }

        if (response.TransactionId != request.TransactionId)
        {
            throw ValidationFailure(
                request,
                exchange,
                "transaction ID",
                $"0x{request.TransactionId:X2}",
                $"0x{response.TransactionId:X2}");
        }

        if (response.CommandClass != request.CommandClass)
        {
            throw ValidationFailure(
                request,
                exchange,
                "command class",
                $"0x{request.CommandClass:X2}",
                $"0x{response.CommandClass:X2}");
        }

        if (response.CommandId != request.CommandId)
        {
            throw ValidationFailure(
                request,
                exchange,
                "command ID",
                $"0x{request.CommandId:X2}",
                $"0x{response.CommandId:X2}");
        }

        if (response.RemainingPackets != request.RemainingPackets)
        {
            throw ValidationFailure(
                request,
                exchange,
                "remaining-packets value",
                request.RemainingPackets.ToString(),
                response.RemainingPackets.ToString());
        }

        if (response.DataSize < minimumResponseDataSize)
        {
            throw ValidationFailure(
                request,
                exchange,
                "response data size",
                $"at least {minimumResponseDataSize}",
                response.DataSize.ToString());
        }

        if (response.Arguments[1] != expectedSelector)
        {
            throw ValidationFailure(
                request,
                exchange,
                $"returned {selectorName}",
                $"0x{expectedSelector:X2}",
                $"0x{response.Arguments[1]:X2}");
        }
    }

    private static RazerProtocolException ValidationFailure(
        RazerPacket request,
        RazerExchangeTrace exchange,
        string field,
        string expected,
        string actual)
    {
        return new RazerProtocolException(
            $"Response validation failed for command 0x{request.CombinedCommand:X4}: " +
            $"{field} expected {expected}, received {actual}. No retry was attempted.",
            [exchange]);
    }
}
