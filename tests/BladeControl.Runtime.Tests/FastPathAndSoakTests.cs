using BladeControl.Runtime;

namespace BladeControl.Runtime.Tests;

[TestClass]
public sealed class FastPathAndSoakTests
{
    [TestMethod]
    public void OneHundredFastSamplesReadCpuAndGpuWithoutRazerRequests()
    {
        var clock = new VirtualRuntimeClock();
        var telemetry = new FakeRuntimeTelemetry(clock);
        var hardware = new FakeRuntimeHardware();

        for (int index = 0; index < 100; index++)
        {
            _ = telemetry.GetControlSample();
        }

        Assert.AreEqual(100, telemetry.CpuReads);
        Assert.AreEqual(100, telemetry.GpuReads);
        Assert.AreEqual(0, hardware.ModeReads);
        Assert.AreEqual(0, telemetry.DiagnosticReads);
    }

    [TestMethod]
    public async Task RuntimeFastPathUsesOnlyLowFrequencyModeWatchdog()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();
        int controlBefore = rig.Telemetry.ControlReads;
        int diagnosticBefore = rig.Telemetry.DiagnosticReads;

        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 100);

        Assert.AreEqual(100, rig.Telemetry.ControlReads - controlBefore);
        Assert.AreEqual(diagnosticBefore, rig.Telemetry.DiagnosticReads);
        Assert.AreEqual(10, rig.Hardware.ModeReads);
    }

    [TestMethod]
    public async Task ControlTelemetryAcquisitionDurationIsMeasuredWithoutBecomingAFailure()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        rig.Telemetry.WorkDuration = TimeSpan.FromMilliseconds(100);
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();

        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 3);

        Assert.AreEqual(
            100,
            runtime.GetStatus().LastTelemetryAcquisitionDuration.TotalMilliseconds);
        Assert.AreEqual(RuntimeState.Running, runtime.State);
    }

    [TestMethod]
    public async Task SlowControlSampleIsDiagnosticAndDoesNotAloneTriggerEmergency()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        rig.Telemetry.WorkDuration = TimeSpan.FromMilliseconds(300);
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();

        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 3);

        Assert.AreEqual(
            300,
            runtime.GetStatus().LastTelemetryAcquisitionDuration.TotalMilliseconds);
        Assert.AreEqual(RuntimeState.Running, runtime.State);
        Assert.AreEqual(0, rig.Hardware.AutoAttempts);
    }

    [TestMethod]
    public async Task DiagnosticSnapshotRemainsExplicitAndSeparate()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();

        _ = runtime.GetDiagnosticSnapshot();

        Assert.AreEqual(1, rig.Telemetry.DiagnosticReads);
    }

    [TestMethod]
    public async Task SixExchangesProduceSixGlobalTraceRecords()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime(eventCapacity: 64);
        Assert.IsTrue(runtime.InitializeHost());
        long before = runtime.GetStatus().RecentEvents.OfType<ProtocolExchangeEvent>().LongCount();

        rig.Hardware.EmitExchanges(6);

        long after = runtime.GetStatus().RecentEvents.OfType<ProtocolExchangeEvent>().LongCount();
        Assert.AreEqual(6, after - before);
    }

    [TestMethod]
    public async Task TwentyFourHourVirtualSoakDoesNotDriftOrGrowUnbounded()
    {
        const long cycles = 172_800;
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime(eventCapacity: 256);
        runtime.StartThermalControl();
        int initialFanWrites = rig.Hardware.FanWrites;

        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: cycles);

        RuntimeStatus status = runtime.GetStatus();
        Assert.AreEqual(cycles, status.Scheduler.CompletedCycles);
        Assert.AreEqual(
            TimeSpan.FromMilliseconds((cycles - 1) * 500),
            rig.Clock.MonotonicNow);
        Assert.AreEqual(0, status.Scheduler.OverrunCount);
        Assert.IsTrue(status.RecentEvents.Count <= 256);
        Assert.IsTrue(status.RetainedThermalDecisionCount <= 4096);
        Assert.IsTrue(status.RetainedThermalTraceCount <= 4096);
        Assert.AreEqual(256, new BoundedRuntimeEventLog(256).Capacity);
        Assert.IsTrue(rig.Hardware.FanWrites - initialFanWrites < cycles / 10);
        Assert.AreEqual(RuntimeState.Running, runtime.State);
        Console.WriteLine(
            $"cycles={status.Scheduler.CompletedCycles}; " +
            $"virtualElapsed={rig.Clock.MonotonicNow}; " +
            $"overruns={status.Scheduler.OverrunCount}; " +
            $"watchdogReads={rig.Hardware.ModeReads}; " +
            $"fanWrites={rig.Hardware.FanWrites - initialFanWrites}; " +
            $"events={status.RecentEvents.Count}; " +
            $"decisions={status.RetainedThermalDecisionCount}; " +
            $"thermalTrace={status.RetainedThermalTraceCount}");
    }

    [TestMethod]
    public async Task EmergencyTerminatesSoakAndPreventsLaterSet()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();
        rig.Telemetry.BeforeRead = sample =>
        {
            if (sample >= 10)
            {
                rig.Telemetry.FixedGpuTemperature = 80;
            }
        };

        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 1000);
        int writesAfterEmergency = rig.Hardware.FanWrites;

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 10));

        Assert.AreEqual(writesAfterEmergency, rig.Hardware.FanWrites);
        Assert.AreEqual(1, rig.Hardware.AutoAttempts);
        Assert.IsTrue(runtime.GetStatus().RecentEvents.OfType<EmergencyHandoffEvent>().Any());
    }
}
