using BladeControl.Razer;
using BladeControl.Runtime;
using BladeControl.Telemetry;
using BladeControl.Thermal;

namespace BladeControl.Runtime.Tests;

/// <summary>
/// Runtime-level behaviour of the graded thermal-safety model: which state a handoff ends in,
/// and that a handoff is final until a person intervenes.
/// </summary>
/// <remarks>
/// <para>Live diagnostics reported <c>Faulted</c> after a deliberate, fully successful
/// emergency handoff — firmware Auto was established and the captured performance state was
/// restored, yet the runtime described itself as broken. Both outcomes shared one state, so
/// "the safety system worked" and "the safety system could not run" were indistinguishable to
/// the user interface.</para>
/// <para>All synthetic: the rig's fake hardware and telemetry only. No HID, no fan write, no
/// performance write, no service.</para>
/// </remarks>
[TestClass]
public sealed class EmergencyHandoffStateTests
{
    [TestMethod]
    public async Task SuccessfulAutoHandoffEndsInEmergencyHandoffNotFaulted()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();
        rig.Telemetry.FixedCpuTemperature =
            TelemetryHealthEvaluator.CpuImmediateEmergencyTemperatureCelsius;

        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 5);

        Assert.AreEqual(1, rig.Hardware.AutoAttempts);
        Assert.AreEqual(
            RuntimeState.EmergencyHandoff,
            runtime.State,
            "Firmware Auto was established and verified; that is the protection working.");
    }

    [TestMethod]
    public async Task FailedAutoHandoffEndsInFaulted()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();

        // The handoff itself cannot be established: this is the genuinely alarming case, and
        // the one Faulted is reserved for.
        rig.Hardware.AutoSucceeds = false;
        rig.Telemetry.FixedCpuTemperature =
            TelemetryHealthEvaluator.CpuImmediateEmergencyTemperatureCelsius;

        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 5);

        Assert.AreEqual(
            RuntimeState.Faulted,
            runtime.State,
            "Firmware does not verifiably own cooling, so this is a fault.");
    }

    [TestMethod]
    public async Task EmergencyHandoffDoesNotAutomaticallyRestartThermalControl()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();
        rig.Telemetry.FixedCpuTemperature =
            TelemetryHealthEvaluator.CpuImmediateEmergencyTemperatureCelsius;

        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 5);
        Assert.AreEqual(RuntimeState.EmergencyHandoff, runtime.State);

        int autoAttempts = rig.Hardware.AutoAttempts;
        int fanWrites = rig.Hardware.FanWrites;

        // Cool down completely: even with the heat gone, the scheduler refuses to run again.
        // Recovery is a deliberate user action, and the runtime enforces that by refusing the
        // call outright rather than quietly resuming Manual control.
        rig.Telemetry.FixedCpuTemperature = 45;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 40).AsTask());

        Assert.AreEqual(
            RuntimeState.EmergencyHandoff,
            runtime.State,
            "The runtime must stay handed off until a person acts.");
        Assert.AreEqual(autoAttempts, rig.Hardware.AutoAttempts, "No second handoff attempt.");
        Assert.AreEqual(
            fanWrites,
            rig.Hardware.FanWrites,
            "No fan write may follow a handoff: firmware owns cooling now.");
    }

    /// <summary>
    /// The spike that caused the incident, driven all the way through the runtime rather than
    /// the decision engine alone: the session survives and keeps control.
    /// </summary>
    [TestMethod]
    public async Task TransientNinetyDegreeSpikeKeepsTheSessionAndRaisesFans()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();

        rig.Telemetry.FixedCpuTemperature =
            TelemetryHealthEvaluator.CpuCriticalCoolingTemperatureCelsius;
        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 3);

        Assert.AreEqual(0, rig.Hardware.AutoAttempts, "A 90 C spike must not hand off.");
        Assert.AreEqual(
            RuntimeState.Running,
            runtime.State,
            "The thermal session continues; only the fan target changes.");

        rig.Telemetry.FixedCpuTemperature = 60;
        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 10);

        Assert.AreEqual(0, rig.Hardware.AutoAttempts);
        Assert.AreEqual(RuntimeState.Running, runtime.State);
    }

    [TestMethod]
    public async Task SustainedHeatStillHandsOffThroughTheRuntime()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();

        // Held above the sustained threshold, not a spike: the ultimate fail-safe must fire.
        rig.Telemetry.FixedCpuTemperature =
            TelemetryHealthEvaluator.CpuSustainedEmergencyTemperatureCelsius;
        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 10);

        Assert.AreEqual(1, rig.Hardware.AutoAttempts);
        Assert.AreEqual(RuntimeState.EmergencyHandoff, runtime.State);
    }

    // --- The GPU ladder, driven through the runtime -------------------------------------------

    /// <summary>
    /// The GPU counterpart of the CPU spike case. 75 C is this device's maximum operating
    /// temperature, well short of the 80 C at which it shuts itself down, so the answer is
    /// more cooling rather than surrendering control.
    /// </summary>
    [TestMethod]
    public async Task GpuAtMaxOperatingTemperatureRaisesFansWithoutHandingOff()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();

        rig.Telemetry.FixedGpuTemperature =
            FakeRuntimeTelemetry.ReferenceGpuLimits.CriticalCoolingCelsius;
        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 3);

        Assert.AreEqual(0, rig.Hardware.AutoAttempts);
        Assert.AreEqual(RuntimeState.Running, runtime.State);
        CollectionAssert.Contains(
            rig.Hardware.Operations,
            $"Set {FanRpm.MaximumValue}",
            "Maximum cooling, not a handoff.");
    }

    [TestMethod]
    public async Task SustainedGpuSlowdownHandsOffAsEmergencyHandoffNotFaulted()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();

        rig.Telemetry.FixedGpuTemperature =
            FakeRuntimeTelemetry.ReferenceGpuLimits.SustainedEmergencyCelsius;
        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 10);

        Assert.AreEqual(1, rig.Hardware.AutoAttempts);
        Assert.AreEqual(RuntimeState.EmergencyHandoff, runtime.State);
    }

    /// <summary>
    /// The old behaviour handed off at a fixed 80 C, which on this device is the hardware
    /// shutdown point itself. The handoff now happens with a degree of margin instead.
    /// </summary>
    [TestMethod]
    public async Task GpuNearHardwareShutdownHandsOffBeforeReachingIt()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();

        rig.Telemetry.FixedGpuTemperature =
            FakeRuntimeTelemetry.ReferenceGpuLimits.ImmediateEmergencyCelsius;
        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 5);

        Assert.AreEqual(1, rig.Hardware.AutoAttempts);
        Assert.AreEqual(RuntimeState.EmergencyHandoff, runtime.State);
        Assert.IsTrue(
            FakeRuntimeTelemetry.ReferenceGpuLimits.ImmediateEmergencyCelsius <
                FakeRuntimeTelemetry.ReferenceGpuLimits.HardwareShutdownCelsius,
            "The handoff must arrive before the GPU's own shutdown point.");
    }

    /// <summary>
    /// 74 C used to be indistinguishable from any other reading; it is still ordinary, which
    /// is the point — the ladder did not simply move the old cliff downward.
    /// </summary>
    [TestMethod]
    public async Task GpuBelowMaxOperatingTemperatureIsOrdinary()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();

        rig.Telemetry.FixedGpuTemperature =
            FakeRuntimeTelemetry.ReferenceGpuLimits.CriticalCoolingCelsius - 1;
        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 10);

        Assert.AreEqual(0, rig.Hardware.AutoAttempts);
        Assert.AreEqual(RuntimeState.Running, runtime.State);
    }

    /// <summary>
    /// Without limits there is no safe threshold to act on, so thermal ownership is refused
    /// up front rather than falling back to an assumed number.
    /// </summary>
    [TestMethod]
    public async Task StartIsRefusedWhenTheGpuCannotReportItsThermalLimits()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        rig.Telemetry.GpuThermalLimits = null;

        ThermalPreflightException rejection =
            Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        StringAssert.Contains(rejection.Message, "GPU thermal limits");
        Assert.AreEqual(RuntimeState.Stopped, runtime.State, "A refusal is not a fault.");
        Assert.AreEqual(0, rig.Hardware.FanWrites, "No SET may be sent by a refused start.");
    }

    /// <summary>
    /// A field report of a handoff should not require guessing what the thresholds were.
    /// </summary>
    [TestMethod]
    public async Task SessionStartRecordsTheThermalLimitsItIsRunningUnder()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        List<string> messages = [];
        runtime.EventPublished += published => messages.Add(published.Message);

        runtime.StartThermalControl();

        string started = string.Join(" ", messages);
        StringAssert.Contains(started, "max operating 75 C");
        StringAssert.Contains(started, "hardware slowdown 77 C");
        StringAssert.Contains(started, "hardware shutdown 80 C");
        StringAssert.Contains(started, "NVML device thermal limits");
    }

    [TestMethod]
    public async Task CapturedPerformanceStateIsStillRestoredOnEmergencyHandoff()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();
        rig.Telemetry.FixedCpuTemperature =
            TelemetryHealthEvaluator.CpuImmediateEmergencyTemperatureCelsius;

        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 5);

        Assert.AreEqual(RuntimeState.EmergencyHandoff, runtime.State);

        // Same ordering the normal stop path guarantees: firmware Auto first, then the
        // captured performance state. Restoring performance before Auto would leave a manual
        // fan target briefly paired with restored levels.
        int auto = rig.Hardware.Operations.LastIndexOf("Auto");
        int restore = rig.Hardware.Operations.LastIndexOf("Restore");
        Assert.IsTrue(auto >= 0, "Firmware Auto must be established.");
        Assert.IsTrue(
            restore > auto,
            "The performance state captured at Start must still be restored after Auto.");
    }
}
