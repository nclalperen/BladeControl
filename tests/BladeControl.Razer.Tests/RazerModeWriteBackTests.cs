using BladeControl.Razer.Protocol;

namespace BladeControl.Razer.Tests;

[TestClass]
public sealed class RazerModeWriteBackTests
{
    [TestMethod]
    public void SuccessfulWriteBackUsesExactOrderAndValidatesBothEchoes()
    {
        using var transport = CreateWriteBackTransport();
        RazerClient client = CreateWriteBackClient(transport);

        RazerModeWriteBackResult result = client.RunCustomAutoModeWriteBackTest();

        Assert.IsTrue(result.Passed);
        Assert.IsFalse(result.StateDriftDetected);
        Assert.AreEqual(14, transport.CallCount);
        Assert.AreEqual(14, result.Exchanges.Count);
        CollectionAssert.AreEqual(
            new ushort[]
            {
                0x0D81, 0x0D81, 0x0D82, 0x0D82, 0x0D87, 0x0D87,
                0x0D02, 0x0D02,
                0x0D81, 0x0D81, 0x0D82, 0x0D82, 0x0D87, 0x0D87
            },
            transport.Requests
                .Select(report => RazerPacketCodec.DecodeHidFeatureReport(report))
                .Select(packet => packet.CombinedCommand)
                .ToArray());
        AssertWriteEcho(
            result.Zone1WriteExchange,
            new byte[] { 0x01, 0x01, 0x04, 0x00 });
        AssertWriteEcho(
            result.Zone2WriteExchange,
            new byte[] { 0x01, 0x02, 0x04, 0x00 });
    }

    [TestMethod]
    public void WrongZoneEchoStopsBeforeZone2Write()
    {
        AssertWriteValidationFailure(
            (call, request) =>
            {
                RazerPacket response = CreateCustomAutoResponse(call, request);
                if (request.CommandId == RazerCommands.WriteBackPerformanceAndFanModeCommandId)
                {
                    byte[] arguments = response.Arguments.ToArray();
                    arguments[1] = 0x02;
                    return ScriptedRazerTransport.CreateResponse(request, arguments: arguments);
                }

                return response;
            },
            "returned zone",
            expectedCalls: 7);
    }

    [TestMethod]
    public void WrongSetSelectorEchoStopsBeforeZone2Write()
    {
        AssertWriteValidationFailure(
            (call, request) =>
            {
                RazerPacket response = CreateCustomAutoResponse(call, request);
                if (request.CommandId == RazerCommands.WriteBackPerformanceAndFanModeCommandId)
                {
                    byte[] arguments = response.Arguments.ToArray();
                    arguments[0] = 0x00;
                    return ScriptedRazerTransport.CreateResponse(request, arguments: arguments);
                }

                return response;
            },
            "response argument echo",
            expectedCalls: 7);
    }

    [TestMethod]
    public void WrongModeEchoStopsBeforeZone2Write()
    {
        AssertWriteValidationFailure(
            (call, request) =>
            {
                RazerPacket response = CreateCustomAutoResponse(call, request);
                if (request.CommandId == RazerCommands.WriteBackPerformanceAndFanModeCommandId)
                {
                    byte[] arguments = response.Arguments.ToArray();
                    arguments[2] = 0x00;
                    return ScriptedRazerTransport.CreateResponse(request, arguments: arguments);
                }

                return response;
            },
            "response argument echo",
            expectedCalls: 7);
    }

    [TestMethod]
    public void WrongFanModeEchoStopsBeforeZone2Write()
    {
        AssertWriteValidationFailure(
            (call, request) =>
            {
                RazerPacket response = CreateCustomAutoResponse(call, request);
                if (request.CommandId == RazerCommands.WriteBackPerformanceAndFanModeCommandId)
                {
                    byte[] arguments = response.Arguments.ToArray();
                    arguments[3] = 0x01;
                    return ScriptedRazerTransport.CreateResponse(request, arguments: arguments);
                }

                return response;
            },
            "response argument echo",
            expectedCalls: 7);
    }

    [TestMethod]
    public void ShortWriteResponseStopsBeforeZone2Write()
    {
        AssertWriteValidationFailure(
            (call, request) => request.CommandId ==
                RazerCommands.WriteBackPerformanceAndFanModeCommandId
                ? ScriptedRazerTransport.CreateResponse(request, dataSize: 3)
                : CreateCustomAutoResponse(call, request),
            "response data size",
            expectedCalls: 7);
    }

