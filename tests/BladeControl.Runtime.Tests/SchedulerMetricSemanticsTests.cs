using BladeControl.Runtime;

namespace BladeControl.Runtime.Tests;

/// <summary>
/// Each scheduler metric means exactly what its name says.
/// </summary>
/// <remarks>
/// <para>A live session reported 136 "overruns" across 791 cycles. That number came from a
/// single counter that incremented both when a cycle's own body outran the period and when a
/// cycle merely started late because an earlier one had. One slow cycle therefore reported as
/// several faults, and its own catch-up tail — 376.9, 254.9, 129.9, 4.7 ms — was counted as
/// four more.</para>
/// <para>The counts are now separated at the source: a cause is a slow cycle, a consequence is
/// a catch-up cycle. <c>SkippedDeadlines</c> was never computed at all and is now explicitly
/// defined as always zero, because the loop genuinely never skips an iteration.</para>
/// </remarks>
[TestClass]
public sealed class SchedulerMetricSemanticsTests
{
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// One slow cycle, several late starts behind it: one cause, not five faults.
    /// </summary>
    [TestMethod]
    public async Task OneSlowCycleIsCountedOnceAndItsTailSeparately()
    {
        var clock = new VirtualRuntimeClock();
        var scheduler = new DeadlineScheduler(clock, Period);
        long sequence = 0;

        await scheduler.RunAsync(
            (_, _) =>
            {
                // Cycle 1 runs long; the rest are fast and merely catching up.
                clock.Advance(++sequence == 1
                    ? TimeSpan.FromMilliseconds(1700)
                    : TimeSpan.FromMilliseconds(10));
                return ValueTask.FromResult(true);
            },
            CancellationToken.None,
            maximumCycles: 5);

        Assert.AreEqual(1, scheduler.Metrics.SlowCycleCount, "Exactly one cycle was slow.");
        Assert.AreEqual(
            3,
            scheduler.Metrics.CatchUpCycleCount,
            "The recovery tail is reported as recovery, not as three more faults.");
        Assert.AreEqual(5, scheduler.Metrics.CompletedCycles);
    }

