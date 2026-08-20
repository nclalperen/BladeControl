using BladeControl.Runtime;

namespace BladeControl.Runtime.Tests;

[TestClass]
public sealed class DeadlineSchedulerTests
{
    [TestMethod]
    public async Task SeventyMillisecondsWorkMaintainsFiveHundredMillisecondStarts()
    {
        var clock = new VirtualRuntimeClock();
        var scheduler = new DeadlineScheduler(clock, TimeSpan.FromMilliseconds(500));
        var starts = new List<TimeSpan>();

        await scheduler.RunAsync(
            (cycle, _) =>
            {
                starts.Add(cycle.Start);
                clock.Advance(TimeSpan.FromMilliseconds(70));
                return ValueTask.FromResult(true);
            },
            CancellationToken.None,
            maximumCycles: 4);

        CollectionAssert.AreEqual(
            new[] { 0d, 500d, 1000d, 1500d },
            starts.Select(value => value.TotalMilliseconds).ToArray());
        Assert.AreEqual(500, scheduler.Metrics.LatestStartToStart.TotalMilliseconds);
        Assert.AreEqual(0, scheduler.Metrics.SlowCycleCount);
        Console.WriteLine(
            $"70 ms work: starts={string.Join(',', starts.Select(value => value.TotalMilliseconds))}; " +
            $"last period={scheduler.Metrics.LatestStartToStart.TotalMilliseconds} ms");
    }

    [TestMethod]
    public async Task SixHundredMillisecondsWorkNeverOverlapsAndRecordsOverruns()
    {
        var clock = new VirtualRuntimeClock();
        var scheduler = new DeadlineScheduler(clock, TimeSpan.FromMilliseconds(500));
        var starts = new List<TimeSpan>();
        var overruns = new List<TimeSpan>();
        scheduler.CycleOverrun += (_, overrun) => overruns.Add(overrun);

        await scheduler.RunAsync(
            (cycle, _) =>
            {
                starts.Add(cycle.Start);
                clock.Advance(TimeSpan.FromMilliseconds(600));
                return ValueTask.FromResult(true);
            },
            CancellationToken.None,
            maximumCycles: 3);

        CollectionAssert.AreEqual(
            new[] { 0d, 600d, 1200d },
            starts.Select(value => value.TotalMilliseconds).ToArray());
        Assert.AreEqual(3, scheduler.Metrics.SlowCycleCount);

        // The maximum is now the cycle's own execution time, not its distance past the next
        // deadline. Each body took 600 ms; how far that pushed the following deadline is
        // reported separately, as lateness.
        Assert.AreEqual(600, scheduler.Metrics.MaximumCycleExecutionDuration.TotalMilliseconds);
        Assert.AreEqual(
            0,
            scheduler.Metrics.CatchUpCycleCount,
            "Every cycle here was slow in its own right; none was merely recovering.");
        Assert.AreEqual(0, scheduler.Metrics.SkippedDeadlines);
        Assert.AreEqual(3, overruns.Count);
        Console.WriteLine(
            $"600 ms work: starts={string.Join(',', starts.Select(value => value.TotalMilliseconds))}; " +
            $"overruns={scheduler.Metrics.SlowCycleCount}; " +
            $"max={scheduler.Metrics.MaximumCycleExecutionDuration.TotalMilliseconds} ms");
    }
}
