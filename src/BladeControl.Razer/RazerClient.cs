using BladeControl.Razer.Protocol;

namespace BladeControl.Razer;

public sealed class RazerClient
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
        RazerFanReading fan1 = GetFanRpm(RazerZone.Zone1);
        RazerFanReading fan2 = GetFanRpm(RazerZone.Zone2);
        RazerModeReading zone1Mode = GetPerformanceAndFanMode(RazerZone.Zone1);
        RazerModeReading zone2Mode = GetPerformanceAndFanMode(RazerZone.Zone2);

        if (zone1Mode.PerformanceMode != zone2Mode.PerformanceMode ||
            zone1Mode.FanMode != zone2Mode.FanMode)
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