    [TestMethod]
    public async Task APunctualRunReportsNeitherSlowNorCatchUpCycles()
    {
        var clock = new VirtualRuntimeClock();
        var scheduler = new DeadlineScheduler(clock, Period);

        await scheduler.RunAsync(
            (_, _) =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(50));
                return ValueTask.FromResult(true);
            },
            CancellationToken.None,
            maximumCycles: 6);

        Assert.AreEqual(0, scheduler.Metrics.SlowCycleCount);
        Assert.AreEqual(0, scheduler.Metrics.CatchUpCycleCount);
        Assert.AreEqual(0, scheduler.Metrics.MissedDeadlinePeriods);
    }

    /// <summary>
    /// Whole period boundaries lost while running late — a different question from how many
    /// cycles were involved.
    /// </summary>
    [TestMethod]
    public async Task MissedDeadlinePeriodsCountsWholePeriodsLost()
    {
        var clock = new VirtualRuntimeClock();
        var scheduler = new DeadlineScheduler(clock, Period);
        long sequence = 0;

        await scheduler.RunAsync(
            (_, _) =>
            {
                clock.Advance(++sequence == 1
                    ? TimeSpan.FromMilliseconds(1700)
                    : TimeSpan.FromMilliseconds(10));
                return ValueTask.FromResult(true);
            },
            CancellationToken.None,
            maximumCycles: 4);

        // Cycle 2 starts 1200 ms late (2 periods), cycle 3 710 ms (1), cycle 4 220 ms (0).
        Assert.AreEqual(3, scheduler.Metrics.MissedDeadlinePeriods);
    }

    /// <summary>
    /// The loop never skips an iteration: deadlines advance absolutely and a late loop runs
    /// back to back until it catches up. The field is a defined zero, not an unmeasured one.
    /// </summary>
    [TestMethod]
    public async Task EveryDeadlineRunsAndNoneIsSkipped()
    {
        var clock = new VirtualRuntimeClock();
        var scheduler = new DeadlineScheduler(clock, Period);
        var starts = new List<TimeSpan>();

        await scheduler.RunAsync(
            (cycle, _) =>
            {
                starts.Add(cycle.Start);
                clock.Advance(TimeSpan.FromMilliseconds(1200));
                return ValueTask.FromResult(true);
            },
            CancellationToken.None,
            maximumCycles: 4);

        Assert.AreEqual(4, starts.Count, "Every scheduled iteration ran.");
        Assert.AreEqual(4, scheduler.Metrics.CompletedCycles);
        Assert.AreEqual(0, scheduler.Metrics.SkippedDeadlines);
    }

    // --- Latest really means latest, max really retains the maximum -------------------------

    [TestMethod]
    public async Task LatestFieldsReportTheMostRecentCycleOnly()
    {
        var clock = new VirtualRuntimeClock();
        var scheduler = new DeadlineScheduler(clock, Period);
        long sequence = 0;

        await scheduler.RunAsync(
            (_, _) =>
            {
                clock.Advance(++sequence == 1
                    ? TimeSpan.FromMilliseconds(900)
                    : TimeSpan.FromMilliseconds(30));
                return ValueTask.FromResult(true);
            },
            CancellationToken.None,
            maximumCycles: 3);

        Assert.AreEqual(
            30,
            scheduler.Metrics.LatestCycleExecutionDuration.TotalMilliseconds,
            "The last cycle took 30 ms, whatever earlier ones did.");
        Assert.AreEqual(
            900,
            scheduler.Metrics.MaximumCycleExecutionDuration.TotalMilliseconds,
            "The maximum survives the cycles that followed it.");
    }

    [TestMethod]
    public async Task MaximumLatenessIsRetainedAcrossRecovery()
    {
        var clock = new VirtualRuntimeClock();
        var scheduler = new DeadlineScheduler(clock, Period);
        long sequence = 0;

        await scheduler.RunAsync(
            (_, _) =>
            {
                clock.Advance(++sequence == 1
                    ? TimeSpan.FromMilliseconds(1300)
                    : TimeSpan.FromMilliseconds(10));
                return ValueTask.FromResult(true);
            },
            CancellationToken.None,
            maximumCycles: 4);

        Assert.AreEqual(800, scheduler.Metrics.MaximumDeadlineLateness.TotalMilliseconds);
        Assert.AreEqual(
            0,
            scheduler.Metrics.LatestDeadlineLateness.TotalMilliseconds,
            "By the last cycle the loop had caught up.");
    }

    // --- Rolling window behaviour ------------------------------------------------------------

    [TestMethod]
    public void PercentilesAreDeterministicNearestRank()
    {
        var window = new RollingDurationWindow(capacity: 100);
        for (int value = 1; value <= 100; value++)
        {
            window.Record(TimeSpan.FromMilliseconds(value));
        }

        Assert.AreEqual(95, window.Percentile(95).TotalMilliseconds);
        Assert.AreEqual(99, window.Percentile(99).TotalMilliseconds);
        Assert.AreEqual(100, window.Percentile(100).TotalMilliseconds);
        Assert.AreEqual(1, window.Percentile(1).TotalMilliseconds);
    }

    /// <summary>Order of arrival must not change the answer.</summary>
    [TestMethod]
    public void PercentilesDoNotDependOnInsertionOrder()
    {
        var ascending = new RollingDurationWindow(capacity: 50);
        var descending = new RollingDurationWindow(capacity: 50);
        for (int value = 1; value <= 50; value++)
        {
            ascending.Record(TimeSpan.FromMilliseconds(value));
            descending.Record(TimeSpan.FromMilliseconds(51 - value));
        }

        Assert.AreEqual(ascending.Percentile(95), descending.Percentile(95));
        Assert.AreEqual(ascending.Percentile(99), descending.Percentile(99));
    }

    /// <summary>
    /// The window rolls: old observations leave, so percentiles describe recent behaviour
    /// rather than the whole session.
    /// </summary>
    [TestMethod]
    public void OldObservationsLeaveTheWindow()
    {
        var window = new RollingDurationWindow(capacity: 4);
        window.Record(TimeSpan.FromMilliseconds(1000));
        for (int index = 0; index < 4; index++)
        {
            window.Record(TimeSpan.FromMilliseconds(10));
        }

        Assert.AreEqual(4, window.Count);
        Assert.AreEqual(
            10,
            window.Percentile(100).TotalMilliseconds,
            "The 1000 ms observation has rolled out of the window.");
        Assert.AreEqual(
            1000,
            window.Maximum.TotalMilliseconds,
            "The all-time maximum is kept separately and does not roll away.");
    }

    [TestMethod]
    public void AnEmptyWindowReportsZeroRatherThanFailing()
    {
        var window = new RollingDurationWindow();

        Assert.AreEqual(0, window.Count);
        Assert.AreEqual(TimeSpan.Zero, window.Percentile(95));
        Assert.AreEqual(DurationStatistics.Empty, DurationStatistics.From(window));
    }

    [TestMethod]
    public void StatisticsCarryLatestMaximumAndPercentiles()
    {
        var window = new RollingDurationWindow(capacity: 10);
        window.Record(TimeSpan.FromMilliseconds(500));
        window.Record(TimeSpan.FromMilliseconds(20));

        DurationStatistics statistics = DurationStatistics.From(window);

        Assert.AreEqual(20, statistics.Latest.TotalMilliseconds);
        Assert.AreEqual(500, statistics.Maximum.TotalMilliseconds);
        Assert.AreEqual(2, statistics.SampleCount);
    }

    [TestMethod]
    public void SchedulerPopulatesTheCycleExecutionWindow()
    {
        var clock = new VirtualRuntimeClock();
        var scheduler = new DeadlineScheduler(clock, Period);

        _ = scheduler.RunAsync(
            (_, _) =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(40));
                return ValueTask.FromResult(true);
            },
            CancellationToken.None,
            maximumCycles: 3);

        Assert.AreEqual(3, scheduler.Metrics.CycleExecution.SampleCount);
        Assert.AreEqual(40, scheduler.Metrics.CycleExecution.P95.TotalMilliseconds);
    }
}
