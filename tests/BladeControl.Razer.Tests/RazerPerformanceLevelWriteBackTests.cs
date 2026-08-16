using BladeControl.Razer.Protocol;

namespace BladeControl.Razer.Tests;

[TestClass]
public sealed class RazerPerformanceLevelWriteBackTests
{
    [TestMethod]
    public void SuccessfulWriteBackUsesExactOrderAndAcceptsBothEchoes()
    {
        using var transport = CreateExpectedStateTransport();
        RazerClient client = CreateClient(transport);

        RazerPerformanceLevelWriteBackResult result =
            client.RunPerformanceLevelWriteBackTest();

        Assert.IsTrue(result.Passed);
        Assert.IsFalse(result.StateDriftDetected);
        Assert.AreEqual(14, transport.CallCount);
        Assert.AreEqual(14, result.Exchanges.Count);
        CollectionAssert.AreEqual(
            new ushort[]
            {
                0x0D81, 0x0D81, 0x0D82, 0x0D82, 0x0D87, 0x0D87,
                0x0D07, 0x0D07,
                0x0D81, 0x0D81, 0x0D82, 0x0D82, 0x0D87, 0x0D87
            },
            transport.Requests
                .Select(report => RazerPacketCodec.DecodeHidFeatureReport(report))
                .Select(packet => packet.CombinedCommand)
                .ToArray());
        AssertWriteEcho(
            result.CpuWriteExchange,
            new byte[] { 0x00, 0x01, 0x01 });
        AssertWriteEcho(
            result.GpuWriteExchange,
            new byte[] { 0x00, 0x02, 0x00 });
        Assert.AreEqual(2, CountWriteRequests(transport));
    }

    [TestMethod]
    public void WrongCpuClusterStopsBeforeGpuWrite()
    {
        AssertWriteValidationFailure(
            (call, request) => MutateWriteResponse(
                call,
                request,
                RazerPerformanceCluster.Cpu,
                arguments => arguments[1] = 0x02),
            "returned cluster",
            expectedCalls: 7,
            expectedWriteExchanges: 1);
    }

    [TestMethod]
    public void WrongGpuClusterIsRejectedWithoutRetry()
    {
        AssertWriteValidationFailure(
            (call, request) => MutateWriteResponse(
                call,
                request,
                RazerPerformanceCluster.Gpu,
                arguments => arguments[1] = 0x01),
            "returned cluster",
            expectedCalls: 8,
            expectedWriteExchanges: 2);
    }

    [TestMethod]
    public void WrongCpuLevelStopsBeforeGpuWrite()
    {
        AssertWriteValidationFailure(
            (call, request) => MutateWriteResponse(
                call,
                request,
                RazerPerformanceCluster.Cpu,
                arguments => arguments[2] = 0x02),
            "response argument echo",
            expectedCalls: 7,
            expectedWriteExchanges: 1);
    }

    [TestMethod]
    public void WrongGpuLevelIsRejectedWithoutRetry()
    {
        AssertWriteValidationFailure(
            (call, request) => MutateWriteResponse(
                call,
                request,
                RazerPerformanceCluster.Gpu,
                arguments => arguments[2] = 0x01),
            "response argument echo",
            expectedCalls: 8,
            expectedWriteExchanges: 2);
    }

    [TestMethod]
    public void BadCpuResponseChecksumStopsBeforeGpuWrite()
    {
        using var transport = new ScriptedRazerTransport(CreateExpectedStateResponse)
        {
            CorruptResponseChecksumOnCall = 7
        };
        RazerClient client = CreateClient(transport);

        RazerPerformanceLevelWriteBackValidationException exception =
            Assert.ThrowsException<RazerPerformanceLevelWriteBackValidationException>(
                () => client.RunPerformanceLevelWriteBackTest());

        StringAssert.Contains(
            exception.InnerException?.Message ?? string.Empty,
            "checksum mismatch");
        Assert.AreEqual(7, transport.CallCount);
        Assert.AreEqual(1, CountWriteRequests(transport));
    }

