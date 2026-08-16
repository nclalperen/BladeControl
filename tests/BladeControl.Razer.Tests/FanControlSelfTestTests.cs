namespace BladeControl.Razer.Tests;

[TestClass]
public sealed class FanControlSelfTestTests
{
    [TestMethod]
    public void CompleteSelfTestSucceedsAndRestoresInitialState()
    {
        using var transport = CreateInitialTransport();
        var delay = new RecordingFanObservationDelay();
        var client = CreateClient(transport, delay);

        FanControlSelfTestResult result = client.RunFanControlSelfTest();

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(5, result.Stages.Count);
        Assert.AreEqual(RazerPerformanceMode.Custom, transport.Zone1Mode);
        Assert.AreEqual(RazerPerformanceMode.Custom, transport.Zone2Mode);
        Assert.AreEqual(RazerFanMode.Auto, transport.Zone1FanMode);
        Assert.AreEqual(RazerFanMode.Auto, transport.Zone2FanMode);
        Assert.AreEqual(RazerCpuPerformanceLevel.Medium, transport.CpuLevel);
        Assert.AreEqual(RazerGpuPerformanceLevel.Low, transport.GpuLevel);
        Assert.AreEqual(10, transport.WriteCount);
    }

    [TestMethod]
    public void InitialStateMismatchAbortsBeforeEverySet()
    {
        using var transport = CreateInitialTransport();
        transport.CpuLevel = RazerCpuPerformanceLevel.Low;
        var client = new RazerClient(transport);

        Assert.ThrowsException<FanControlSelfTestPreconditionException>(
            () => client.RunFanControlSelfTest());
        Assert.AreEqual(0, transport.WriteCount);
    }

    [TestMethod]
    public void FailureEnteringManualRecoversAutoThenInitialState()
    {
        using var transport = CreateInitialTransport();
        transport.FailWriteNumbers.Add(1);
        var client = new RazerClient(transport);

        FanControlSelfTestResult result = client.RunFanControlSelfTest();

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("SELFTEST FAILED - INITIAL STATE RESTORED", result.Message);
        Assert.IsTrue(result.AutoRecovery!.Succeeded);
        Assert.AreEqual(RazerPerformanceMode.Custom, transport.Zone1Mode);
        Assert.AreEqual(RazerFanMode.Auto, transport.Zone1FanMode);
        Assert.AreEqual(5, transport.WriteCount);
    }

    [TestMethod]
    public void FirstFanSetFailureUsesSuccessfulEmergencyRecovery()
    {
        using var transport = CreateInitialTransport();
        transport.FailWriteNumbers.Add(3);
        var client = new RazerClient(transport);

        FanControlSelfTestResult result = client.RunFanControlSelfTest();

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("SELFTEST FAILED - INITIAL STATE RESTORED", result.Message);
        Assert.IsTrue(result.AutoRecovery!.Succeeded);
        Assert.AreEqual(7, transport.WriteCount);
    }

    [TestMethod]
    public void AsymmetricStageFailureStopsLaterStagesAndRestoresInitial()
    {
        using var transport = CreateInitialTransport();
        transport.FailWriteNumbers.Add(6);
        var client = new RazerClient(transport);

        FanControlSelfTestResult result = client.RunFanControlSelfTest();

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(3, result.Stages.Count);
        Assert.AreEqual("SELFTEST FAILED - INITIAL STATE RESTORED", result.Message);
        Assert.AreEqual(10, transport.WriteCount);
    }

    [TestMethod]
    public void SelfTestSettlingTimeoutDoesNotRepeatSetAndRestoresInitial()
    {
        using var transport = CreateInitialTransport();
        transport.IgnoreWriteNumbers.UnionWith([3, 4]);
        var delay = new RecordingFanObservationDelay();
        var client = CreateClient(transport, delay);

        FanControlSelfTestResult result = client.RunFanControlSelfTest();

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("SELFTEST FAILED - INITIAL STATE RESTORED", result.Message);
        Assert.AreEqual(10, delay.Waits.Count);
        Assert.AreEqual(2, transport.WriteRequests.Count(packet =>
            packet.CommandId == 0x01));
    }

    [TestMethod]
    public void FailedEmergencyAutoStopsBeforePerformanceRestoration()
    {
        using var transport = CreateInitialTransport();
        transport.FailWriteNumbers.UnionWith([3, 4]);
        var client = new RazerClient(transport);

        FanControlSelfTestResult result = client.RunFanControlSelfTest();

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("FAN AUTO RESTORATION FAILED", result.Message);
        Assert.IsFalse(result.AutoRecovery!.Succeeded);
        Assert.IsNull(result.PerformanceRestoration);
        Assert.AreEqual(4, transport.WriteCount);
    }

    [TestMethod]
    public void PerformanceRestorationFailureIsReportedWithoutFurtherWrites()
    {
        using var transport = CreateInitialTransport();
        transport.FailWriteNumbers.UnionWith([9, 10]);
        var client = new RazerClient(transport);

        FanControlSelfTestResult result = client.RunFanControlSelfTest();

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("PERFORMANCE RESTORATION FAILED", result.Message);
        Assert.IsNotNull(result.PerformanceRestoration);
        Assert.AreEqual(10, transport.WriteCount);
        Assert.AreEqual(RazerPerformanceMode.Balanced, transport.Zone1Mode);
        Assert.AreEqual(RazerFanMode.Auto, transport.Zone1FanMode);
    }

    private static StatefulPerformanceTransport CreateInitialTransport() => new(
        RazerPerformanceMode.Custom,
        RazerCpuPerformanceLevel.Medium,
        RazerGpuPerformanceLevel.Low,
        2000,
        2000);

    private static RazerClient CreateClient(
        StatefulPerformanceTransport transport,
        RecordingFanObservationDelay delay) => new(
            transport,
            new SequentialTransactionIdSource(),
            delay);
}
