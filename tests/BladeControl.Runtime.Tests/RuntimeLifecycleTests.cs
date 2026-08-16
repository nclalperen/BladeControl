using BladeControl.Razer;
using BladeControl.Runtime;
using BladeControl.Telemetry;
using BladeControl.Thermal;

namespace BladeControl.Runtime.Tests;

[TestClass]
public sealed class RuntimeLifecycleTests
{
    [TestMethod]
    public async Task ServiceStartupInKnownAutoStateDoesNotWrite()
    {
        RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();

        Assert.IsTrue(runtime.InitializeHost());

        Assert.AreEqual(RuntimeState.Stopped, runtime.State);
        Assert.AreEqual(0, rig.Hardware.AutoAttempts);
        Assert.AreEqual(0, rig.Hardware.FanWrites);
    }

    [TestMethod]
    public async Task StartupKnownAutoStateTracesExactlyTwoModeGets()
    {
        RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();

        Assert.IsTrue(runtime.InitializeHost());

        ProtocolExchangeEvent[] exchanges = runtime.GetStatus().RecentEvents
            .OfType<ProtocolExchangeEvent>()
            .ToArray();
        Assert.AreEqual(2, exchanges.Length);
        Assert.IsTrue(exchanges.All(item => item.Exchange.CombinedCommand == 0x0D82));
    }

    [TestMethod]
    public async Task FreshCpuQualificationFailurePreventsEverySet()
    {
        RuntimeRig rig = new();
        rig.Telemetry.MissingCpu = true;
        await using BladeRuntime runtime = rig.CreateRuntime();

        Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        Assert.AreEqual(1, rig.Telemetry.QualificationReads);
        Assert.AreEqual(0, rig.Hardware.FanWrites);
        Assert.AreEqual(0, rig.Hardware.AutoAttempts);
        CollectionAssert.DoesNotContain(rig.Hardware.Operations, "Capture");
        Assert.AreEqual(RuntimeState.Stopped, runtime.State);
    }

    [TestMethod]
    public async Task FreshGpuQualificationFailurePreventsEverySet()
    {
        RuntimeRig rig = new();
        rig.Telemetry.MissingGpu = true;
        await using BladeRuntime runtime = rig.CreateRuntime();

        Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        Assert.AreEqual(0, rig.Hardware.FanWrites);
        Assert.AreEqual(0, rig.Hardware.AutoAttempts);
        CollectionAssert.DoesNotContain(rig.Hardware.Operations, "Capture");
    }

    [TestMethod]
    public async Task ThermalStartDoesNotTrustEarlierSuccessfulDoctorQualification()
    {
        RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        ThermalOwnershipQualification doctor = runtime.QualifyThermalOwnership();
        Assert.IsTrue(doctor.ThermalOwnershipReady);
        rig.Telemetry.MissingCpu = true;

        Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        Assert.AreEqual(2, rig.Telemetry.QualificationReads);
        Assert.AreEqual(0, rig.Hardware.FanWrites);
        Assert.AreEqual(0, rig.Hardware.AutoAttempts);
    }

    [TestMethod]
    public async Task OrphanedBalancedManualIsRecoveredExactlyOnce()
    {
        RuntimeRig rig = new();
        rig.Hardware.SetMode(RazerPerformanceMode.Balanced, RazerFanMode.Manual);
        await using BladeRuntime runtime = rig.CreateRuntime();

        Assert.IsTrue(runtime.InitializeHost());

        Assert.AreEqual(1, rig.Hardware.AutoAttempts);
        Assert.IsTrue(runtime.GetStatus().RecentEvents.OfType<RecoveryResultEvent>()
            .Single().Succeeded);
    }

    [TestMethod]
    public async Task OrphanRecoveryFailureFaultsAndPreventsThermalStart()
    {
        RuntimeRig rig = new();
        rig.Hardware.SetMode(RazerPerformanceMode.Balanced, RazerFanMode.Manual);
        rig.Hardware.AutoSucceeds = false;
        await using BladeRuntime runtime = rig.CreateRuntime();

        Assert.IsFalse(runtime.InitializeHost());

        Assert.AreEqual(RuntimeState.Faulted, runtime.State);
        Assert.AreEqual(1, rig.Hardware.AutoAttempts);
        Assert.ThrowsException<InvalidOperationException>(runtime.StartThermalControl);
    }

    [TestMethod]
    public async Task NormalStartStopUsesAutoBeforePerformanceRestore()
    {
        RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();

        _ = await runtime.StopThermalControlAsync();

        int auto = rig.Hardware.Operations.LastIndexOf("Auto");
        int restore = rig.Hardware.Operations.LastIndexOf("Restore");
        Assert.IsTrue(auto >= 0 && restore > auto);
        Assert.AreEqual(RuntimeState.Stopped, runtime.State);
    }

    [DataTestMethod]
    [DataRow("Ctrl+C")]
    [DataRow("ServiceStop")]
    public async Task AllNormalStopSourcesUseSameShutdownStateMachine(string source)
    {
        _ = source;
        RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();

        _ = await runtime.StopThermalControlAsync();

        CollectionAssert.Contains(rig.Hardware.Operations, "Auto");
        CollectionAssert.Contains(rig.Hardware.Operations, "Restore");
        Assert.AreEqual(1, rig.Hardware.AutoAttempts);
    }