    [TestMethod]
    public void WrongCpuTransactionStopsBeforeGpuWrite()
    {
        AssertWriteValidationFailure(
            (call, request) => IsPerformanceLevelWrite(
                request,
                RazerPerformanceCluster.Cpu)
                ? ScriptedRazerTransport.CreateResponse(
                    request,
                    transactionId: unchecked((byte)(request.TransactionId + 1)))
                : CreateExpectedStateResponse(call, request),
            "transaction ID",
            expectedCalls: 7,
            expectedWriteExchanges: 1);
    }

    [TestMethod]
    public void WrongCpuResponseClassStopsBeforeGpuWrite()
    {
        AssertWriteValidationFailure(
            (call, request) => IsPerformanceLevelWrite(
                request,
                RazerPerformanceCluster.Cpu)
                ? ScriptedRazerTransport.CreateResponse(request, commandClass: 0x0E)
                : CreateExpectedStateResponse(call, request),
            "command class",
            expectedCalls: 7,
            expectedWriteExchanges: 1);
    }

    [TestMethod]
    public void WrongCpuResponseCommandStopsBeforeGpuWrite()
    {
        AssertWriteValidationFailure(
            (call, request) => IsPerformanceLevelWrite(
                request,
                RazerPerformanceCluster.Cpu)
                ? ScriptedRazerTransport.CreateResponse(request, commandId: 0x02)
                : CreateExpectedStateResponse(call, request),
            "command ID",
            expectedCalls: 7,
            expectedWriteExchanges: 1);
    }

    [TestMethod]
    public void ShortCpuResponseStopsBeforeGpuWrite()
    {
        AssertWriteValidationFailure(
            (call, request) => IsPerformanceLevelWrite(
                request,
                RazerPerformanceCluster.Cpu)
                ? ScriptedRazerTransport.CreateResponse(request, dataSize: 2)
                : CreateExpectedStateResponse(call, request),
            "response data size",
            expectedCalls: 7,
            expectedWriteExchanges: 1);
    }

    [TestMethod]
    public void NonCustomModeCompletesPreReadAndSendsNoWrite()
    {
        AssertPreconditionFailure(
            (call, request) => MutateReadResponse(
                call,
                request,
                RazerCommands.GetPerformanceAndFanModeCommandId,
                arguments => arguments[2] = 0x00),
            "Custom performance mode");
    }

    [TestMethod]
    public void ManualFanModeCompletesPreReadAndSendsNoWrite()
    {
        AssertPreconditionFailure(
            (call, request) => MutateReadResponse(
                call,
                request,
                RazerCommands.GetPerformanceAndFanModeCommandId,
                arguments => arguments[3] = 0x01),
            "Auto fan mode");
    }

    [TestMethod]
    public void ZoneDisagreementCompletesPreReadAndSendsNoWrite()
    {
        AssertPreconditionFailure(
            (call, request) =>
            {
                RazerPacket response = CreateExpectedStateResponse(call, request);
                if (request.CommandId == RazerCommands.GetPerformanceAndFanModeCommandId &&
                    request.Arguments[1] == (byte)RazerZone.Zone2)
                {
                    byte[] arguments = response.Arguments.ToArray();
                    arguments[2] = 0x00;
                    return ScriptedRazerTransport.CreateResponse(request, arguments: arguments);
                }

                return response;
            },
            "disagrees between zones");
    }

    [TestMethod]
    public void UnexpectedCpuLevelCompletesPreReadAndSendsNoWrite()
    {
        AssertPreconditionFailure(
            (call, request) => MutateClusterReadResponse(
                call,
                request,
                RazerPerformanceCluster.Cpu,
                level: 0x02),
            "CPU must report Medium");
    }

