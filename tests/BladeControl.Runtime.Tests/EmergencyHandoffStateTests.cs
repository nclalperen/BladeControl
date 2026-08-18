using BladeControl.Runtime;
using BladeControl.Telemetry;

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
