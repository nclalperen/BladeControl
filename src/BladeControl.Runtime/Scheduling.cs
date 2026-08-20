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

/// <summary>
/// A fixed-capacity window of durations, kept sorted only when asked.
/// </summary>
/// <remarks>
/// <para>Percentiles need a sample, but the 500 ms control path cannot afford to allocate one.
/// This keeps a pre-allocated ring of the most recent observations and sorts a stack-allocated
/// copy only when a percentile is actually read — which happens on a diagnostics request, never
/// on the control path.</para>
/// <para>A rolling window rather than an all-time reservoir: for qualification we want what the
/// machine is doing now, not an average diluted by the first minutes after a start.</para>
/// </remarks>
public sealed class RollingDurationWindow
{
    private readonly long[] _ticks;
    private int _count;
    private int _next;

    public RollingDurationWindow(int capacity = 256)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _ticks = new long[capacity];
    }

    public int Capacity => _ticks.Length;

    public int Count => _count;

    public TimeSpan Latest { get; private set; }

    public TimeSpan Maximum { get; private set; }

    /// <summary>Records one observation. Allocation-free and O(1).</summary>
    public void Record(TimeSpan value)
    {
        Latest = value;
        if (value > Maximum)
        {
            Maximum = value;
        }

        _ticks[_next] = value.Ticks;
        _next = (_next + 1) % _ticks.Length;
        if (_count < _ticks.Length)
        {
            _count++;
        }
    }

    /// <summary>
    /// The nearest-rank percentile of the current window.
    /// </summary>
    /// <remarks>
    /// Nearest-rank, not interpolated: with a bounded window of integral tick counts an exact
    /// order statistic is both cheaper and easier to assert than an interpolated one, and no
    /// decision depends on sub-sample precision.
    /// </remarks>
    public TimeSpan Percentile(double percentile)
    {
        if (percentile is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile));
        }

        if (_count == 0)
        {
            return TimeSpan.Zero;
        }

        Span<long> sorted = _count <= 256 ? stackalloc long[_count] : new long[_count];
        _ticks.AsSpan(0, _count).CopyTo(sorted);
        sorted.Sort();
        int rank = (int)Math.Ceiling(percentile / 100d * _count);
        return TimeSpan.FromTicks(sorted[Math.Clamp(rank - 1, 0, _count - 1)]);
    }

    public void Reset()
    {
        _count = 0;
        _next = 0;
        Latest = TimeSpan.Zero;
        Maximum = TimeSpan.Zero;
    }
}

/// <summary>Latest, maximum and rolling percentiles for one measured duration.</summary>
public sealed record DurationStatistics(
    TimeSpan Latest,
    TimeSpan Maximum,
    TimeSpan P95,
    TimeSpan P99,
    int SampleCount)
{
    public static DurationStatistics Empty { get; } = new(
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        0);

    public static DurationStatistics From(RollingDurationWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return new DurationStatistics(
            window.Latest,
            window.Maximum,
            window.Percentile(95),
            window.Percentile(99),
            window.Count);
    }
}

