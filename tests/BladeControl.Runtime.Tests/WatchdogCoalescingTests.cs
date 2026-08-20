using System.Diagnostics;
using BladeControl.Razer;
using BladeControl.Runtime;

namespace BladeControl.Runtime.Tests;

/// <summary>
/// A fan write's own ownership read can answer a watchdog deadline that falls in the same
/// cycle.
/// </summary>
/// <remarks>
/// <para>A fan target change verifies ownership as its last act, reading exactly the 0x0D82
/// pair the watchdog needs. Issuing a second pair microseconds later costs two more HID
/// exchanges on the cycle that is already the most expensive one — the cycle that was producing
/// the observed deadline overruns.</para>
/// <para>What the coalescing must never do: infer ownership from the write having succeeded,
/// reuse the pre-write observation, or accept a reading whose measured age exceeds the bound.
/// The observation carries the timestamp of its own second 0x0D82 response, so its age is
/// measured rather than assumed, and anything that does not qualify falls through to a normal
/// read.</para>
/// <para>Most of these are A/B comparisons: two runs identical but for the one variable under
/// test. Counting reads in absolute terms would depend on how often the curve happens to ask
/// for a new target, which is not what any of these tests is about.</para>
/// </remarks>
[TestClass]
public sealed class WatchdogCoalescingTests
{
    /// <summary>Small enough that the watchdog is due on every cycle after the first.</summary>
    private static readonly TimeSpan AlwaysDue = TimeSpan.FromTicks(1);

    private const int Cycles = 12;

    /// <summary>
    /// A fresh observation displaces watchdog reads; a stale one cannot.
    /// </summary>
    [TestMethod]
    public async Task FreshObservationsDisplaceWatchdogReadsAndStaleOnesDoNot()
    {
        int freshReads = await CountWatchdogReadsAsync(TimeSpan.Zero);
        int staleReads = await CountWatchdogReadsAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(
            staleReads > freshReads,
            $"A stale observation must fall through to a real read " +
            $"(fresh {freshReads}, stale {staleReads}).");
    }

    /// <summary>
    /// The bound is measured against the observation's own timestamp, so an observation older
    /// than a control period is refused however correct its content.
    /// </summary>
    [TestMethod]
    public async Task ObservationOlderThanAControlPeriodIsRefused()
    {
        int justOverAPeriod = await CountWatchdogReadsAsync(TimeSpan.FromMilliseconds(600));
        int stale = await CountWatchdogReadsAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(
            stale,
            justOverAPeriod,
            "Anything past the freshness bound is refused; degrees of staleness do not matter.");
    }

    /// <summary>
    /// A run that never writes has no observation to reuse, so every due watchdog reads
    /// firmware — the behaviour that existed before coalescing.
    /// </summary>
    [TestMethod]
    public async Task RunsWithoutWritesAlwaysReadTheWatchdog()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime(watchdogInterval: AlwaysDue);
        runtime.StartThermalControl();

        // Steady and cool: the curve asks for nothing new after the baseline.
        rig.Telemetry.FixedCpuTemperature = 45;
        int writesBefore = rig.Hardware.FanWrites;
        int readsBefore = rig.Hardware.ModeReads;

        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: Cycles);

        int writes = rig.Hardware.FanWrites - writesBefore;
        int reads = rig.Hardware.ModeReads - readsBefore;

        // A settling cycle or two may still write; what matters is that the watchdog kept
        // reading on its own for the cycles that did not.
        Assert.IsTrue(
            reads > writes,
            $"With no observation to reuse the watchdog reads firmware itself " +
            $"({reads} reads against {writes} writes).");
    }

    /// <summary>
    /// The observation is cleared at the top of every cycle, so one taken earlier can never
    /// answer a later deadline.
    /// </summary>
    [TestMethod]
    public async Task ObservationDoesNotCarryOverBetweenCycles()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime(watchdogInterval: AlwaysDue);
        runtime.StartThermalControl();

        // Force writes for a while, then go quiet. If an observation carried over, the quiet
        // stretch would stop reading the watchdog.
        RampTemperature(rig);
        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: Cycles);

        rig.Telemetry.BeforeRead = null;
        rig.Telemetry.FixedCpuTemperature = 45;
        int readsBefore = rig.Hardware.ModeReads;

        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: Cycles);

        Assert.IsTrue(
            rig.Hardware.ModeReads > readsBefore,
            "Quiet cycles must read the watchdog even after a run of writing ones.");
    }

    /// <summary>
    /// Coalescing changes which read answers the deadline, never what the answer means.
    /// Ownership loss is still detected and still ends the session.
    /// </summary>
    [TestMethod]
    public async Task CoalescedObservationStillDetectsOwnershipLoss()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime(watchdogInterval: AlwaysDue);
        runtime.StartThermalControl();

        // Every write reports back that firmware has taken the fans to Auto.
        rig.Hardware.OwnershipObservationOverride = new RazerOwnershipObservation(
            RazerPerformanceMode.Balanced,
            RazerFanMode.Auto,
            RazerPerformanceMode.Balanced,
            RazerFanMode.Auto,
            Stopwatch.GetTimestamp(),
            []);
        RampTemperature(rig);

        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: Cycles);

        Assert.AreNotEqual(
            RuntimeState.Running,
            runtime.State,
            "External ownership change is detected whichever read observed it.");
    }

    /// <summary>
    /// A watchdog that is not yet due does not read, whether or not a write just happened.
    /// Coalescing answers deadlines; it does not move them.
    /// </summary>
    [TestMethod]
    public async Task CoalescingDoesNotAdvanceTheWatchdogSchedule()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime(
            watchdogInterval: TimeSpan.FromMinutes(5));
        runtime.StartThermalControl();
        RampTemperature(rig);
        int readsBefore = rig.Hardware.ModeReads;

        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: Cycles);

        Assert.IsTrue(rig.Hardware.FanWrites > 0, "Writes happened.");
        Assert.AreEqual(
            readsBefore,
            rig.Hardware.ModeReads,
            "A watchdog that is not due does not read, coalescing or otherwise.");
    }

    /// <summary>Runs an identical writing workload and reports how often the watchdog read.</summary>
    private static async Task<int> CountWatchdogReadsAsync(TimeSpan observationAge)
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime(watchdogInterval: AlwaysDue);
        runtime.StartThermalControl();
        rig.Hardware.OwnershipObservationAge = observationAge;
        RampTemperature(rig);
        int readsBefore = rig.Hardware.ModeReads;

        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: Cycles);

        return rig.Hardware.ModeReads - readsBefore;
    }

    /// <summary>
    /// Moves the temperature every sample so the curve keeps asking for a new target, which is
    /// what puts a write and a watchdog deadline in the same cycle.
    /// </summary>
    private static void RampTemperature(RuntimeLifecycleTests.RuntimeRig rig) =>
        rig.Telemetry.BeforeRead = sample =>
            rig.Telemetry.FixedCpuTemperature = 50 + (sample % 30);
}
