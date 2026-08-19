using BladeControl.Runtime;
using BladeControl.Telemetry;
using BladeControl.Thermal;

namespace BladeControl.Runtime.Tests;

/// <summary>
/// The Dynamic-start gate at the boundary the installed service actually calls.
/// </summary>
/// <remarks>
/// <para>An installed RC displayed "GPU thermal limits: unavailable" and "Thermal ownership
/// ready: Yes" in the same report. The qualifier itself was correct — the CLI heading was
/// printing sensor health under a qualification label — but a unit test of the qualifier alone
/// would not have caught it, because the qualifier was never the thing that was wrong.</para>
/// <para>So these tests sit on <see cref="BladeRuntime.StartThermalControl"/>, the entry point
/// the service uses, and assert the invariant end to end: no GPU thermal limits means the start
/// is refused, the runtime stays Stopped, and nothing is written to the hardware.</para>
/// </remarks>
[TestClass]
public sealed class GpuLimitStartGateTests
{
    [TestMethod]
    public async Task StartIsRefusedWhenGpuThermalLimitsAreUnavailable()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        rig.Telemetry.GpuThermalLimits = null;

        ThermalPreflightException rejection =
            Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        StringAssert.Contains(rejection.Message, "GPU thermal limits");
        Assert.AreEqual(RuntimeState.Stopped, runtime.State, "A refusal is not a fault.");
    }

    /// <summary>The prohibition that matters: nothing reaches the hardware.</summary>
    [TestMethod]
    public async Task RefusedStartSendsNoRazerWrites()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        rig.Telemetry.GpuThermalLimits = null;

        Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        Assert.AreEqual(0, rig.Hardware.FanWrites, "No fan SET.");
        Assert.AreEqual(0, rig.Hardware.PerformanceApplies, "No performance SET.");
        Assert.AreEqual(0, rig.Hardware.AutoAttempts, "Nothing to hand back — nothing was taken.");
    }

    /// <summary>
    /// The qualification the service exposes and the start decision must agree. They are the
    /// same call, so a report saying "ready" while start refuses is not expressible.
    /// </summary>
    [TestMethod]
    public async Task ReportedQualificationMatchesTheStartDecision()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        rig.Telemetry.GpuThermalLimits = null;

        ThermalOwnershipQualification qualification = runtime.QualifyThermalOwnership();
        Assert.IsFalse(qualification.ThermalOwnershipReady);
        Assert.IsFalse(qualification.GpuThermalLimitsKnown);
        Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        rig.Telemetry.GpuThermalLimits = FakeRuntimeTelemetry.ReferenceGpuLimits;
        Assert.IsTrue(runtime.QualifyThermalOwnership().ThermalOwnershipReady);
        runtime.StartThermalControl();
        Assert.AreEqual(RuntimeState.Running, runtime.State);
    }

    /// <summary>The reported qualification carries why, not merely that.</summary>
    [TestMethod]
    public async Task ReportedQualificationCarriesTheDiscoveryReason()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        rig.Telemetry.GpuThermalLimits = null;
        rig.Telemetry.GpuThermalLimitDiagnostic =
            "GPU \"NVIDIA GeForce RTX 9999\" has no validated thermal signature.";

        ThermalOwnershipQualification qualification = runtime.QualifyThermalOwnership();

        StringAssert.Contains(qualification.GpuThermalLimitDiagnostic, "no validated thermal signature");
        Assert.IsTrue(
            qualification.Reasons.Any(reason => reason.Contains("validated thermal signature")),
            "The reason a machine was refused must reach whoever reads the refusal.");
    }

    [TestMethod]
    public async Task ValidatedLimitsAllowStartAndReachTheThermalPolicy()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();

        runtime.StartThermalControl();

        Assert.AreEqual(RuntimeState.Running, runtime.State);
        Assert.IsTrue(runtime.QualifyThermalOwnership().GpuThermalLimitsKnown);
    }
}
