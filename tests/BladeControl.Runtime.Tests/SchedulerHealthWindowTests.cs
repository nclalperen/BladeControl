using BladeControl.Runtime;

namespace BladeControl.Runtime.Tests;

/// <summary>
/// Scheduler health describes recent behaviour, not the whole session's history.
/// </summary>
/// <remarks>
/// <para>Health used to be derived from cumulative counts, so one slow cycle early in a session
/// left the runtime reading "Degraded" for as long as it ran. That is a true statement about the
/// session and a useless one about the machine: an operator asking "is it coping now" got an
/// answer about an hour ago.</para>
/// <para>The verdict now comes from slow cycles inside the rolling window, with the lifetime
/// totals reported alongside it — "none recently, 13 this session" says more than either half.</para>
/// </remarks>
[TestClass]
public sealed class SchedulerHealthWindowTests
{
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(500);

    [TestMethod]
    public void RecentCountsOnlyTheObservationsStillInTheWindow()
    {
        var window = new RollingDurationWindow(capacity: 4);
        window.Record(TimeSpan.FromMilliseconds(900));
        Assert.AreEqual(1, window.CountAbove(Period));

        // Four fast cycles push the slow one out.
        for (int index = 0; index < 4; index++)
        {
            window.Record(TimeSpan.FromMilliseconds(100));
        }

        Assert.AreEqual(
            0,
            window.CountAbove(Period),
            "The slow observation has rolled out; recent behaviour is clean.");
        Assert.AreEqual(
            900,
            window.Maximum.TotalMilliseconds,
            "The lifetime maximum is kept separately and does not roll away.");
    }

    /// <summary>
    /// A session that was briefly slow and then recovered reports recent health as clean while
    /// still owning up to its history.
    /// </summary>
    [TestMethod]
    public async Task OneEarlySlowCycleDoesNotPoisonHealthForever()
    {
        var clock = new VirtualRuntimeClock();
        var scheduler = new DeadlineScheduler(clock, Period, windowCapacity: 8);
        long sequence = 0;

        await scheduler.RunAsync(
            (_, _) =>
            {
                clock.Advance(++sequence == 1
                    ? TimeSpan.FromMilliseconds(900)
                    : TimeSpan.FromMilliseconds(50));
                return ValueTask.FromResult(true);
            },
            CancellationToken.None,
            maximumCycles: 20);

        Assert.AreEqual(
            1,
            scheduler.Metrics.SlowCycleCount,
            "The session still remembers that one cycle was slow.");
        Assert.AreEqual(
            0,
            scheduler.Metrics.RecentSlowCycleCount,
            "But nothing recent was slow, which is what health should reflect.");
        Assert.AreEqual(8, scheduler.Metrics.RecentWindowSize);
    }

    [TestMethod]
    public async Task SustainedSlownessIsStillReportedAsRecent()
    {
        var clock = new VirtualRuntimeClock();
        var scheduler = new DeadlineScheduler(clock, Period, windowCapacity: 8);

        await scheduler.RunAsync(
            (_, _) =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(900));
                return ValueTask.FromResult(true);
            },
            CancellationToken.None,
            maximumCycles: 12);

        Assert.AreEqual(
            8,
            scheduler.Metrics.RecentSlowCycleCount,
            "Every cycle in the window was slow, so the window says so.");
    }

    [TestMethod]
    public void AFreshSchedulerReportsNoWindowAtAll()
    {
        var scheduler = new DeadlineScheduler(new VirtualRuntimeClock(), Period);

        Assert.AreEqual(0, scheduler.Metrics.CompletedCycles);
        Assert.AreEqual(
            0,
            scheduler.Metrics.RecentWindowSize,
            "No cycles means no window to draw a verdict from.");
    }
}