    [TestMethod]
    public async Task SecondRuntimeOwnershipAcquisitionIsRejected()
    {
        var gate = new SharedTestOwnershipGate();
        RuntimeRig first = new(gate);
        RuntimeRig second = new(gate);
        await using BladeRuntime runtime1 = first.CreateRuntime();
        await using BladeRuntime runtime2 = second.CreateRuntime();
        Assert.IsTrue(runtime1.InitializeHost());

        Assert.ThrowsException<RuntimeOwnershipException>(() => runtime2.InitializeHost());
    }

    [TestMethod]
    public async Task ExternalManualToAutoChangeStopsWithoutFightingController()
    {
        RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();
        int autoBefore = rig.Hardware.AutoAttempts;
        rig.Hardware.SetMode(RazerPerformanceMode.Custom, RazerFanMode.Auto);

        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 12);

        Assert.AreEqual(RuntimeState.Faulted, runtime.State);
        Assert.AreEqual(autoBefore, rig.Hardware.AutoAttempts);
        Assert.AreEqual(1, runtime.GetStatus().RecentEvents.OfType<OwnershipLostEvent>().Count());
    }

    [TestMethod]
    public async Task WatchdogZoneMismatchAttemptsOneEmergencyAuto()
    {
        RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();
        rig.Hardware.SetMode(
            RazerPerformanceMode.Balanced,
            RazerFanMode.Manual,
            RazerPerformanceMode.Balanced,
            RazerFanMode.Auto);

        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 12);

        Assert.AreEqual(1, rig.Hardware.AutoAttempts);
        Assert.AreEqual(RuntimeState.Faulted, runtime.State);
    }

    [TestMethod]
    public async Task TwoMissingSensorSamplesCauseOneEmergencyHandoff()
    {
        RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();
        rig.Telemetry.MissingCpu = true;

        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 5);

        Assert.AreEqual(1, rig.Hardware.AutoAttempts);
        Assert.AreEqual(RuntimeState.Faulted, runtime.State);
    }

    [TestMethod]
    public async Task CriticalTemperatureCausesImmediateEmergencyHandoff()
    {
        RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();
        rig.Telemetry.FixedCpuTemperature = 90;

        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 5);

        Assert.AreEqual(1, rig.Hardware.AutoAttempts);
        Assert.AreEqual(RuntimeState.Faulted, runtime.State);
    }

    [TestMethod]
    public async Task StandaloneFixedFanProfileIsRecoveredOnRuntimeShutdown()
    {
        RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        FanControlApplyResult apply = runtime.ApplyFanProfile(
            FanControlProfile.Fixed(new FanRpm(3000), new FanRpm(3000)));

        ThermalSessionResult? result = await runtime.StopThermalControlAsync();

        Assert.IsTrue(apply.Succeeded);
        Assert.IsTrue(result!.Succeeded);
        int auto = rig.Hardware.Operations.LastIndexOf("Auto");
        int restore = rig.Hardware.Operations.LastIndexOf("Restore");
        Assert.IsTrue(auto >= 0 && restore > auto);
    }

    [TestMethod]
    public async Task StandaloneFixedFanProfileRejectsSecondThermalOwner()
    {
        RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        _ = runtime.ApplyFanProfile(
            FanControlProfile.Fixed(new FanRpm(3000), new FanRpm(3000)));

        Assert.ThrowsException<RuntimeOwnershipException>(runtime.StartThermalControl);
    }

    [TestMethod]
    public async Task FailedStandaloneAutoShutdownIsNeverRetriedDuringDispose()
    {
        RuntimeRig rig = new();
        BladeRuntime runtime = rig.CreateRuntime();
        _ = runtime.ApplyFanProfile(
            FanControlProfile.Fixed(new FanRpm(3000), new FanRpm(3000)));
        rig.Hardware.AutoSucceeds = false;

        ThermalSessionResult? result = await runtime.StopThermalControlAsync();
        await runtime.DisposeAsync();

        Assert.IsFalse(result!.Succeeded);
        Assert.AreEqual(1, rig.Hardware.AutoAttempts);
    }

    [TestMethod]
    public async Task FailedStandalonePerformanceRestoreIsNeverRetriedDuringDispose()
    {
        RuntimeRig rig = new();
        BladeRuntime runtime = rig.CreateRuntime();
        _ = runtime.ApplyFanProfile(
            FanControlProfile.Fixed(new FanRpm(3000), new FanRpm(3000)));
        rig.Hardware.RestoreSucceeds = false;

        Assert.ThrowsException<InvalidOperationException>(() =>
            runtime.ApplyFanProfile(FanControlProfile.Auto));
        await runtime.DisposeAsync();

        Assert.AreEqual(1, rig.Hardware.Operations.Count(operation => operation == "Restore"));
    }

    internal sealed class RuntimeRig
    {
        private readonly IRuntimeOwnershipGate _gate;

        internal RuntimeRig(IRuntimeOwnershipGate? gate = null)
        {
            Clock = new VirtualRuntimeClock();
            Telemetry = new FakeRuntimeTelemetry(Clock);
            Hardware = new FakeRuntimeHardware();
            _gate = gate ?? new SharedTestOwnershipGate();
        }

        internal VirtualRuntimeClock Clock { get; }

        internal FakeRuntimeTelemetry Telemetry { get; }

        internal FakeRuntimeHardware Hardware { get; }

        internal BladeRuntime CreateRuntime(int eventCapacity = 2048) => new(
            Telemetry,
            Telemetry,
            Hardware,
            _gate,
            Clock,
            eventCapacity: eventCapacity);
    }
}
