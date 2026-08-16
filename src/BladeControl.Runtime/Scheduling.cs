using System.Diagnostics;

namespace BladeControl.Runtime;

public interface IRuntimeClock
{
    DateTimeOffset UtcNow { get; }

    TimeSpan MonotonicNow { get; }

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemRuntimeClock : IRuntimeClock
{
    private readonly long _origin = Stopwatch.GetTimestamp();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public TimeSpan MonotonicNow => Stopwatch.GetElapsedTime(_origin);

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero
            ? ValueTask.CompletedTask
            : new ValueTask(Task.Delay(delay, cancellationToken));
}

public sealed record SchedulerCycle(
    long Sequence,
    TimeSpan Deadline,
    TimeSpan Start,
    TimeSpan ActualStartToStart,
    TimeSpan DeadlineLateness);

public sealed record SchedulerMetrics(
    TimeSpan RequestedPeriod,
    long CompletedCycles,
    TimeSpan ActualStartToStart,
    TimeSpan CycleExecutionDuration,
    TimeSpan DeadlineLateness,
    long OverrunCount,
    TimeSpan MaximumOverrun,
    long SkippedDeadlines);

public sealed class DeadlineScheduler
{
    private readonly IRuntimeClock _clock;
    private readonly TimeSpan _period;
    private SchedulerMetrics _metrics;

    public DeadlineScheduler(IRuntimeClock clock, TimeSpan period)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        _period = period;
        _metrics = EmptyMetrics(period);
    }

    public SchedulerMetrics Metrics => _metrics;

    public event Action<SchedulerCycle, TimeSpan>? CycleOverrun;

    public async ValueTask RunAsync(
        Func<SchedulerCycle, CancellationToken, ValueTask<bool>> cycle,
        CancellationToken cancellationToken,
        long? maximumCycles = null)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        if (maximumCycles is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCycles));
        }

        TimeSpan deadline = _clock.MonotonicNow;
        TimeSpan? previousStart = null;
        long sequence = 0;
        while (!cancellationToken.IsCancellationRequested &&
               (!maximumCycles.HasValue || sequence < maximumCycles.Value))
        {
            TimeSpan wait = deadline - _clock.MonotonicNow;
            if (wait > TimeSpan.Zero)
            {
                await _clock.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
            }

            TimeSpan start = _clock.MonotonicNow;
            TimeSpan actualPeriod = previousStart.HasValue
                ? start - previousStart.Value
                : TimeSpan.Zero;
            TimeSpan lateness = Positive(start - deadline);
            var context = new SchedulerCycle(
                ++sequence,
                deadline,
                start,
                actualPeriod,
                lateness);
            bool continueRunning = await cycle(context, cancellationToken).ConfigureAwait(false);
            TimeSpan end = _clock.MonotonicNow;
            TimeSpan duration = end - start;
            TimeSpan nextDeadline = deadline + _period;
            TimeSpan overrun = Positive(end - nextDeadline);
            _metrics = new SchedulerMetrics(
                _period,
                sequence,
                actualPeriod,
                duration,
                lateness,
                _metrics.OverrunCount + (overrun > TimeSpan.Zero ? 1 : 0),
                overrun > _metrics.MaximumOverrun ? overrun : _metrics.MaximumOverrun,
                0);
            if (overrun > TimeSpan.Zero)
            {
                CycleOverrun?.Invoke(context, overrun);
            }

            previousStart = start;
            deadline = nextDeadline;
            if (!continueRunning)
            {
                break;
            }
        }
    }

    private static SchedulerMetrics EmptyMetrics(TimeSpan period) => new(
        period,
        0,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        0,
        TimeSpan.Zero,
        0);

    private static TimeSpan Positive(TimeSpan value) =>
        value > TimeSpan.Zero ? value : TimeSpan.Zero;
}
