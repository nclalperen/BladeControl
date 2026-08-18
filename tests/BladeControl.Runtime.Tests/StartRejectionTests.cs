using BladeControl.Razer;
using BladeControl.Runtime;
using BladeControl.Thermal;

namespace BladeControl.Runtime.Tests;

/// <summary>
/// Runtime behaviour when a Start Dynamic request cannot proceed: what it costs in Razer
/// exchanges, and what state the runtime is left in.
/// </summary>
/// <remarks>
/// <para>Field incident: Start Dynamic was refused with "Thermal control must start from a
/// consistent Auto fan mode" while firmware reads moments later showed both zones Custom /
/// Auto and thermal ownership ready. No SET was sent — and the runtime went to Faulted, which
/// could only be cleared by restarting the service. A prerequisite that is not met is not a
/// fault: nothing broke, firmware still owns cooling, and the next attempt may well succeed.</para>
/// <para>Entirely synthetic — the rig's fake hardware and telemetry. No HID, no SET, no
/// service, no installed product.</para>
/// </remarks>
[TestClass]
public sealed class StartRejectionTests
{
    // --- Cost of the qualification -----------------------------------------------------------

    /// <summary>
    /// The ownership gate is two mode reads. Fan RPM says nothing about who owns the fans, so
    /// it must not be what authorises taking them.
    /// </summary>
    [TestMethod]
    public async Task OwnershipQualificationReadsBothZoneModesAndNoFanRpm()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        rig.Hardware.Operations.Clear();

        runtime.StartThermalControl();

        Assert.AreEqual(
            1,
            rig.Hardware.FanModeObservations,
            "Exactly one fresh two-GET 0x0D82 observation gates the transition.");

