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
