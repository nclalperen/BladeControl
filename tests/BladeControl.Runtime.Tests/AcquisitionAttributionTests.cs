using BladeControl.Runtime;

namespace BladeControl.Runtime.Tests;

/// <summary>
/// Acquisition cost is reported per provider, not only in aggregate.
/// </summary>
/// <remarks>
/// <para>The scheduler limitation in docs/known-limitations.md is that acquisition consumes
/// roughly 390 ms of a 500 ms control period, so any cycle that also writes a fan target
/// overruns. That file says the per-component statistics exist so the fix can be decided on
/// distribution data. They did not: the Windows session measured the CPU and GPU reads
/// separately from the start, and threw both away every cycle, leaving only the total — which
/// says the cycle is tight without saying which read to go after.</para>
/// <para>These assert the attribution rather than any particular duration. A split that
/// silently reported the same number for both, or attributed one provider's cost to the other,
/// would send the eventual fix at the wrong half of the problem.</para>
/// </remarks>
[TestClass]
public sealed class AcquisitionAttributionTests
{
    [TestMethod]
    public async Task AcquisitionIsAttributedToTheProviderThatSpentIt()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        rig.Telemetry.WorkDuration = TimeSpan.FromMilliseconds(400);
        rig.Telemetry.CpuShareOfWork = 0.9;

        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        runtime.StartThermalControl();
        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 6);

        RuntimeStatus status = runtime.GetStatus();

        Assert.IsTrue(
            status.CpuAcquisition.SampleCount > 0,
            "The CPU read's cost must be recorded, or the aggregate is all anyone can see.");
        Assert.IsTrue(
            status.GpuAcquisition.SampleCount > 0,
            "The GPU read's cost must be recorded.");
        Assert.IsTrue(
            status.CpuAcquisition.Latest > status.GpuAcquisition.Latest,
            $"A 90/10 split must be reported as one: CPU {status.CpuAcquisition.Latest}, " +
            $"GPU {status.GpuAcquisition.Latest}. Reporting them equal, or the wrong way " +
            "round, would point the fix at the wrong provider.");

        await runtime.StopThermalControlAsync();
    }

    /// <summary>
    /// The two halves account for the aggregate, so neither hides cost in the other.
    /// </summary>
    [TestMethod]
    public async Task ThePerProviderCostsSumToTheAggregate()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        rig.Telemetry.WorkDuration = TimeSpan.FromMilliseconds(400);
        rig.Telemetry.CpuShareOfWork = 0.75;

        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        runtime.StartThermalControl();
        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 6);

        RuntimeStatus status = runtime.GetStatus();
        TimeSpan parts = status.CpuAcquisition.Latest + status.GpuAcquisition.Latest;

        Assert.AreEqual(
            status.TelemetryAcquisition.Latest.TotalMilliseconds,
            parts.TotalMilliseconds,
            1.0,
            $"CPU ({status.CpuAcquisition.Latest}) plus GPU ({status.GpuAcquisition.Latest}) " +
            $"must account for the acquisition total ({status.TelemetryAcquisition.Latest}). " +
            "A gap between them is cost nobody is attributing to anything.");

        await runtime.StopThermalControlAsync();
    }
}