    [TestMethod]
    public void BadWriteResponseChecksumStopsBeforeZone2Write()
    {
        using var transport = new ScriptedRazerTransport(CreateCustomAutoResponse)
        {
            CorruptResponseChecksumOnCall = 7
        };
        RazerClient client = CreateWriteBackClient(transport);

        RazerModeWriteBackValidationException exception =
            Assert.ThrowsException<RazerModeWriteBackValidationException>(
                () => client.RunCustomAutoModeWriteBackTest());

        StringAssert.Contains(
            exception.InnerException?.Message ?? string.Empty,
            "checksum mismatch");
        Assert.AreEqual(7, transport.CallCount);
        Assert.AreEqual(1, exception.WriteExchanges.Count);
    }

    [TestMethod]
    public void WriteTransactionMismatchStopsBeforeZone2Write()
    {
        AssertWriteValidationFailure(
            (call, request) => request.CommandId ==
                RazerCommands.WriteBackPerformanceAndFanModeCommandId
                ? ScriptedRazerTransport.CreateResponse(
                    request,
                    transactionId: unchecked((byte)(request.TransactionId + 1)))
                : CreateCustomAutoResponse(call, request),
            "transaction ID",
            expectedCalls: 7);
    }

    [TestMethod]
    public void NonCustomPreconditionCompletesAllReadsAndSendsNoWrite()
    {
        using var transport = new ScriptedRazerTransport();
        RazerClient client = CreateWriteBackClient(transport);

        RazerModeWriteBackPreconditionException exception =
            Assert.ThrowsException<RazerModeWriteBackPreconditionException>(
                () => client.RunCustomAutoModeWriteBackTest());

        StringAssert.Contains(exception.Message, "Custom performance mode");
        Assert.AreEqual(6, transport.CallCount);
        Assert.IsFalse(transport.Requests.Any(IsWriteRequest));
        Assert.AreEqual(6, exception.PreWriteState.Exchanges.Count);
    }

    [TestMethod]
    public void NonAutoPreconditionCompletesAllReadsAndSendsNoWrite()
    {
        using var transport = new ScriptedRazerTransport((call, request) =>
        {
            RazerPacket response = CreateCustomAutoResponse(call, request);
            if (request.CommandId == RazerCommands.GetPerformanceAndFanModeCommandId)
            {
                byte[] arguments = response.Arguments.ToArray();
                arguments[3] = 0x01;
                return ScriptedRazerTransport.CreateResponse(request, arguments: arguments);
            }

            return response;
        });
        RazerClient client = CreateWriteBackClient(transport);

        RazerModeWriteBackPreconditionException exception =
            Assert.ThrowsException<RazerModeWriteBackPreconditionException>(
                () => client.RunCustomAutoModeWriteBackTest());

        StringAssert.Contains(exception.Message, "Auto fan mode");
        Assert.AreEqual(6, transport.CallCount);
        Assert.IsFalse(transport.Requests.Any(IsWriteRequest));
    }

    [TestMethod]
    public void ZoneDisagreementCompletesAllReadsAndSendsNoWrite()
    {
        using var transport = new ScriptedRazerTransport((call, request) =>
        {
            RazerPacket response = CreateCustomAutoResponse(call, request);
            if (request.CommandId == RazerCommands.GetPerformanceAndFanModeCommandId &&
                request.Arguments[1] == (byte)RazerZone.Zone2)
            {
                byte[] arguments = response.Arguments.ToArray();
                arguments[2] = 0x00;
                return ScriptedRazerTransport.CreateResponse(request, arguments: arguments);
            }

            return response;
        });
        RazerClient client = CreateWriteBackClient(transport);

        RazerModeWriteBackPreconditionException exception =
            Assert.ThrowsException<RazerModeWriteBackPreconditionException>(
                () => client.RunCustomAutoModeWriteBackTest());

        StringAssert.Contains(exception.Message, "disagrees between zones");
        Assert.AreEqual(6, transport.CallCount);
        Assert.IsFalse(transport.Requests.Any(IsWriteRequest));
    }

    [TestMethod]
    public void CpuDriftIsDetectedWithoutRollback()
    {
        using var transport = CreateWriteBackTransport(postCpuLevel: 0x02);
        RazerClient client = CreateWriteBackClient(transport);

        RazerModeWriteBackResult result = client.RunCustomAutoModeWriteBackTest();

        Assert.IsTrue(result.StateDriftDetected);
        Assert.IsFalse(result.CpuPerformanceLevelUnchanged);
        Assert.IsTrue(result.GpuPerformanceLevelUnchanged);
        Assert.IsFalse(result.Passed);
        Assert.AreEqual(2, CountWriteRequests(transport));
    }