    [TestMethod]
    public void UnexpectedGpuLevelCompletesPreReadAndSendsNoWrite()
    {
        AssertPreconditionFailure(
            (call, request) => MutateClusterReadResponse(
                call,
                request,
                RazerPerformanceCluster.Gpu,
                level: 0x01),
            "GPU must report Low");
    }

    [TestMethod]
    public void FailedSixthPreReadSendsNoWrite()
    {
        using var transport = new ScriptedRazerTransport((call, request) =>
            call == 6
                ? ScriptedRazerTransport.CreateResponse(
                    request,
                    transactionId: unchecked((byte)(request.TransactionId + 1)))
                : CreateExpectedStateResponse(call, request));
        RazerClient client = CreateClient(transport);

        Assert.ThrowsException<RazerProtocolException>(
            () => client.RunPerformanceLevelWriteBackTest());

        Assert.AreEqual(6, transport.CallCount);
        Assert.AreEqual(0, CountWriteRequests(transport));
    }

    [TestMethod]
    public void IdenticalPostStatePasses()
    {
        using var transport = CreateExpectedStateTransport();
        RazerClient client = CreateClient(transport);

        RazerPerformanceLevelWriteBackResult result =
            client.RunPerformanceLevelWriteBackTest();

        Assert.IsTrue(result.Passed);
        Assert.IsFalse(result.StateDriftDetected);
        Assert.IsTrue(result.PerformanceUnchanged);
        Assert.IsTrue(result.FanModeUnchanged);
        Assert.IsTrue(result.CpuPerformanceLevelUnchanged);
        Assert.IsTrue(result.GpuPerformanceLevelUnchanged);
    }

    [TestMethod]
    public void CpuDriftIsDetectedWithoutRollback()
    {
        using var transport = CreateExpectedStateTransport(postCpuLevel: 0x02);
        RazerClient client = CreateClient(transport);

        RazerPerformanceLevelWriteBackResult result =
            client.RunPerformanceLevelWriteBackTest();

        Assert.IsTrue(result.StateDriftDetected);
        Assert.IsFalse(result.CpuPerformanceLevelUnchanged);
        Assert.IsTrue(result.GpuPerformanceLevelUnchanged);
        Assert.AreEqual(2, CountWriteRequests(transport));
    }

    [TestMethod]
    public void GpuDriftIsDetectedWithoutRollback()
    {
        using var transport = CreateExpectedStateTransport(postGpuLevel: 0x01);
        RazerClient client = CreateClient(transport);

        RazerPerformanceLevelWriteBackResult result =
            client.RunPerformanceLevelWriteBackTest();

        Assert.IsTrue(result.StateDriftDetected);
        Assert.IsTrue(result.CpuPerformanceLevelUnchanged);
        Assert.IsFalse(result.GpuPerformanceLevelUnchanged);
        Assert.AreEqual(2, CountWriteRequests(transport));
    }

    [TestMethod]
    public void PerformanceDriftIsDetected()
    {
        using var transport = CreateExpectedStateTransport(postPerformanceMode: 0x00);
        RazerClient client = CreateClient(transport);

        RazerPerformanceLevelWriteBackResult result =
            client.RunPerformanceLevelWriteBackTest();

        Assert.IsTrue(result.StateDriftDetected);
        Assert.IsFalse(result.PerformanceUnchanged);
    }

    [TestMethod]
    public void FanModeDriftIsDetected()
    {
        using var transport = CreateExpectedStateTransport(postFanMode: 0x01);
        RazerClient client = CreateClient(transport);

        RazerPerformanceLevelWriteBackResult result =
            client.RunPerformanceLevelWriteBackTest();

        Assert.IsTrue(result.StateDriftDetected);
        Assert.IsFalse(result.FanModeUnchanged);
    }

