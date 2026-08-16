using BladeControl.Razer.Protocol;

namespace BladeControl.Razer.Tests;

[TestClass]
public sealed class RazerClientTests
{
    [TestMethod]
    public void GlobalTraceObserverReceivesEveryStatusExchange()
    {
        using var transport = new ScriptedRazerTransport();
        var client = new RazerClient(
            transport,
            new SequenceTransactionIdSource(1, 2, 3, 4, 5, 6));
        var observed = new List<RazerExchangeTrace>();
        client.ExchangeCompleted += observed.Add;

        _ = client.GetStatus();

        Assert.AreEqual(6, transport.CallCount);
        Assert.AreEqual(6, observed.Count);
        CollectionAssert.AreEqual(
            new byte[] { 1, 2, 3, 4, 5, 6 },
            observed.Select(exchange => exchange.TransactionId).ToArray());
    }

    [TestMethod]
    public void GlobalTraceObserverRecordsTransportFailureWithoutRetry()
    {
        using var transport = new ScriptedRazerTransport(
            (_, _) => throw new InvalidOperationException("injected transport failure"));
        var client = new RazerClient(
            transport,
            new SequenceTransactionIdSource(0x44));
        var observed = new List<RazerExchangeTrace>();
        client.ExchangeCompleted += observed.Add;

        Assert.ThrowsException<InvalidOperationException>(() =>
            client.GetFanRpm(RazerZone.Zone1));

        Assert.AreEqual(1, transport.CallCount);
        Assert.AreEqual(1, observed.Count);
        Assert.AreEqual(0x44, observed[0].TransactionId);
        Assert.IsFalse(observed[0].HasResponse);
    }

    [TestMethod]
    public void GetStatusUsesSixOrderedWhitelistedGetsAndInjectedTransactions()
    {
        using var transport = new ScriptedRazerTransport();
        var transactions = new SequenceTransactionIdSource(
            0x21,
            0x22,
            0x23,
            0x24,
            0x25,
            0x26);
        var client = new RazerClient(transport, transactions);

        RazerStatusSnapshot status = client.GetStatus();

        Assert.AreEqual(2300, status.Fan1.FirmwareReportedRpm);
        Assert.AreEqual(2400, status.Fan2.FirmwareReportedRpm);
        Assert.AreEqual("Balanced", status.PerformanceMode.ToString());
        Assert.AreEqual("Boost", status.CpuPerformanceLevel.ToString());
        Assert.AreEqual("High", status.GpuPerformanceLevel.ToString());
        Assert.AreEqual("Auto", status.FanMode.ToString());
        Assert.AreEqual(6, transport.CallCount);
        Assert.AreEqual(6, status.Exchanges.Count);

        ushort[] commands = transport.Requests
            .Select(report => RazerPacketCodec.DecodeHidFeatureReport(report))
            .Select(packet => packet.CombinedCommand)
            .ToArray();
        CollectionAssert.AreEqual(
            new ushort[] { 0x0D81, 0x0D81, 0x0D82, 0x0D82, 0x0D87, 0x0D87 },
            commands);

        byte[] selectors = transport.Requests
            .Select(report => RazerPacketCodec.DecodeHidFeatureReport(report))
            .Select(packet => packet.Arguments[1])
            .ToArray();
        CollectionAssert.AreEqual(
            new byte[] { 0x01, 0x02, 0x01, 0x02, 0x01, 0x02 },
            selectors);

        CollectionAssert.AreEqual(
            new byte[] { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26 },
            status.Exchanges.Select(exchange => exchange.TransactionId).ToArray());

        foreach (byte[] request in transport.Requests)
        {
            Assert.AreEqual(91, request.Length);
            Assert.AreEqual(0, request[0]);
            RazerPacket packet = RazerPacketCodec.DecodeHidFeatureReport(request);
            Assert.AreEqual(
                RazerPacketCodec.CalculateChecksum(request.AsSpan(1)),
                packet.Crc);
            Assert.AreEqual(0, packet.Reserved);
            Assert.IsTrue(packet.Arguments[packet.DataSize..].ToArray().All(value => value == 0));
        }
    }

    [TestMethod]
    public void TransactionMismatchIsRejectedWithoutRetry()
    {
        using var transport = new ScriptedRazerTransport(
            (_, request) => ScriptedRazerTransport.CreateResponse(
                request,
                transactionId: 0x7F));
        var client = CreateClient(transport);

        RazerProtocolException exception = Assert.ThrowsException<RazerProtocolException>(
            () => client.GetFanRpm(RazerZone.Zone1));

        StringAssert.Contains(exception.Message, "transaction ID");
        Assert.AreEqual(1, transport.CallCount);
        Assert.AreEqual(1, exception.Exchanges.Count);
    }

    [TestMethod]
    public void CommandMismatchIsRejectedWithoutRetry()
    {
        using var transport = new ScriptedRazerTransport(
            (_, request) => ScriptedRazerTransport.CreateResponse(
                request,
                commandId: 0x82));
        var client = CreateClient(transport);

        RazerProtocolException exception = Assert.ThrowsException<RazerProtocolException>(
            () => client.GetFanRpm(RazerZone.Zone1));

        StringAssert.Contains(exception.Message, "command ID");
        Assert.AreEqual(1, transport.CallCount);
    }