        int observation = rig.Hardware.Operations.LastIndexOf("ReadFanMode");
        Assert.IsTrue(observation >= 0, "The fresh observation must happen.");
    }

    [TestMethod]
    public async Task FreshObservationIsTakenEvenThoughTheRuntimeAlreadyHoldsAWatchdogPicture()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();

        // Give the runtime a watchdog reading first, so a cached picture genuinely exists.
        _ = rig.Hardware.ReadModeState();
        int observationsBefore = rig.Hardware.FanModeObservations;

        runtime.StartThermalControl();

        Assert.AreEqual(
            observationsBefore + 1,
            rig.Hardware.FanModeObservations,
            "A held watchdog picture must not stand in for the ownership read.");
    }

    // --- Historical state must not decide ------------------------------------------------------

    /// <summary>
    /// The field case: firmware is in Auto right now, so the start must be allowed regardless
    /// of what any earlier observation said.
    /// </summary>
    [TestMethod]
    public async Task StartSucceedsWhenLiveFirmwareIsAutoInCustomPerformanceMode()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();

        // Exactly the state Diagnostics reported after the refused start.
        rig.Hardware.SetMode(RazerPerformanceMode.Custom, RazerFanMode.Auto);

        runtime.StartThermalControl();

        Assert.AreEqual(RuntimeState.Running, runtime.State);
    }

    [TestMethod]
    public async Task StartIsRefusedWhenLiveFirmwareIsManual()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        // Initialise while the machine is still in Auto. InitializeHost performs a one-time
        // orphaned-Balanced+Manual recovery, so setting Manual before it runs would be quietly
        // repaired rather than refused — and the realistic case is the mode changing after the
        // service is already up.
        Assert.IsTrue(runtime.InitializeHost());
        rig.Hardware.SetMode(RazerPerformanceMode.Balanced, RazerFanMode.Manual);
        int fanWrites = rig.Hardware.FanWrites;

        Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        Assert.AreEqual(fanWrites, rig.Hardware.FanWrites, "No SET may be sent on refusal.");
    }

    [TestMethod]
    public async Task StartIsRefusedWhenTheZonesDisagree()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        // Initialise while the machine is still in Auto. InitializeHost performs a one-time
        // orphaned-Balanced+Manual recovery, so setting Manual before it runs would be quietly
        // repaired rather than refused — and the realistic case is the mode changing after the
        // service is already up.
        Assert.IsTrue(runtime.InitializeHost());
        rig.Hardware.SetMode(
            RazerPerformanceMode.Balanced,
            RazerFanMode.Auto,
            RazerPerformanceMode.Balanced,
            RazerFanMode.Manual);
        int fanWrites = rig.Hardware.FanWrites;

        Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        Assert.AreEqual(fanWrites, rig.Hardware.FanWrites);
    }

    /// <summary>
    /// The same discrimination at runtime level: the six-GET capture reports Manual while the
    /// fresh two-GET observation reports Auto, and the start proceeds.
    /// </summary>
    [TestMethod]
    public async Task CapturedManualWithFreshAutoStartsBecauseOnlyTheFreshReadDecides()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());

        // Capture will report Manual; the live ownership read reports Auto.
        rig.Hardware.SetMode(RazerPerformanceMode.Balanced, RazerFanMode.Manual);
        rig.Hardware.FreshFanModeOverride = new ThermalFanModeObservation(
            RazerPerformanceMode.Custom,
            RazerFanMode.Auto,
            RazerPerformanceMode.Custom,
            RazerFanMode.Auto,
            []);

        runtime.StartThermalControl();

        Assert.AreEqual(
            RuntimeState.Running,
            runtime.State,
            "The capture has no veto over ownership; only the fresh observation decides.");
    }

    /// <summary>The converse: a stale-looking Auto capture cannot authorise over a live Manual.</summary>
    [TestMethod]
    public async Task CapturedAutoWithFreshManualIsRefusedWithZeroSets()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());

        rig.Hardware.SetMode(RazerPerformanceMode.Balanced, RazerFanMode.Auto);
        rig.Hardware.FreshFanModeOverride = new ThermalFanModeObservation(
            RazerPerformanceMode.Balanced,
            RazerFanMode.Manual,
            RazerPerformanceMode.Balanced,
            RazerFanMode.Manual,
            []);
        int fanWrites = rig.Hardware.FanWrites;

        ThermalPreflightException rejected =
            Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        StringAssert.Contains(rejected.Message, "Manual");
        Assert.AreEqual(fanWrites, rig.Hardware.FanWrites, "Zero SETs on refusal.");
        Assert.AreEqual(RuntimeState.Stopped, runtime.State);
    }

    // --- A refused start is not a fault ---------------------------------------------------------

    [TestMethod]
    public async Task RefusedStartLeavesTheRuntimeStoppedRatherThanFaulted()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        rig.Hardware.SetMode(RazerPerformanceMode.Balanced, RazerFanMode.Manual);

        Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        Assert.AreEqual(
            RuntimeState.Stopped,
            runtime.State,
            "Nothing broke and firmware still owns cooling; that is Stopped, not Faulted.");
    }

    [TestMethod]
    public async Task RefusedStartRecordsWhyWithoutClaimingAFault()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        rig.Hardware.SetMode(RazerPerformanceMode.Balanced, RazerFanMode.Manual);

        Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        RuntimeStatus status = runtime.GetStatus();
        Assert.AreEqual(RuntimeState.Stopped, status.State);
        Assert.IsNotNull(status.LastFailureReason, "The reason must survive for the interface.");
        StringAssert.Contains(status.LastFailureReason, "rejected");
        StringAssert.Contains(status.LastFailureReason, "No SET was sent.");
    }

    /// <summary>
    /// The expensive half of the old behaviour: a refusal used to require a service restart.
    /// </summary>
    [TestMethod]
    public async Task RefusedStartDoesNotMakeTheRuntimePermanentlyUnrestartable()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        rig.Hardware.SetMode(RazerPerformanceMode.Balanced, RazerFanMode.Manual);

        Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);
        Assert.AreEqual(RuntimeState.Stopped, runtime.State);

        // The user puts the fans back into Auto and tries again — no restart in between.
        rig.Hardware.SetMode(RazerPerformanceMode.Custom, RazerFanMode.Auto);
        runtime.StartThermalControl();

        Assert.AreEqual(RuntimeState.Running, runtime.State);
    }

    [TestMethod]
    public async Task RepeatedRefusalsStayRecoverable()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        rig.Hardware.SetMode(RazerPerformanceMode.Balanced, RazerFanMode.Manual);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);
            Assert.AreEqual(RuntimeState.Stopped, runtime.State);
        }

        rig.Hardware.SetMode(RazerPerformanceMode.Balanced, RazerFanMode.Auto);
        runtime.StartThermalControl();

        Assert.AreEqual(RuntimeState.Running, runtime.State);
    }

    // --- Restoration-data rejection is a prerequisite failure, not a poisoned runtime --------

    /// <summary>
    /// Captured zones disagreeing on performance mode is a start prerequisite failure. It
    /// happens before any SET, so it must leave the runtime Stopped and retryable — not
    /// Faulted merely because the helper that detects it once threw
    /// InvalidOperationException.
    /// </summary>
    [TestMethod]
    public async Task CapturedPerformanceDisagreementLeavesTheRuntimeStoppedAndRetryable()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());

        rig.Hardware.SetMode(
            RazerPerformanceMode.Balanced,
            RazerFanMode.Auto,
            RazerPerformanceMode.Silent,
            RazerFanMode.Auto);
        int fanWrites = rig.Hardware.FanWrites;

        Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        Assert.AreEqual(fanWrites, rig.Hardware.FanWrites, "Zero SETs.");
        Assert.AreEqual(RuntimeState.Stopped, runtime.State, "Stopped, not Faulted.");

        RuntimeStatus status = runtime.GetStatus();
        StringAssert.Contains(status.LastFailureReason, "rejected");
        StringAssert.Contains(status.LastFailureReason, "No SET was sent.");

        // The machine is put back into a coherent state and the user tries again — no service
        // restart in between.
        rig.Hardware.SetMode(RazerPerformanceMode.Custom, RazerFanMode.Auto);
        runtime.StartThermalControl();

        Assert.AreEqual(RuntimeState.Running, runtime.State);
    }

    /// <summary>
    /// Faulted is still reserved for the case where the control path itself is broken, which
    /// this deliberately does not weaken.
    /// </summary>
    [TestMethod]
    public async Task AGenuineControlFailureStillFaults()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());

        // Qualification passes; the SET itself is what fails.
        rig.Hardware.FanApplySucceeds = false;

        Assert.ThrowsException<InvalidOperationException>(runtime.StartThermalControl);

        Assert.AreEqual(
            RuntimeState.Faulted,
            runtime.State,
            "A SET that failed is a real fault, unlike a prerequisite that was not met.");
    }
}