    [TestMethod]
    public void PostReadZoneDisagreementIsDetected()
    {
        using var transport = CreateExpectedStateTransport(
            postZone2PerformanceMode: 0x00);
        RazerClient client = CreateClient(transport);

        RazerPerformanceLevelWriteBackResult result =
            client.RunPerformanceLevelWriteBackTest();

        Assert.AreNotEqual(
            result.PostWriteState.Zone1Mode.PerformanceMode,
            result.PostWriteState.Zone2Mode.PerformanceMode);
        Assert.IsTrue(result.StateDriftDetected);
        Assert.IsFalse(result.PerformanceUnchanged);
    }

    [TestMethod]
    public void FanRpmChangeIsAllowed()
    {
        using var transport = CreateExpectedStateTransport(changePostFanRpm: true);
        RazerClient client = CreateClient(transport);

        RazerPerformanceLevelWriteBackResult result =
            client.RunPerformanceLevelWriteBackTest();

        Assert.AreNotEqual(
            result.PreWriteState.Fan1.RevolutionsPerMinute,
            result.PostWriteState.Fan1.RevolutionsPerMinute);
        Assert.AreNotEqual(
            result.PreWriteState.Fan2.RevolutionsPerMinute,
            result.PostWriteState.Fan2.RevolutionsPerMinute);
        Assert.IsTrue(result.Passed);
        Assert.IsFalse(result.StateDriftDetected);
    }

    private static ScriptedRazerTransport CreateExpectedStateTransport(
        byte postCpuLevel = 0x01,
        byte postGpuLevel = 0x00,
        byte postPerformanceMode = 0x04,
        byte postFanMode = 0x00,
        byte? postZone2PerformanceMode = null,
        bool changePostFanRpm = false)
    {
        return new ScriptedRazerTransport(
            (call, request) => CreateExpectedStateResponse(
                call,
                request,
                postCpuLevel,
                postGpuLevel,
                postPerformanceMode,
                postFanMode,
                postZone2PerformanceMode,
                changePostFanRpm));
    }

    private static RazerPacket CreateExpectedStateResponse(
        int call,
        RazerPacket request)
    {
        return CreateExpectedStateResponse(
            call,
            request,
            postCpuLevel: 0x01,
            postGpuLevel: 0x00,
            postPerformanceMode: 0x04,
            postFanMode: 0x00,
            postZone2PerformanceMode: null,
            changePostFanRpm: false);
    }

    private static RazerPacket CreateExpectedStateResponse(
        int call,
        RazerPacket request,
        byte postCpuLevel,
        byte postGpuLevel,
        byte postPerformanceMode,
        byte postFanMode,
        byte? postZone2PerformanceMode,
        bool changePostFanRpm)
    {
        RazerPacket response = ScriptedRazerTransport.CreateSuccessfulResponse(request);
        byte[] arguments = response.Arguments.ToArray();
        bool isPostRead = call >= 9;

        if (request.CommandId == RazerCommands.GetFanRpmCommandId)
        {
            arguments[2] = changePostFanRpm && isPostRead
                ? (byte)0x1E
                : (byte)0x14;
        }
        else if (request.CommandId == RazerCommands.GetPerformanceAndFanModeCommandId)
        {
            arguments[2] = isPostRead
                ? request.Arguments[1] == (byte)RazerZone.Zone2 &&
                    postZone2PerformanceMode.HasValue
                    ? postZone2PerformanceMode.Value
                    : postPerformanceMode
                : (byte)0x04;
            arguments[3] = isPostRead ? postFanMode : (byte)0x00;
        }
        else if (request.CommandId == RazerCommands.GetPerformanceBoostLevelCommandId)
        {
            arguments[2] = request.Arguments[1] == (byte)RazerPerformanceCluster.Cpu
                ? isPostRead ? postCpuLevel : (byte)0x01
                : isPostRead ? postGpuLevel : (byte)0x00;
        }

        return ScriptedRazerTransport.CreateResponse(request, arguments: arguments);
    }

