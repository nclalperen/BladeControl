using BladeControl.Runtime;
using BladeControl.Telemetry;

namespace BladeControl.Runtime.Tests;

/// <summary>
/// Monitoring clients share one hardware acquisition instead of each causing their own.
/// </summary>
/// <remarks>
/// <para>Measured against the installed service before this was added: a
/// <c>GetTelemetrySample</c> request cost 330–420 ms, against about 1 ms for a status request,
/// because every call performed a full provider acquisition. The interface polls every 500 ms,
/// so an idle machine controlling nothing spent a large and permanent fraction of wall-clock
/// time reading hardware — holding the operation gate against control commands the whole while,
/// and keeping the discrete GPU out of the power-saving state whose absence is itself recorded
/// in docs/known-limitations.md.</para>
/// <para>The rate limit lives in the runtime rather than in the client because the runtime owns
/// the hardware. A polite client is not a guarantee, and there can be more than one: the
/// interface and the diagnostic CLI can poll at the same time.</para>
/// </remarks>
[TestClass]
public sealed class MonitoringAcquisitionRateTests
{
    /// <summary>
    /// Polling faster than the reuse window must not read the hardware faster than it.
    /// </summary>
    /// <remarks>
    /// Would have failed before the change with 8 reads for 8 calls.
    /// </remarks>
    [TestMethod]
    public async Task RepeatedMonitoringCallsInsideTheReuseWindowShareOneAcquisition()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());

        int baseline = rig.Telemetry.ControlReads;

        // Eight polls at the interface's own 500 ms cadence, inside a 1500 ms window.
        for (int i = 0; i < 8; i++)
        {
            _ = runtime.GetTelemetrySample();
            rig.Clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        int reads = rig.Telemetry.ControlReads - baseline;
        Assert.IsTrue(
            reads is 1,
            $"Eight polls spanning 800 ms must share one acquisition inside the " +
            $"{BladeRuntime.MonitoringSampleReuseWindow.TotalMilliseconds:N0} ms reuse " +
            $"window; the hardware was read {reads} times.");
    }

    /// <summary>
    /// The window bounds the acquisition rate; it does not stop acquisition.
    /// </summary>
    [TestMethod]
    public async Task MonitoringReadsTheHardwareAgainOnceTheWindowHasElapsed()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());

        int baseline = rig.Telemetry.ControlReads;

        _ = runtime.GetTelemetrySample();
        rig.Clock.Advance(BladeRuntime.MonitoringSampleReuseWindow + TimeSpan.FromMilliseconds(1));
        _ = runtime.GetTelemetrySample();

        Assert.AreEqual(
            2,
            rig.Telemetry.ControlReads - baseline,
            "A sample older than the reuse window must be replaced, not served again.");
    }

    /// <summary>
    /// A reused sample is the same reading, not a fabricated or re-stamped one.
    /// </summary>
    /// <remarks>
    /// The age shown to a user is derived from the sample's own timestamp, so re-stamping a
    /// cached sample would make a 1.4 s old reading claim to be current — the same class of
    /// untruth as the "Live" badge fixed in v0.1.3, reintroduced one layer down.
    /// </remarks>
    [TestMethod]
    public async Task AReusedSampleKeepsItsOriginalTimestamp()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());

        ThermalTelemetrySample first = runtime.GetTelemetrySample();
        rig.Clock.Advance(TimeSpan.FromMilliseconds(900));
        ThermalTelemetrySample second = runtime.GetTelemetrySample();

        Assert.AreEqual(
            first.Timestamp,
            second.Timestamp,
            "A reused sample must carry the instant it was actually taken, so its age stays " +
            "honest to whoever displays it.");
    }

    /// <summary>
    /// The reuse window must stay well inside the interface's staleness budget.
    /// </summary>
    /// <remarks>
    /// <para>The interface treats telemetry as stale at 3 s and polls at 500 ms. A sample
    /// served at the very end of the reuse window is displayed for up to one further poll
    /// interval, so the window plus a poll interval must remain below the staleness threshold
    /// or the runtime would be manufacturing "Stale" out of correctly served data.</para>
    /// <para>Asserted numerically here rather than against the interface's constant because
    /// BladeControl.Runtime cannot reference BladeControl.UI — the dependency runs the other
    /// way, and must. If the staleness threshold ever moves, this test is the tripwire.</para>
    /// </remarks>
    [TestMethod]
    public void TheReuseWindowStaysInsideTheInterfaceStalenessBudget()
    {
        TimeSpan interfaceStaleThreshold = TimeSpan.FromSeconds(3);
        TimeSpan interfacePollInterval = TimeSpan.FromMilliseconds(500);

        Assert.IsTrue(
            BladeRuntime.MonitoringSampleReuseWindow + interfacePollInterval <
                interfaceStaleThreshold,
            $"A sample served at the end of the reuse window " +
            $"({BladeRuntime.MonitoringSampleReuseWindow.TotalMilliseconds:N0} ms) and held " +
            $"for one poll interval must still be fresh against the " +
            $"{interfaceStaleThreshold.TotalSeconds:N0} s staleness threshold.");
    }
}