    [DataTestMethod]
    [DataRow((byte)0x01, "Busy")]
    [DataRow((byte)0x03, "Failure")]
    [DataRow((byte)0x04, "Timeout")]
    [DataRow((byte)0x05, "NotSupported")]
    [DataRow((byte)0x7E, "Unknown(0x7E)")]
    public void NonsuccessAndUnknownStatusesAreRejectedSafely(
        byte status,
        string expectedText)
    {
        using var transport = new ScriptedRazerTransport(
            (_, request) => ScriptedRazerTransport.CreateResponse(
                request,
                status: status));
        var client = CreateClient(transport);

        RazerProtocolException exception = Assert.ThrowsException<RazerProtocolException>(
            () => client.GetFanRpm(RazerZone.Zone1));

        StringAssert.Contains(exception.Message, expectedText);
        Assert.AreEqual(1, transport.CallCount);
    }

    [TestMethod]
    public void CommandClassMismatchIsRejectedWithoutRetry()
    {
        using var transport = new ScriptedRazerTransport(
            (_, request) => ScriptedRazerTransport.CreateResponse(
                request,
                commandClass: 0x0E));
        var client = CreateClient(transport);

        RazerProtocolException exception = Assert.ThrowsException<RazerProtocolException>(
            () => client.GetFanRpm(RazerZone.Zone1));

        StringAssert.Contains(exception.Message, "command class");
        Assert.AreEqual(1, transport.CallCount);
    }

    [TestMethod]
    public void ShortResponsePayloadIsRejectedWithoutRetry()
    {
        using var transport = new ScriptedRazerTransport(
            (_, request) => ScriptedRazerTransport.CreateResponse(
                request,
                dataSize: 2));
        var client = CreateClient(transport);

        RazerProtocolException exception = Assert.ThrowsException<RazerProtocolException>(
            () => client.GetFanRpm(RazerZone.Zone1));

        StringAssert.Contains(exception.Message, "data size");
        Assert.AreEqual(1, transport.CallCount);
    }

    [TestMethod]
    public void ReturnedZoneMismatchIsRejected()
    {
        using var transport = new ScriptedRazerTransport((_, request) =>
        {
            RazerPacket response = ScriptedRazerTransport.CreateSuccessfulResponse(request);
            byte[] arguments = response.Arguments.ToArray();
            arguments[1] = 0x02;
            return ScriptedRazerTransport.CreateResponse(request, arguments: arguments);
        });
        var client = CreateClient(transport);

        RazerProtocolException exception = Assert.ThrowsException<RazerProtocolException>(
            () => client.GetFanRpm(RazerZone.Zone1));

        StringAssert.Contains(exception.Message, "returned zone");
    }

    [TestMethod]
    public void NonzeroRemainingPacketsIsRejected()
    {
        using var transport = new ScriptedRazerTransport(
            (_, request) => ScriptedRazerTransport.CreateResponse(
                request,
                remainingPackets: 1));
        var client = CreateClient(transport);

        RazerProtocolException exception = Assert.ThrowsException<RazerProtocolException>(
            () => client.GetFanRpm(RazerZone.Zone1));

        StringAssert.Contains(exception.Message, "remaining-packets");
    }

    [TestMethod]
    public void FirstValidationFailureStopsStatusSequence()
    {
        using var transport = new ScriptedRazerTransport(
            (call, request) => call == 2
                ? ScriptedRazerTransport.CreateResponse(request, transactionId: 0x7F)
                : ScriptedRazerTransport.CreateSuccessfulResponse(request));
        var client = new RazerClient(
            transport,
            new SequenceTransactionIdSource(1, 2, 3, 4));

        Assert.ThrowsException<RazerProtocolException>(() => client.GetStatus());
        Assert.AreEqual(2, transport.CallCount);
    }

    [TestMethod]
    public void CrossZoneModeDisagreementIsRejected()
    {
        using var transport = new ScriptedRazerTransport((_, request) =>
        {
            RazerPacket response = ScriptedRazerTransport.CreateSuccessfulResponse(request);
            if (request.CommandId == RazerCommands.GetPerformanceAndFanModeCommandId &&
                request.Arguments[1] == (byte)RazerZone.Zone2)
            {
                byte[] arguments = response.Arguments.ToArray();
                arguments[2] = 0x04;
                return ScriptedRazerTransport.CreateResponse(request, arguments: arguments);
            }

            return response;
        });
        var client = new RazerClient(
            transport,
            new SequenceTransactionIdSource(1, 2, 3, 4));

        RazerProtocolException exception = Assert.ThrowsException<RazerProtocolException>(
            () => client.GetStatus());

        StringAssert.Contains(exception.Message, "differs between returned zones");
        Assert.AreEqual(4, transport.CallCount);
        Assert.AreEqual(4, exception.Exchanges.Count);
    }

    [TestMethod]
    public void UnknownModeValuesRenderWithoutThrowing()
    {
        Assert.AreEqual("Unknown(0xA7)", new RazerPerformanceMode(0xA7).ToString());
        Assert.AreEqual("Unknown(0xFE)", new RazerFanMode(0xFE).ToString());
    }

    [TestMethod]
    public void ChecksumMismatchStopsStatusSequenceWithoutRetry()
    {
        using var transport = new ScriptedRazerTransport
        {
            CorruptResponseChecksum = true
        };
        var client = new RazerClient(
            transport,
            new SequenceTransactionIdSource(1, 2, 3, 4));

        RazerProtocolException exception = Assert.ThrowsException<RazerProtocolException>(
            () => client.GetStatus());

        StringAssert.Contains(exception.Message, "checksum mismatch");
        StringAssert.Contains(exception.Message, "expected 0x99");
        StringAssert.Contains(exception.Message, "received 0x00");
        Assert.AreEqual(1, transport.CallCount);
        Assert.AreEqual(1, exception.Exchanges.Count);
    }

    private static RazerClient CreateClient(ScriptedRazerTransport transport)
    {
        return new RazerClient(
            transport,
            new SequenceTransactionIdSource(0x2A));
    }
}