    private static RazerPacket MutateWriteResponse(
        int call,
        RazerPacket request,
        RazerPerformanceCluster targetCluster,
        Action<byte[]> mutation)
    {
        RazerPacket response = CreateExpectedStateResponse(call, request);
        if (!IsPerformanceLevelWrite(request, targetCluster))
        {
            return response;
        }

        byte[] arguments = response.Arguments.ToArray();
        mutation(arguments);
        return ScriptedRazerTransport.CreateResponse(request, arguments: arguments);
    }

    private static RazerPacket MutateReadResponse(
        int call,
        RazerPacket request,
        byte targetCommandId,
        Action<byte[]> mutation)
    {
        RazerPacket response = CreateExpectedStateResponse(call, request);
        if (request.CommandId != targetCommandId)
        {
            return response;
        }

        byte[] arguments = response.Arguments.ToArray();
        mutation(arguments);
        return ScriptedRazerTransport.CreateResponse(request, arguments: arguments);
    }

    private static RazerPacket MutateClusterReadResponse(
        int call,
        RazerPacket request,
        RazerPerformanceCluster targetCluster,
        byte level)
    {
        RazerPacket response = CreateExpectedStateResponse(call, request);
        if (request.CommandId != RazerCommands.GetPerformanceBoostLevelCommandId ||
            request.Arguments[1] != (byte)targetCluster)
        {
            return response;
        }

        byte[] arguments = response.Arguments.ToArray();
        arguments[2] = level;
        return ScriptedRazerTransport.CreateResponse(request, arguments: arguments);
    }

    private static bool IsPerformanceLevelWrite(
        RazerPacket request,
        RazerPerformanceCluster cluster)
    {
        return request.CommandId == RazerCommands.WriteBackPerformanceLevelCommandId &&
            request.Arguments[1] == (byte)cluster;
    }

    private static bool IsWriteRequest(byte[] report)
    {
        return RazerPacketCodec.DecodeHidFeatureReport(report).CombinedCommand == 0x0D07;
    }

    private static int CountWriteRequests(ScriptedRazerTransport transport)
    {
        return transport.Requests.Count(IsWriteRequest);
    }

    private static RazerClient CreateClient(ScriptedRazerTransport transport)
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
        int expectedCalls,
        int expectedWriteExchanges)
    {
        using var transport = new ScriptedRazerTransport(responseFactory);
        RazerClient client = CreateClient(transport);

        RazerPerformanceLevelWriteBackValidationException exception =
            Assert.ThrowsException<RazerPerformanceLevelWriteBackValidationException>(
                () => client.RunPerformanceLevelWriteBackTest());

        StringAssert.Contains(
            exception.InnerException?.Message ?? string.Empty,
            expectedMessage);
        Assert.AreEqual(expectedCalls, transport.CallCount);
        Assert.AreEqual(expectedWriteExchanges, exception.WriteExchanges.Count);
        Assert.AreEqual(expectedWriteExchanges, CountWriteRequests(transport));
    }

    private static void AssertPreconditionFailure(
        Func<int, RazerPacket, RazerPacket> responseFactory,
        string expectedMessage)
    {
        using var transport = new ScriptedRazerTransport(responseFactory);
        RazerClient client = CreateClient(transport);

        RazerPerformanceLevelWriteBackPreconditionException exception =
            Assert.ThrowsException<RazerPerformanceLevelWriteBackPreconditionException>(
                () => client.RunPerformanceLevelWriteBackTest());

        StringAssert.Contains(exception.Message, expectedMessage);
        Assert.AreEqual(6, transport.CallCount);
        Assert.AreEqual(6, exception.PreWriteState.Exchanges.Count);
        Assert.AreEqual(0, CountWriteRequests(transport));
    }

    private static void AssertWriteEcho(
        RazerExchangeTrace exchange,
        byte[] expectedArguments)
    {
        CollectionAssert.AreEqual(
            expectedArguments,
            exchange.RequestPacket.Span[8..11].ToArray());
        CollectionAssert.AreEqual(
            expectedArguments,
            exchange.ResponsePacket.Span[8..11].ToArray());
    }
}