/// <summary>
/// What the scheduler observed, with each field meaning exactly what its name says.
/// </summary>
/// <param name="LatestStartToStart">
/// The interval between the two most recent cycle starts. Latest only — it was previously
/// named <c>ActualStartToStart</c>, which read as a running average it never was.
/// </param>
/// <param name="LatestCycleExecutionDuration">How long the most recent cycle body took.</param>
/// <param name="LatestDeadlineLateness">
/// How late the most recent cycle started relative to its own deadline.
/// </param>
/// <param name="SlowCycleCount">
/// Cycles whose <i>own</i> execution exceeded the period. This is the count that identifies a
/// cause.
/// </param>
/// <param name="CatchUpCycleCount">
/// Cycles that began late <i>because the previous cycle was still running when their deadline
/// passed</i>. These are the recovery tail, not independent faults: one slow cycle typically
/// produces several. Counting them together with slow cycles is what made 136 "overruns" look
/// far worse than the ~35 events behind them.
/// </param>
/// <remarks>
/// The predecessor test matters. Defining a catch-up cycle as merely "started late" counted
/// ordinary timer jitter: a live 479-cycle session reported 296 catch-up cycles while its
/// worst lateness was 28.3 ms and it lost no whole period. Windows timer granularity makes
/// almost every cycle start a millisecond or two late, so that definition measured the clock,
/// not the workload — the same conflation the old single overrun counter made.
/// </remarks>
/// <param name="MissedDeadlinePeriods">
/// How many whole period boundaries elapsed while the loop was running late, summed. Answers
/// "how much schedule was lost", which neither count above does.
/// </param>
/// <param name="CycleExecution">
/// Rolling latest/max/p95/p99 for cycle execution. Nullable because a deserialised status from
/// a runtime older than this field carries none, and a diagnostic tool must degrade rather than
/// crash during exactly the upgrade window where it is most needed.
/// </param>
/// <param name="SkippedDeadlines">
/// Always zero. The loop never skips an iteration: deadlines advance absolutely and a late loop
/// runs back to back until it catches up. Retained as an explicitly defined zero rather than
/// removed, because a diagnostics consumer reading a missing field as "unknown" would be worse
/// than reading a defined "never happens".
/// </param>
public sealed record SchedulerMetrics(
    TimeSpan RequestedPeriod,
    long CompletedCycles,
    TimeSpan LatestStartToStart,
    TimeSpan LatestCycleExecutionDuration,
    TimeSpan LatestDeadlineLateness,
    long SlowCycleCount,
    long CatchUpCycleCount,
    long MissedDeadlinePeriods,
    TimeSpan MaximumCycleExecutionDuration,
    TimeSpan MaximumDeadlineLateness,
    DurationStatistics? CycleExecution,
    long SkippedDeadlines = 0)
{
    /// <summary>
    /// A zeroed set for the requested period, to be refined with <c>with</c>.
    /// </summary>
    /// <remarks>
    /// Exists so a fixture can state the one or two fields it cares about instead of restating
    /// every positional argument, which is how call sites end up silently wrong when a field is
    /// added in the middle.
    /// </remarks>
    public static SchedulerMetrics Idle(TimeSpan period) => new(
        period,
        0,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        0,
        0,
        0,
        TimeSpan.Zero,
        TimeSpan.Zero,
        DurationStatistics.Empty);
}

public sealed class DeadlineScheduler
{
    private readonly IRuntimeClock _clock;
    private readonly TimeSpan _period;
    private readonly RollingDurationWindow _cycleExecution;
    private SchedulerMetrics _metrics;

    public DeadlineScheduler(IRuntimeClock clock, TimeSpan period, int windowCapacity = 256)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        _period = period;
        _cycleExecution = new RollingDurationWindow(windowCapacity);
        _metrics = EmptyMetrics(period);
    }

    public SchedulerMetrics Metrics => _metrics;

    /// <summary>Raised when a cycle's own execution exceeds the period.</summary>
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
        TimeSpan? previousEnd = null;
        long sequence = 0;
        long slowCycles = 0;
        long catchUpCycles = 0;
        long missedPeriods = 0;
        TimeSpan maximumExecution = TimeSpan.Zero;
        TimeSpan maximumLateness = TimeSpan.Zero;
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

            // A cycle is slow when its own body outran the period. A cycle is a catch-up when
            // it merely started late because something earlier did. Conflating the two turns
            // one slow cycle into a handful of reported faults and hides how many events there
            // really were.
            bool slow = duration > _period;

            // Late *because the predecessor was still running*, not merely late. Timer
            // granularity alone makes nearly every start a millisecond or two late; only a
            // cycle whose deadline passed while the previous one was still executing is
            // recovering from an overrun.
            bool caughtUp = !slow && previousEnd.HasValue && previousEnd.Value > deadline;
            if (slow)
            {
                slowCycles++;
            }

            if (caughtUp)
            {
                catchUpCycles++;
            }

            missedPeriods += (long)(lateness.Ticks / _period.Ticks);
            _cycleExecution.Record(duration);
            if (duration > maximumExecution)
            {
                maximumExecution = duration;
            }

            if (lateness > maximumLateness)
            {
                maximumLateness = lateness;
            }

            _metrics = new SchedulerMetrics(
                _period,
                sequence,
                actualPeriod,
                duration,
                lateness,
                slowCycles,
                catchUpCycles,
                missedPeriods,
                maximumExecution,
                maximumLateness,
                DurationStatistics.From(_cycleExecution));
            if (slow)
            {
                CycleOverrun?.Invoke(context, duration - _period);
            }

            previousStart = start;
            previousEnd = end;

            // Absolute advance: the schedule never rebases to "now", so a late loop catches up
            // rather than drifting. That is deliberate and unchanged.
            deadline += _period;
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
        0,
        0,
        TimeSpan.Zero,
        TimeSpan.Zero,
        DurationStatistics.Empty);

    private static TimeSpan Positive(TimeSpan value) =>
        value > TimeSpan.Zero ? value : TimeSpan.Zero;
}
