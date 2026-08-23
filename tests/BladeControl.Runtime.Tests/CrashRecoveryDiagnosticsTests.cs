using BladeControl.Razer;
using BladeControl.Runtime;

namespace BladeControl.Runtime.Tests;

/// <summary>
/// What the runtime reports about itself after recovering a crashed predecessor's fan mode.
/// </summary>
/// <remarks>
/// <para>A hard-killed process — <c>TerminateProcess</c>, a power event, a service crash —
/// runs no managed cleanup, so it leaves the fans in Balanced + Manual with nothing driving
/// them. The SCM restarts the service, and host initialisation performs a one-time recovery
/// back to firmware Auto. That path is the only thing standing between a crash and fans held
/// at a fixed speed indefinitely, and it runs before any session exists.</para>
/// <para>These tests cover what the runtime then reports, because a recovery nobody can
/// observe is indistinguishable from one that never ran.</para>
/// </remarks>
[TestClass]
public sealed class CrashRecoveryDiagnosticsTests
{
    /// <summary>
    /// A successful recovery leaves the watchdog describing the recovered state, not the
    /// orphaned one it replaced.
    /// </summary>
    [TestMethod]
    public async Task RecoveredHostReportsTheRecoveredModeNotTheOrphanedOne()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        rig.Hardware.SetMode(RazerPerformanceMode.Balanced, RazerFanMode.Manual);
        await using BladeRuntime runtime = rig.CreateRuntime();

        Assert.IsTrue(runtime.InitializeHost());
        Assert.AreEqual(1, rig.Hardware.AutoAttempts);

        RuntimeStatus status = runtime.GetStatus();
        Assert.IsNotNull(status.LastRazerWatchdogState);

        // The startup read said Manual; the recovery's own readback says Auto and is newer.
        // Reporting the older of the two describes a machine the runtime has already fixed as
        // still being held in Manual.
        Assert.AreEqual(
            RazerFanMode.Auto,
            status.LastRazerWatchdogState!.Zone1FanMode);
        Assert.AreEqual(RazerFanMode.Auto, status.LastRazerWatchdogState.Zone2FanMode);
        Assert.IsTrue(status.LastRazerWatchdogState.IsKnownAuto);
    }

    /// <summary>A failed recovery is reported as a failure, with its reason.</summary>
    [TestMethod]
    public async Task FailedRecoveryFaultsAndNamesTheReason()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        rig.Hardware.SetMode(RazerPerformanceMode.Balanced, RazerFanMode.Manual);
        rig.Hardware.AutoSucceeds = false;
        await using BladeRuntime runtime = rig.CreateRuntime();

        Assert.IsFalse(runtime.InitializeHost());

        RuntimeStatus status = runtime.GetStatus();
        Assert.AreEqual(RuntimeState.Faulted, status.State);
        StringAssert.Contains(status.LastFailureReason, "ORPHANED MANUAL MODE RECOVERY FAILED");

        // The fans really are still in Manual here, and the report must say so rather than
        // inheriting the optimism of a recovery that did not work.
        Assert.IsNotNull(status.LastRazerWatchdogState);
        Assert.AreEqual(
            RazerFanMode.Manual,
            status.LastRazerWatchdogState!.Zone1FanMode);
    }

    /// <summary>
    /// A session orphaned in Silent or Custom is recovered, not treated as an unsafe state.
    /// </summary>
    /// <remarks>
    /// Recovery used to test for Balanced + Manual specifically, which was equivalent only
    /// while every session forced Balanced. Now that a session runs in the mode the user chose,
    /// a crash in Silent strands the fans exactly as thoroughly, and testing for Balanced would
    /// have walked straight past it and faulted with "not a safe known Auto state" instead of
    /// handing the fans back.
    /// </remarks>
    [DataTestMethod]
    [DataRow((byte)0x04)]
    [DataRow((byte)0x05)]
    public async Task SessionOrphanedOutsideBalancedIsStillRecovered(byte modeValue)
    {
        var mode = new RazerPerformanceMode(modeValue);
        RuntimeLifecycleTests.RuntimeRig rig = new();
        rig.Hardware.SetMode(mode, RazerFanMode.Manual);
        await using BladeRuntime runtime = rig.CreateRuntime();

        Assert.IsTrue(runtime.InitializeHost(), "Orphaned Manual must be recoverable.");
        Assert.AreEqual(1, rig.Hardware.AutoAttempts);

        RuntimeStatus status = runtime.GetStatus();
        Assert.IsNotNull(status.LastRazerWatchdogState);
        Assert.AreEqual(
            RazerFanMode.Auto,
            status.LastRazerWatchdogState!.Zone1FanMode,
            "Firmware must own the fans after recovery.");
        Assert.IsTrue(status.LastRazerWatchdogState.IsKnownAuto);

        // Recovering a stranded session is not an occasion to change the user's mode.
        Assert.AreEqual(
            mode,
            status.LastRazerWatchdogState.Zone1PerformanceMode,
            "Recovery must hand the fans back without moving the machine to Balanced.");
    }

    /// <summary>
    /// A performance-mode change during a session hands the fans back to firmware.
    /// </summary>
    /// <remarks>
    /// <para>The GPU thermal limits a session runs under are derived from its mode's thermal
    /// anchor: 87/89/92 in Balanced, 75/77/80 in Silent and Custom. A mode change from outside
    /// — a keyboard shortcut, vendor software — leaves the fan mode untouched, so ownership
    /// still looks intact while the ladder carries on with limits that no longer describe the
    /// machine.</para>
    /// <para>One direction is dangerous rather than merely wrong. Balanced to Silent drops the
    /// real target from 87 to 75 while the ladder still holds 87-based thresholds, so every
    /// rung fires about twelve degrees late. This is a hole that supporting multiple modes
    /// opened, and it did not exist while every session forced Balanced.</para>
    /// </remarks>
    [TestMethod]
    public async Task PerformanceModeChangingUnderARunningSessionRequalifiesRatherThanEnding()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        rig.Hardware.SetMode(RazerPerformanceMode.Balanced, RazerFanMode.Auto);
        await using BladeRuntime runtime = rig.CreateRuntime(
            watchdogInterval: TimeSpan.FromMilliseconds(1));

        Assert.IsTrue(runtime.InitializeHost());
        runtime.StartThermalControl();
        Assert.AreEqual(RuntimeState.Running, runtime.GetStatus().State);

        // The machine moves to Silent underneath the session; the fans stay Manual.
        rig.Hardware.SetMode(RazerPerformanceMode.Silent, RazerFanMode.Manual);
        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 12);

        // The limits went stale, not the session. Re-deriving them for the mode now in force
        // is the fix; ending the session called a deliberate user action a thermal emergency
        // and, on the way out, restored the captured performance state - undoing the very
        // change that triggered it.
        RuntimeStatus status = runtime.GetStatus();
        Assert.AreEqual(
            RuntimeState.Running,
            status.State,
            "A deliberate mode change is not a reason to stop cooling.");
        Assert.IsTrue(string.IsNullOrEmpty(status.EmergencyStatus));
    }

    /// <summary>A host that started from a safe state does not write to the hardware.</summary>
    [TestMethod]
    public async Task StartupFromAutoAttemptsNoRecovery()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        rig.Hardware.SetMode(RazerPerformanceMode.Balanced, RazerFanMode.Auto);
        await using BladeRuntime runtime = rig.CreateRuntime();

        Assert.IsTrue(runtime.InitializeHost());
        Assert.AreEqual(0, rig.Hardware.AutoAttempts);
    }
}
