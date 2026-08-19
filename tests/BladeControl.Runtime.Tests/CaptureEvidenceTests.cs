using BladeControl.Razer;
using BladeControl.Runtime;
using BladeControl.Thermal;

namespace BladeControl.Runtime.Tests;

/// <summary>
/// A refused start must keep the evidence that explains the refusal.
/// </summary>
/// <remarks>
/// <para>Field incident: Dynamic start was correctly refused with "the captured zones report
/// different performance modes", and Diagnostics — read moments later — showed both zones
/// reporting Custom / Auto and agreeing. Both statements were true. The capture had caught a
/// transient, and the rejection had discarded the only record of it.</para>
/// <para>Nothing about the safety decision was wrong. What was wrong is that answering "was
/// that a non-persistent observation or a genuinely asymmetric machine?" required a second
/// firmware read, which by definition observes a different moment.</para>
/// </remarks>
[TestClass]
public sealed class CaptureEvidenceTests
{
    [TestMethod]
    public async Task RejectedStartNamesTheCapturedZoneValues()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();

        // Initialize before changing mode: host initialization performs a one-time
        // orphaned-Manual recovery that would otherwise consume the state under test.
        Assert.IsTrue(runtime.InitializeHost());
        rig.Hardware.SetMode(
            RazerPerformanceMode.Custom,
            RazerFanMode.Auto,
            RazerPerformanceMode.Balanced,
            RazerFanMode.Auto);

        ThermalPreflightException rejection =
            Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        StringAssert.Contains(rejection.Message, "did not stabilize safely");
        StringAssert.Contains(rejection.Message, "Capture A:");
        StringAssert.Contains(rejection.Message, "Capture B:");
        StringAssert.Contains(rejection.Message, "Capture C:");
        StringAssert.Contains(rejection.Message, "zone 1 performance = Custom");
        StringAssert.Contains(rejection.Message, "zone 2 performance = Balanced");
        StringAssert.Contains(rejection.Message, "zone 1 fan mode = Auto");
        StringAssert.Contains(rejection.Message, "No SET was sent.");
    }

    /// <summary>
    /// The capture reaches the event log too, so the evidence survives past the one dialog that
    /// showed the rejection.
    /// </summary>
    [TestMethod]
    public async Task RejectedStartEmitsTheCapturedStateAsAnEvent()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        List<RuntimeEvent> published = [];
        runtime.EventPublished += published.Add;
        Assert.IsTrue(runtime.InitializeHost());
        rig.Hardware.SetMode(
            RazerPerformanceMode.Custom,
            RazerFanMode.Auto,
            RazerPerformanceMode.Balanced,
            RazerFanMode.Auto);

        Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        List<RestorationStateCapturedEvent> captures = published
            .OfType<RestorationStateCapturedEvent>()
            .ToList();

        // Every capture is published, labelled, so the sequence is legible afterwards.
        CollectionAssert.AreEqual(
            new[] { "A", "B", "C" },
            captures.Select(item => item.Capture).ToArray());
        foreach (RestorationStateCapturedEvent capture in captures)
        {
            Assert.AreEqual("Custom", capture.Zone1PerformanceMode);
            Assert.AreEqual("Balanced", capture.Zone2PerformanceMode);
            Assert.AreEqual("Auto", capture.Zone1FanMode);
            Assert.AreEqual("Auto", capture.Zone2FanMode);
            Assert.IsFalse(capture.ZonesAgree);
            Assert.IsFalse(capture.Accepted);
        }
    }

    /// <summary>
    /// The fields are carried individually so successive captures can be compared without
    /// parsing prose — which is what distinguishes a transient from a persistent state.
    /// </summary>
    [TestMethod]
    public async Task CapturedStateIsStructuredNotOnlyFormatted()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        List<RuntimeEvent> published = [];
        runtime.EventPublished += published.Add;

        runtime.StartThermalControl();

        List<RestorationStateCapturedEvent> captures = published
            .OfType<RestorationStateCapturedEvent>()
            .ToList();

        CollectionAssert.AreEqual(
            new[] { "A", "B" },
            captures.Select(item => item.Capture).ToArray(),
            "A stable machine settles in two captures; C is only taken when needed.");
        Assert.IsTrue(captures.All(capture => capture.ZonesAgree));
        Assert.IsFalse(captures[0].Accepted, "A is corroboration, not the adopted state.");
        Assert.IsTrue(captures[1].Accepted, "B is the state the session promises to restore.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(captures[1].CpuLevel));
        Assert.IsFalse(string.IsNullOrWhiteSpace(captures[1].GpuLevel));
    }

    /// <summary>Preserving evidence must not have loosened the gate.</summary>
    [TestMethod]
    public async Task DisagreeingCaptureStillSendsNothingToTheHardware()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();

        // Initialize before changing mode: host initialization performs a one-time
        // orphaned-Manual recovery that would otherwise consume the state under test.
        Assert.IsTrue(runtime.InitializeHost());
        rig.Hardware.SetMode(
            RazerPerformanceMode.Custom,
            RazerFanMode.Auto,
            RazerPerformanceMode.Balanced,
            RazerFanMode.Auto);

        Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        Assert.AreEqual(0, rig.Hardware.FanWrites);
        Assert.AreEqual(0, rig.Hardware.PerformanceApplies);
        Assert.AreEqual(0, rig.Hardware.AutoAttempts);
        Assert.AreEqual(RuntimeState.Stopped, runtime.State);
    }

    /// <summary>
    /// The capture is restoration data only. It costs no ownership read, because a machine
    /// that cannot be restored must never pay for the two-GET gate.
    /// </summary>
    [TestMethod]
    public async Task DisagreeingCaptureCostsNoOwnershipRead()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        int modeReadsBefore = rig.Hardware.ModeReads;
        rig.Hardware.SetMode(
            RazerPerformanceMode.Custom,
            RazerFanMode.Auto,
            RazerPerformanceMode.Balanced,
            RazerFanMode.Auto);

        Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        Assert.AreEqual(
            modeReadsBefore,
            rig.Hardware.ModeReads,
            "The fresh 0x0D82 ownership gate must not run for capture data that never settled.");
    }

    /// <summary>
    /// A symmetric capture outside the validated restoration policy is refused with its values
    /// too — the other rejection branch must not stay mute.
    /// </summary>
    [TestMethod]
    public async Task PolicyRejectionAlsoNamesTheCapturedValues()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();

        // Initialize before changing mode: host initialization performs a one-time
        // orphaned-Manual recovery that would otherwise consume the state under test.
        Assert.IsTrue(runtime.InitializeHost());
        rig.Hardware.SetMode(
            RazerPerformanceMode.Custom,
            RazerFanMode.Auto,
            RazerPerformanceMode.Custom,
            RazerFanMode.Auto);
        rig.Hardware.SetPerformanceLevels(RazerCpuPerformanceLevel.High, RazerGpuPerformanceLevel.Low);

        ThermalPreflightException rejection =
            Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        StringAssert.Contains(rejection.Message, "CPU level = High");
        StringAssert.Contains(rejection.Message, "No SET was sent.");
    }
}