    [TestMethod]
    public void GpuDriftIsDetectedWithoutRollback()
    {
        using var transport = CreateWriteBackTransport(postGpuLevel: 0x01);
        RazerClient client = CreateWriteBackClient(transport);

        RazerModeWriteBackResult result = client.RunCustomAutoModeWriteBackTest();

        Assert.IsTrue(result.StateDriftDetected);
        Assert.IsTrue(result.CpuPerformanceLevelUnchanged);
        Assert.IsFalse(result.GpuPerformanceLevelUnchanged);
        Assert.IsFalse(result.Passed);
        Assert.AreEqual(2, CountWriteRequests(transport));
    }

    [TestMethod]
    public void FanRpmDifferenceIsAllowed()
    {
        using var transport = CreateWriteBackTransport(changePostFanRpm: true);
        RazerClient client = CreateWriteBackClient(transport);

        RazerModeWriteBackResult result = client.RunCustomAutoModeWriteBackTest();

        Assert.AreNotEqual(
            result.PreWriteState.Fan1.FirmwareReportedRpm,
            result.PostWriteState.Fan1.FirmwareReportedRpm);
        Assert.AreNotEqual(
            result.PreWriteState.Fan2.FirmwareReportedRpm,
            result.PostWriteState.Fan2.FirmwareReportedRpm);
        Assert.IsTrue(result.Passed);
        Assert.IsFalse(result.StateDriftDetected);
    }

    private static ScriptedRazerTransport CreateWriteBackTransport(
        byte postCpuLevel = 0x01,
        byte postGpuLevel = 0x00,
        bool changePostFanRpm = false)
    {
        return new ScriptedRazerTransport(
            (call, request) => CreateCustomAutoResponse(
                call,
                request,
                postCpuLevel,
                postGpuLevel,
                changePostFanRpm));
    }

    private static RazerPacket CreateCustomAutoResponse(
        int call,
        RazerPacket request)
    {
        return CreateCustomAutoResponse(
            call,
            request,
            postCpuLevel: 0x01,
            postGpuLevel: 0x00,
            changePostFanRpm: false);
    }

    private static RazerPacket CreateCustomAutoResponse(
        int call,
        RazerPacket request,
        byte postCpuLevel,
        byte postGpuLevel,
        bool changePostFanRpm)
    {
        RazerPacket response = ScriptedRazerTransport.CreateSuccessfulResponse(request);
        byte[] arguments = response.Arguments.ToArray();

        if (request.CommandId == RazerCommands.GetFanRpmCommandId)
        {
            arguments[2] = changePostFanRpm && call >= 9
                ? (byte)0x1E
                : (byte)0x14;
        }
        else if (request.CommandId == RazerCommands.GetPerformanceAndFanModeCommandId)
        {
            arguments[2] = 0x04;
            arguments[3] = 0x00;
        }
        else if (request.CommandId == RazerCommands.GetPerformanceBoostLevelCommandId)
        {
            bool isPostRead = call >= 9;
            arguments[2] = request.Arguments[1] == (byte)RazerPerformanceCluster.Cpu
                ? isPostRead ? postCpuLevel : (byte)0x01
                : isPostRead ? postGpuLevel : (byte)0x00;
        }

        return ScriptedRazerTransport.CreateResponse(request, arguments: arguments);
    }

    private static RazerClient CreateWriteBackClient(ScriptedRazerTransport transport)
    {
        return new RazerClient(
            transport,
            new SequenceTransactionIdSource(
                1, 2, 3, 4, 5, 6, 7,
                8, 9, 10, 11, 12, 13, 14));
    }

    private static void AssertWriteValidationFailure(
        Func<int, RazerPacket, RazerPacket> responseFactory,
        string expectedMessage,
        int expectedCalls)
    {
        using var transport = new ScriptedRazerTransport(responseFactory);
        RazerClient client = CreateWriteBackClient(transport);

        RazerModeWriteBackValidationException exception =
            Assert.ThrowsException<RazerModeWriteBackValidationException>(
                () => client.RunCustomAutoModeWriteBackTest());

        StringAssert.Contains(
            exception.InnerException?.Message ?? string.Empty,
            expectedMessage);
        Assert.AreEqual(expectedCalls, transport.CallCount);
        Assert.AreEqual(1, exception.WriteExchanges.Count);
    }

    private static void AssertWriteEcho(
        RazerExchangeTrace exchange,
        byte[] expectedArguments)
    {
        CollectionAssert.AreEqual(
            expectedArguments,
            exchange.RequestPacket.Span[8..12].ToArray());
        CollectionAssert.AreEqual(
            expectedArguments,
            exchange.ResponsePacket.Span[8..12].ToArray());
    }

    private static bool IsWriteRequest(byte[] report)
    {
        return RazerPacketCodec.DecodeHidFeatureReport(report).CombinedCommand == 0x0D02;
    }

    private static int CountWriteRequests(ScriptedRazerTransport transport)
    {
        return transport.Requests.Count(IsWriteRequest);
    }
}
