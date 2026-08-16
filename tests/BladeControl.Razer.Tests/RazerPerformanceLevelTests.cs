using BladeControl.Razer.Protocol;

namespace BladeControl.Razer.Tests;

[TestClass]
public sealed class RazerPerformanceLevelTests
{
    [DataTestMethod]
    [DataRow((byte)0x00, "Low")]
    [DataRow((byte)0x01, "Medium")]
    [DataRow((byte)0x02, "High")]
    [DataRow((byte)0x03, "Boost")]
    [DataRow((byte)0x04, "Overclock")]
    public void CpuPerformanceLevelsAreParsed(byte value, string expected)
    {
        RazerStatusSnapshot status = GetStatusWithLevels(value, gpuValue: 0x02);

        Assert.AreEqual(expected, status.CpuPerformanceLevel.ToString());
    }

    [DataTestMethod]
    [DataRow((byte)0x00, "Low")]
    [DataRow((byte)0x01, "Medium")]
    [DataRow((byte)0x02, "High")]
    public void GpuPerformanceLevelsAreParsed(byte value, string expected)
    {
        RazerStatusSnapshot status = GetStatusWithLevels(cpuValue: 0x03, gpuValue: value);

        Assert.AreEqual(expected, status.GpuPerformanceLevel.ToString());
    }

    [TestMethod]
    public void UnknownCpuPerformanceLevelIsPreservedForDisplay()
    {
        RazerStatusSnapshot status = GetStatusWithLevels(0xA7, gpuValue: 0x02);

        Assert.AreEqual("Unknown(0xA7)", status.CpuPerformanceLevel.ToString());
    }

    [TestMethod]
    public void UnknownGpuPerformanceLevelIsPreservedForDisplay()
    {
        RazerStatusSnapshot status = GetStatusWithLevels(cpuValue: 0x03, gpuValue: 0xFE);

        Assert.AreEqual("Unknown(0xFE)", status.GpuPerformanceLevel.ToString());
    }

    [TestMethod]
    public void CpuClusterMismatchIsRejectedWithoutGpuQueryOrRetry()
    {
        using var transport = new ScriptedRazerTransport((_, request) =>
        {
            RazerPacket response = ScriptedRazerTransport.CreateSuccessfulResponse(request);
            if (request.CommandId == RazerCommands.GetPerformanceBoostLevelCommandId &&
                request.Arguments[1] == (byte)RazerPerformanceCluster.Cpu)
            {
                byte[] arguments = response.Arguments.ToArray();
                arguments[1] = (byte)RazerPerformanceCluster.Gpu;
                return ScriptedRazerTransport.CreateResponse(request, arguments: arguments);
            }

            return response;
        });
        var client = CreateStatusClient(transport);

        RazerProtocolException exception = Assert.ThrowsException<RazerProtocolException>(
            () => client.GetStatus());

        StringAssert.Contains(exception.Message, "returned cluster");
        StringAssert.Contains(exception.Message, "expected 0x01");
        StringAssert.Contains(exception.Message, "received 0x02");
        Assert.AreEqual(5, transport.CallCount);
    }

    [TestMethod]
    public void ShortCpuPerformanceResponseIsRejectedAsMalformed()
    {
        using var transport = new ScriptedRazerTransport((_, request) =>
        {
            if (request.CommandId == RazerCommands.GetPerformanceBoostLevelCommandId &&
                request.Arguments[1] == (byte)RazerPerformanceCluster.Cpu)
            {
                return ScriptedRazerTransport.CreateResponse(request, dataSize: 2);
            }

            return ScriptedRazerTransport.CreateSuccessfulResponse(request);
        });
        var client = CreateStatusClient(transport);

        RazerProtocolException exception = Assert.ThrowsException<RazerProtocolException>(
            () => client.GetStatus());

        StringAssert.Contains(exception.Message, "response data size");
        Assert.AreEqual(5, transport.CallCount);
    }

    private static RazerStatusSnapshot GetStatusWithLevels(byte cpuValue, byte gpuValue)
    {
        using var transport = new ScriptedRazerTransport((_, request) =>
        {
            RazerPacket response = ScriptedRazerTransport.CreateSuccessfulResponse(request);
            if (request.CommandId != RazerCommands.GetPerformanceBoostLevelCommandId)
            {
                return response;
            }

            byte[] arguments = response.Arguments.ToArray();
            arguments[2] = request.Arguments[1] == (byte)RazerPerformanceCluster.Cpu
                ? cpuValue
                : gpuValue;
            return ScriptedRazerTransport.CreateResponse(request, arguments: arguments);
        });
        RazerClient client = CreateStatusClient(transport);

        return client.GetStatus();
    }

    private static RazerClient CreateStatusClient(ScriptedRazerTransport transport)
    {
        return new RazerClient(
            transport,
            new SequenceTransactionIdSource(1, 2, 3, 4, 5, 6));
    }
}
