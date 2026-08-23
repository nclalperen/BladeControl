using System.Reflection;
using System.Diagnostics;
using BladeControl.Razer;
using BladeControl.Telemetry;
using BladeControl.Thermal;

namespace BladeControl.Runtime;

public enum RuntimeState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    EmergencyHandoff,
    Faulted
}

public sealed record RuntimeStatus(
    RuntimeState State,
    Guid? SessionId,
    DateTimeOffset? StartTimestamp,
    string? CurrentProfile,
    ThermalMachineState? CapturedOriginalPerformanceState,
    int? CurrentEffectiveFanTargetRpm,
    ThermalTelemetrySample? LatestAuthoritativeTelemetry,
    TelemetryHealth? TelemetryHealth,
    SchedulerMetrics Scheduler,
    string SchedulerHealth,
    string RuntimeBuild,
    RuntimeRazerModeState? LastRazerWatchdogState,
    DateTimeOffset? LastRazerWatchdogObservedAt,
    string? LastFailureReason,
    string? LastStartRejectionReason,
    string? EmergencyStatus,
    TimeSpan LastTelemetryAcquisitionDuration,
    DurationStatistics TelemetryAcquisition,
    DurationStatistics ActuatorDuration,
    long WatchdogCoalescedCount,
    long TotalEventCount,
    int RetainedThermalDecisionCount,
    int RetainedThermalTraceCount,
    IReadOnlyList<RuntimeEvent> RecentEvents);

public sealed class RuntimeOwnershipException : Exception
{
    public RuntimeOwnershipException(string message)
        : base(message)
    {
    }
}

public sealed class BladeRuntime : IAsyncDisposable
{
    public static readonly TimeSpan DefaultControlPeriod = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan DefaultWatchdogInterval = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IControlTelemetryProvider _controlTelemetry;
    private readonly ITelemetryProvider _diagnosticTelemetry;
    private readonly IRuntimeHardwareController _hardware;
    private readonly IRuntimeOwnershipGate _ownershipGate;
    private readonly IRuntimeClock _clock;
    private readonly ThermalProfile _profile;
    private readonly TimeSpan _watchdogInterval;
    private readonly TimeSpan _controlPeriod;

    /// <summary>
    /// Rolling windows for the two costs that actually compete for the control period.
    /// </summary>
    /// <remarks>
    /// Recording is O(1) into a pre-allocated ring; percentiles are computed only when a
    /// diagnostics request reads them, never on the 500 ms path.
    /// </remarks>
    private readonly RollingDurationWindow _acquisitionWindow = new();
    private readonly RollingDurationWindow _actuatorWindow = new();
    private long _watchdogCoalescedCount;
    private readonly BoundedRuntimeEventLog _events;
    private readonly ControlTelemetryAdapter _telemetryAdapter;
    private IRuntimeOwnershipLease? _hostLease;
    private ThermalRuntimeController? _controller;
    private DeadlineScheduler _scheduler;
    private RuntimeState _state = RuntimeState.Stopped;
    private RuntimeRazerModeState? _lastWatchdog;

    /// <summary>
    /// When the last watchdog observation was actually taken.
    /// </summary>
    /// <remarks>
    /// An ownership observation without a time is an assertion about the present made from an
    /// unknown moment. A stopped runtime once presented a fan reading sixteen minutes old as
    /// current firmware state; a reader could not tell, because nothing carried when it was
    /// read.
    /// </remarks>
    private DateTimeOffset? _lastWatchdogAt;

    /// <summary>Why the last start attempt was refused, as distinct from a fault.</summary>
    private string? _lastRejection;

    /// <summary>
    /// The performance mode the running session qualified in, or null when none is running.
    /// </summary>
    /// <remarks>
    /// A session runs in whichever mode the user chose and preserves it, and the GPU thermal
    /// limits it operates under were derived from that mode's thermal anchor. The mode can
    /// still change from outside — a keyboard shortcut, vendor software — and the fan mode
    /// would be untouched, so nothing about ownership would look wrong while the ladder went
    /// on using limits for a mode the machine had left.
    /// </remarks>
    private RazerPerformanceMode? _sessionPerformanceMode;
    private Guid? _sessionId;
    private DateTimeOffset? _startTimestamp;
    private TimeSpan _nextWatchdog;
    private string? _lastFailure;
    private string? _emergencyStatus;
    private long _eventSequence;
    private int? _currentTarget;
    private string? _currentProfile;
    private ThermalMachineState? _standaloneManualOriginal;
    private ThermalSessionResult? _standaloneStopResult;
    private bool _standaloneShutdownAttempted;
    private bool _initialized;
    private bool _emergencyLatched;
    private bool _disposed;

    public BladeRuntime(
        IControlTelemetryProvider controlTelemetry,
        ITelemetryProvider diagnosticTelemetry,
        IRuntimeHardwareController hardware,
        IRuntimeOwnershipGate ownershipGate,
        IRuntimeClock? clock = null,
        ThermalProfile? profile = null,
        TimeSpan? controlPeriod = null,
        TimeSpan? watchdogInterval = null,
        int eventCapacity = 2048)
    {
        _controlTelemetry = controlTelemetry ??
            throw new ArgumentNullException(nameof(controlTelemetry));
        _diagnosticTelemetry = diagnosticTelemetry ??
            throw new ArgumentNullException(nameof(diagnosticTelemetry));
        _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        _ownershipGate = ownershipGate ?? throw new ArgumentNullException(nameof(ownershipGate));
        _clock = clock ?? new SystemRuntimeClock();
        _profile = profile ?? BuiltInThermalProfiles.Default;
        _watchdogInterval = watchdogInterval ?? DefaultWatchdogInterval;
        if (_watchdogInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(watchdogInterval));
        }

        _events = new BoundedRuntimeEventLog(eventCapacity);
        _telemetryAdapter = new ControlTelemetryAdapter(
            _controlTelemetry,
            _clock,
            OnTelemetrySample);
        _scheduler = new DeadlineScheduler(
            _clock,
            controlPeriod ?? DefaultControlPeriod);
        _controlPeriod = controlPeriod ?? DefaultControlPeriod;
        _scheduler.CycleOverrun += OnSchedulerOverrun;
        _hardware.ExchangeCompleted += OnExchangeCompleted;
    }

    public RuntimeState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public event Action<RuntimeEvent>? EventPublished;

    public bool InitializeHost()
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            if (_initialized)
            {
                return _state != RuntimeState.Faulted;
            }

            _hostLease = _ownershipGate.TryAcquire();
            if (_hostLease is null)
            {
                _lastFailure = "Another BladeControl runtime already owns the hardware session.";
                _state = RuntimeState.Faulted;
                throw new RuntimeOwnershipException(_lastFailure);
            }

            _initialized = true;
        }

        RuntimeRazerModeState startupState;
        try
        {
            startupState = _hardware.ReadModeState();
            _lastWatchdog = startupState;
            _lastWatchdogAt = _clock.UtcNow;
        }
        catch (Exception exception)
        {
            Fault($"Startup Razer mode read failed: {exception.Message}");
            return false;
        }

        if (startupState.IsKnownAuto)
        {
            return true;
        }

        // A crashed session strands the fans in whatever mode it was running in, which is now
        // the mode the user chose rather than always Balanced. Checking for Balanced + Manual
        // specifically would have walked straight past a session orphaned in Silent and called
        // it an unsafe startup state instead of recovering it.
        if (!startupState.IsOwnedManual)
        {
            Fault($"Startup firmware state is not a safe known Auto state: {startupState}.");
            return false;
        }

        AddEvent((sequence, timestamp) => new RecoveryAttemptEvent(
            sequence,
            timestamp,
            $"Potentially orphaned {startupState.Zone1PerformanceMode} + Manual mode detected; " +
            "attempting one recovery to firmware Auto."));
        ThermalControlOperationResult recovery;
        try
        {
            recovery = _hardware.ReturnToFirmwareAuto();
        }
        catch (Exception exception)
        {
            recovery = new ThermalControlOperationResult(
                false,
                true,
                true,
                false,
                exception.Message,
                null,
                []);
        }

        bool succeeded = recovery.Succeeded && recovery.FinalState?.IsKnownAuto == true;

        // Adopt the recovery's own readback as the watchdog observation. Until this was
        // added, _lastWatchdog kept the pre-recovery Balanced + Manual reading taken a few
        // exchanges earlier, and every diagnostic reported it: a machine the runtime had just
        // returned to firmware Auto was described as still held in Manual, which reads as the
        // recovery never having happened. The fresher observation existed all along and was
        // being discarded. Adopted whatever the outcome — a failed recovery's final state is
        // equally the most recent thing known about the hardware.
        if (recovery.FinalState is { } recovered)
        {
            _lastWatchdog = new RuntimeRazerModeState(
                recovered.Zone1PerformanceMode,
                recovered.Zone1FanMode,
                recovered.Zone2PerformanceMode,
                recovered.Zone2FanMode,
                recovered.Exchanges);
            _lastWatchdogAt = _clock.UtcNow;
        }

        string message = succeeded
            ? "ORPHANED MANUAL MODE RECOVERED"
            : $"ORPHANED MANUAL MODE RECOVERY FAILED: {recovery.Message}";
        AddEvent((sequence, timestamp) => new RecoveryResultEvent(
            sequence,
            timestamp,
            message,
            succeeded));
        if (!succeeded)
        {
            Fault(message);
        }

        return succeeded;
    }

    public void StartThermalControl()
    {
        ThrowIfDisposed();
        if (!InitializeHost())
        {
            throw new InvalidOperationException(_lastFailure ?? "Runtime host initialization failed.");
        }

        _operationGate.Wait();
        try
        {
            lock (_sync)
            {
                if (_state != RuntimeState.Stopped)
                {
                    throw new RuntimeOwnershipException(
                        $"Thermal ownership cannot be acquired while runtime state is {_state}.");
                }

                if (_emergencyLatched)
                {
                    throw new RuntimeOwnershipException(
                        "This runtime experienced an emergency and cannot re-enter Manual mode. Restart the runtime first.");
                }

                if (_standaloneManualOriginal is not null)
                {
                    throw new RuntimeOwnershipException(
                        "A standalone fixed fan profile owns Manual mode. Apply Auto before starting thermal control.");
                }
            }

            ThermalOwnershipQualification qualification =
                _controlTelemetry.QualifyThermalOwnership();
            if (!qualification.ThermalOwnershipReady)
            {
                throw new ThermalPreflightException(
                    $"Fresh thermal ownership qualification failed: " +
                    $"{string.Join(" ", qualification.Reasons)} No SET was sent.");
            }

            lock (_sync)
            {
                _state = RuntimeState.Starting;
                _lastRejection = null;
                _sessionId = Guid.NewGuid();
                _startTimestamp = _clock.UtcNow;
                _lastFailure = null;
                _emergencyStatus = null;
            }

            try
            {
                // The GPU ladder is built from limits the device reported at qualification.
                // Passing them per-session keeps the engine free of provider concerns and
                // keeps discovery off the telemetry path.
                _controller = new ThermalRuntimeController(
                    _telemetryAdapter,
                    _hardware,
                    _profile,
                    policy: new ThermalPolicy
                    {
                        GpuLimits = _controlTelemetry.Capabilities.GpuThermalLimits
                    },
                    clock: new ThermalClockAdapter(_clock));
                _controller.Start();

                // The performance mode the session qualified and took ownership in. The GPU
                // thermal limits above were derived from that mode's thermal anchor, so if the
                // mode changes underneath the session those limits stop describing the machine.
                _sessionPerformanceMode =
                    _controller.OriginalState?.Zone1PerformanceMode;
                _currentTarget = ThermalCurve.MinimumDynamicRpm;
                _currentProfile = "Thermal/default";
                _nextWatchdog = _clock.MonotonicNow + _watchdogInterval;
                lock (_sync)
                {
                    _state = RuntimeState.Running;
                }

                // Record which GPU thermal limits the session is running under. When a handoff
                // is later reported from the field, the event log says what the thresholds
                // were rather than leaving them to be inferred.
                GpuThermalLimits? gpuLimits = _controlTelemetry.Capabilities.GpuThermalLimits;
                PublishCapturedRestorationState(accepted: true);
                AddEvent((sequence, timestamp) => new SessionStartedEvent(
                    sequence,
                    timestamp,
                    "Thermal session started with fast telemetry and deadline scheduling. " +
                    $"GPU thermal limits: {gpuLimits?.Describe() ?? "unavailable"}.",
                    _sessionId!.Value));
            }
            catch (ThermalPreflightException exception)
            {
                // Whatever the controller managed to capture before refusing is the only
                // record of what the firmware actually reported at that instant.
                PublishCapturedRestorationState(accepted: false);

                // A prerequisite was not met and no SET was sent, so firmware still owns
                // cooling exactly as it did a moment ago. Nothing is broken: the runtime is
                // simply not in a thermal session, which is what Stopped means.
                //
                // Faulting here was wrong and expensive — it made a benign "your fans are not
                // in Auto" answer look like a runtime failure and demanded a service restart
                // to clear. Faulted is reserved for conditions where the runtime or the
                // hardware control path is actually broken.
                RejectStart($"Thermal session start rejected: {exception.Message}");
                throw;
            }
            catch (Exception exception)
            {
                Fault($"Thermal session start failed: {exception.Message}");
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public ThermalOwnershipQualification QualifyThermalOwnership()
    {
        EnsureReadOperationAllowed();
        _operationGate.Wait();
        try
        {
            return _controlTelemetry.QualifyThermalOwnership();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask RunScheduledAsync(
        CancellationToken cancellationToken,
        long? maximumCycles = null)
    {
        ThrowIfDisposed();
        if (State != RuntimeState.Running)
        {
            throw new InvalidOperationException("Thermal control is not running.");
        }

        try
        {
            await _scheduler.RunAsync(
                RunScheduledCycleAsync,
                cancellationToken,
                maximumCycles).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await EmergencyFromUnexpectedExceptionAsync(exception).ConfigureAwait(false);
        }
    }

    public async ValueTask<ThermalSessionResult?> StopThermalControlAsync()
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThermalRuntimeController? controller = _controller;
            if (controller is null)
            {
                return StopStandaloneManual();
            }

            RuntimeState stateBeforeStop;
            lock (_sync)
            {
                stateBeforeStop = _state;
                if (_state == RuntimeState.Running)
                {
                    _state = RuntimeState.Stopping;
                }
            }

            ThermalSessionResult result = controller.Stop();
            lock (_sync)
            {
                if (stateBeforeStop is not RuntimeState.Faulted and
                    not RuntimeState.EmergencyHandoff)
                {
                    _state = result.Succeeded ? RuntimeState.Stopped : RuntimeState.Faulted;
                }

                _currentProfile = null;
                _sessionPerformanceMode = null;
                _currentTarget = null;

                if (!result.Succeeded)
                {
                    _lastFailure = result.Message;
                }
            }

            AddEvent((sequence, timestamp) => new SessionStoppedEvent(
                sequence,
                timestamp,
                result.Message,
                _sessionId));
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public RuntimeStatus GetStatus()
    {
        ThrowIfDisposed();
        _operationGate.Wait();
        try
        {
            lock (_sync)
            {
                return new RuntimeStatus(
                    _state,
                    _sessionId,
                    _startTimestamp,
                    _currentProfile,
                    _controller?.OriginalState ?? _standaloneManualOriginal,
                    _currentTarget,
                    _telemetryAdapter.Latest,
                    _telemetryAdapter.Latest is null
                        ? null
                        : TelemetryHealthEvaluator.Evaluate(
                            _telemetryAdapter.Latest.ToDiagnosticSnapshot(),
                            _clock.UtcNow),
                    _scheduler.Metrics,
                    DescribeSchedulerHealth(_scheduler.Metrics),
                    RuntimeBuildIdentifier,
                    _lastWatchdog,
                    _lastWatchdogAt,
                    _lastFailure,
                    _lastRejection,
                    _emergencyStatus,
                    _telemetryAdapter.LastAcquisitionDuration,
                    DurationStatistics.From(_acquisitionWindow),
                    DurationStatistics.From(_actuatorWindow),
                    _watchdogCoalescedCount,
                    _events.TotalCount,
                    _controller?.Decisions.Count ?? 0,
                    _controller?.Trace.Count ?? 0,
                    _events.Snapshot());
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public TelemetrySnapshot GetDiagnosticSnapshot()
    {
        EnsureReadOperationAllowed();
        _operationGate.Wait();
        try
        {
            return _diagnosticTelemetry.GetSnapshot();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Returns the lightweight provider sample used by monitoring clients. While thermal
    /// control is running, the controller's latest authoritative sample is reused so the
    /// UI cannot introduce a second provider acquisition into the control cadence.
    /// </summary>
    public ThermalTelemetrySample GetTelemetrySample()
    {
        ThrowIfDisposed();
        _operationGate.Wait();
        try
        {
            if (State == RuntimeState.Running && _telemetryAdapter.Latest is { } latest)
            {
                return latest;
            }

            return _controlTelemetry.GetControlSample();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public PerformanceState GetPerformanceState()
    {
        EnsureReadOperationAllowed();
        _operationGate.Wait();
        try
        {
            return _hardware.GetPerformanceState();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public PerformanceApplyResult ApplyPerformanceProfile(PerformanceProfile profile)
    {
        EnsureStaticOperationAllowed();
        _operationGate.Wait();
        try
        {
            return _hardware.ApplyPerformanceProfile(profile);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public FanControlState GetFanState()
    {
        EnsureReadOperationAllowed();
        _operationGate.Wait();
        try
        {
            return _hardware.GetFanState();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public FanControlApplyResult ApplyFanProfile(FanControlProfile profile)
    {
        EnsureStaticOperationAllowed();
        _operationGate.Wait();
        try
        {
            FanControlApplyResult result = _hardware.ApplyFanProfile(profile);
            if (profile.IsFixed && result.Succeeded)
            {
                _standaloneManualOriginal ??= Convert(result.InitialState);
                _standaloneShutdownAttempted = false;
                _standaloneStopResult = null;
                _currentProfile = "Fan/Fixed";
                _currentTarget = Math.Max(
                    profile.Fan1Rpm!.Value.Value,
                    profile.Fan2Rpm!.Value.Value);
            }
            else if (!profile.IsFixed && result.Succeeded &&
                     _standaloneManualOriginal is not null)
            {
                ThermalControlOperationResult restore =
                    _hardware.RestorePerformance(_standaloneManualOriginal);
                if (!restore.Succeeded)
                {
                    _standaloneShutdownAttempted = true;
                    _standaloneStopResult = new ThermalSessionResult(
                        ThermalControllerStateKind.EmergencyStopped,
                        false,
                        "Standalone fan profile reached Auto, but performance restoration failed.",
                        _standaloneManualOriginal,
                        restore.FinalState,
                        [],
                        []);
                    Fault($"Standalone fan-profile performance restoration failed: {restore.Message}");
                    throw new InvalidOperationException(_lastFailure);
                }

                _standaloneManualOriginal = null;
                _currentProfile = null;
                _sessionPerformanceMode = null;
                _currentTarget = null;
            }

            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_controller is not null || _standaloneManualOriginal is not null)
        {
            await StopThermalControlAsync().ConfigureAwait(false);
        }

        _hardware.ExchangeCompleted -= OnExchangeCompleted;
        _hostLease?.Dispose();
        _hostLease = null;
        _ownershipGate.Dispose();
        _operationGate.Dispose();
        _disposed = true;
    }

    private async ValueTask<bool> RunScheduledCycleAsync(
        SchedulerCycle cycle,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != RuntimeState.Running || _controller is null)
            {
                return false;
            }

            long cycleStarted = Stopwatch.GetTimestamp();
            ThermalDecision decision = _controller.RunCycle();
            _acquisitionWindow.Record(_telemetryAdapter.LastAcquisitionDuration);

            // The actuator's share is whatever the cycle spent that acquisition did not. On a
            // cycle with no write that is the decision alone; on a write cycle it is the eight
            // HID exchanges, which is the number worth watching.
            TimeSpan cycleSoFar = Stopwatch.GetElapsedTime(cycleStarted);
            _actuatorWindow.Record(Positive(cycleSoFar - _telemetryAdapter.LastAcquisitionDuration));
            AddEvent((sequence, timestamp) => new ThermalDecisionEvent(
                sequence,
                timestamp,
                decision.Reason,
                decision));
            if (decision.ShouldWrite)
            {
                _currentTarget = decision.EffectiveTarget.Value;
                AddEvent((sequence, timestamp) => new FanTargetChangedEvent(
                    sequence,
                    timestamp,
                    decision.Reason,
                    decision.EffectiveTarget.Value));
            }

            if (_controller.State == ThermalControllerStateKind.EmergencyStopped)
            {
                // A deliberate safety handoff that reached firmware Auto is not a fault: the
                // protection worked. Reporting it as Faulted made a successful handoff look
                // like a broken runtime. Faulted is reserved for a handoff that could not be
                // established.
                bool auto = _controller.FinalState?.IsAuto == true;
                _emergencyLatched = true;
                _emergencyStatus = decision.Reason;
                lock (_sync)
                {
                    _state = auto ? RuntimeState.EmergencyHandoff : RuntimeState.Faulted;
                    _currentProfile = null;
                    _sessionPerformanceMode = null;
                }

                if (!auto)
                {
                    _lastFailure = decision.Reason;
                }

                AddEvent((sequence, timestamp) => new EmergencyHandoffEvent(
                    sequence,
                    timestamp,
                    decision.Reason,
                    auto));
                return false;
            }

            if (_clock.MonotonicNow >= _nextWatchdog)
            {
                if (!RunWatchdog(_controller.LastOwnershipObservation))
                {
                    return false;
                }

                // The due point advances in absolute steps, exactly as before. Coalescing
                // changes which read answers the deadline, never when the next one falls due,
                // so a satisfied watchdog cannot push its own schedule forward.
                do
                {
                    _nextWatchdog += _watchdogInterval;
                }
                while (_nextWatchdog <= _clock.MonotonicNow);
            }

            return true;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// How recent a fan-write observation must be to answer a watchdog deadline.
    /// </summary>
    /// <remarks>
    /// One control period. An observation from this cycle's own write is microseconds old; one
    /// from any earlier cycle is at least a period old and is refused. The bound is therefore
    /// tight enough that only a same-cycle read can satisfy the deadline, while staying stated
    /// in time rather than in cycle bookkeeping that could drift out of step.
    /// </remarks>
    /// <summary>
    /// Health stated in terms of causes, with the recovery tail reported separately.
    /// </summary>
    /// <remarks>
    /// This used to read "Degraded: N deadline overruns" from a single counter that incremented
    /// for both a slow cycle and every late cycle that followed it. One slow cycle could
    /// therefore report as four or five faults, which is how 791 cycles came to show 136
    /// "overruns" from far fewer actual events.
    /// </remarks>
    private static TimeSpan Positive(TimeSpan value) =>
        value > TimeSpan.Zero ? value : TimeSpan.Zero;

    /// <summary>
    /// Health judged on recent behaviour, with the session totals reported alongside it.
    /// </summary>
    /// <remarks>
    /// <para>Health used to be derived from cumulative counts, so a single slow cycle early in
    /// a session left the runtime reading "Degraded" for as long as it ran. That is a true
    /// statement about the session's history and a useless one about its present state — an
    /// operator asking "is it coping now" got an answer about an hour ago.</para>
    /// <para>The verdict now comes from slow cycles inside the rolling window. The lifetime
    /// totals are still printed, because "none recently, 13 in this session" is a more useful
    /// sentence than either half alone.</para>
    /// </remarks>
    /// <summary>
    /// Which build this runtime is, including the source commit where one was stamped.
    /// </summary>
    /// <remarks>
    /// A running service should be able to say what it is. During live diagnosis "the
    /// installed runtime predates commit X" had to be deduced by hashing files against a
    /// publish directory; that is a fact the process can simply report.
    /// </remarks>
    internal static string RuntimeBuildIdentifier { get; } =
        typeof(BladeRuntime).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(BladeRuntime).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static string DescribeSchedulerHealth(SchedulerMetrics metrics)
    {
        if (metrics.RecentWindowSize == 0)
        {
            return metrics.CompletedCycles == 0
                ? "No session has run."
                : "Healthy";
        }

        string history = metrics.SlowCycleCount == 0
            ? "none this session"
            : $"{metrics.SlowCycleCount} this session, " +
                $"max {metrics.MaximumCycleExecutionDuration.TotalMilliseconds:F0} ms";

        return metrics.RecentSlowCycleCount == 0
            ? $"Healthy: no slow cycles in the last {metrics.RecentWindowSize} ({history})."
            : $"Degraded: {metrics.RecentSlowCycleCount} slow cycles in the last " +
                $"{metrics.RecentWindowSize} ({history}).";
    }

    private TimeSpan WatchdogObservationFreshness => _controlPeriod;

    /// <summary>
    /// Checks that firmware still reports BladeControl owning the fans.
    /// </summary>
    /// <param name="freshObservation">
    /// The zone modes read by a fan write during this cycle, if any. Used instead of a new
    /// read when it is recent enough for this deadline.
    /// </param>
    /// <remarks>
    /// <para>A fan write verifies ownership as its last act, reading exactly the 0x0D82 pair
    /// this check needs. Issuing another one microseconds later costs two more HID exchanges
    /// on the cycle that is already the most expensive.</para>
    /// <para>What is <i>not</i> permitted: inferring ownership from the write having succeeded,
    /// reusing the pre-write observation, or accepting anything whose measured age exceeds
    /// <see cref="WatchdogObservationFreshness"/>. The observation carries the timestamp of its
    /// own second 0x0D82 response, so its age is measured, not assumed. Anything that does not
    /// qualify falls through to a normal read.</para>
    /// </remarks>
    private bool RunWatchdog(RazerOwnershipObservation? freshObservation)
    {
        RuntimeRazerModeState mode;
        try
        {
            if (freshObservation is { } observed &&
                observed.Age <= WatchdogObservationFreshness)
            {
                mode = new RuntimeRazerModeState(
                    observed.Zone1PerformanceMode,
                    observed.Zone1FanMode,
                    observed.Zone2PerformanceMode,
                    observed.Zone2FanMode,
                    observed.Exchanges);
                _watchdogCoalescedCount++;
            }
            else
            {
                mode = _hardware.ReadModeState();
            }

            _lastWatchdog = mode;
            _lastWatchdogAt = _clock.UtcNow;
            AddEvent((sequence, timestamp) => new RazerWatchdogCheckEvent(
                sequence,
                timestamp,
                mode.ToString(),
                mode));
        }
        catch (Exception exception)
        {
            return EmergencyHandoff($"Razer watchdog read failed: {exception.Message}");
        }

        // Ownership is the fan mode. The watchdog used to accept only Balanced + Manual, so a
        // session legitimately running in Silent would have read as ownership lost on its very
        // first watchdog tick.
        if (mode.IsOwnedManual)
        {
            // Owned, but possibly no longer in the mode the session was qualified for. The
            // GPU thermal limits were derived from that mode's anchor: 87/89/92 in Balanced,
            // 75/77/80 in Silent and Custom. A mode change from outside leaves the fan mode
            // untouched, so ownership looks intact while the ladder carries on with limits
            // that no longer describe the machine.
            //
            // One direction of that is dangerous rather than merely wrong. Balanced to Silent
            // drops the real target from 87 to 75 while the ladder still holds 87-based
            // thresholds, so every rung fires about twelve degrees late. Firmware slowdown and
            // shutdown still stand, but the entire point of the ladder is to act before them.
            //
            // Re-qualifying mid-session would mean a fresh NVML discovery inside the control
            // loop, and this is the wrong place to take that on. Handing back to firmware is
            // correct, bounded, and consistent with how every other loss of the qualified
            // state is treated here.
            if (_sessionPerformanceMode is { } qualified &&
                mode.Zone1PerformanceMode != qualified)
            {
                return AdoptLimitsForNewPerformanceMode(qualified, mode.Zone1PerformanceMode);
            }

            return true;
        }

        if (mode.IsKnownAuto)
        {
            string message = $"External ownership change detected: {mode}.";
            _controller!.AbandonAfterOwnershipLoss(message);
            _emergencyLatched = true;
            _lastFailure = message;
            lock (_sync)
            {
                _state = RuntimeState.Faulted;
                _currentProfile = null;
                _sessionPerformanceMode = null;
            }

            AddEvent((sequence, timestamp) => new OwnershipLostEvent(
                sequence,
                timestamp,
                message));
            return false;
        }

        return EmergencyHandoff($"Razer watchdog found mismatched or unsafe state: {mode}.");
    }

    /// <summary>
    /// Re-derives GPU thermal limits after the performance mode changed under the session.
    /// </summary>
    /// <remarks>
    /// <para>This used to hand off to firmware and latch an emergency. That was wrong twice
    /// over. Nothing had overheated — the user had deliberately changed a power setting — so
    /// calling it an emergency described a safe, intentional act as a thermal event. And the
    /// handoff restored the captured performance state on the way out, silently undoing the very
    /// change that triggered it.</para>
    /// <para>The limits are what went stale, not the session. The anchor those limits are derived
    /// from follows the performance mode, so the fix is to derive them again for the mode now in
    /// force and carry on. Ladder counters and hysteresis are preserved, because a GPU sitting at
    /// its slowdown limit a moment ago is still there under a different ceiling.</para>
    /// <para>It still fails closed. If the new mode does not qualify — an anchor nobody has
    /// validated, telemetry gone — there are no thresholds to run against and the fans go back to
    /// firmware, which is the one case that genuinely warrants a handoff.</para>
    /// </remarks>
    private bool AdoptLimitsForNewPerformanceMode(
        RazerPerformanceMode previous,
        RazerPerformanceMode current)
    {
        ThermalOwnershipQualification qualification;
        try
        {
            qualification = _controlTelemetry.QualifyThermalOwnership();
        }
        catch (Exception exception)
        {
            return EmergencyHandoff(
                $"Performance mode changed from {previous} to {current}, and GPU thermal " +
                $"limits could not be re-derived for it: {exception.Message}");
        }

        if (!qualification.ThermalOwnershipReady ||
            _controlTelemetry.Capabilities.GpuThermalLimits is not { } limits)
        {
            return EmergencyHandoff(
                $"Performance mode changed from {previous} to {current}, and the new mode does " +
                $"not qualify for thermal ownership: {string.Join(" ", qualification.Reasons)}");
        }

        _controller!.AdoptGpuThermalLimits(limits);
        _sessionPerformanceMode = current;
        AddEvent((sequence, timestamp) => new RecoveryResultEvent(
            sequence,
            timestamp,
            $"Performance mode changed from {previous} to {current}; GPU thermal limits " +
            $"re-derived for it ({limits.Describe()}). The session continues.",
            true));
        return true;
    }

    private bool EmergencyHandoff(string reason)
    {
        lock (_sync)
        {
            _state = RuntimeState.EmergencyHandoff;
        }

        _controller!.EmergencyHandoff(reason);
        bool auto = _controller.FinalState?.IsAuto == true;
        _emergencyLatched = true;
        _emergencyStatus = auto
            ? $"Emergency Balanced + Auto handoff completed: {reason}"
            : $"Emergency Balanced + Auto handoff failed: {reason}";
        lock (_sync)
        {
            // EmergencyHandoff means firmware verifiably owns cooling again. Faulted means we
            // could not get it there, which is the genuinely alarming case.
            _state = auto ? RuntimeState.EmergencyHandoff : RuntimeState.Faulted;
            _currentProfile = null;
            _sessionPerformanceMode = null;
        }

        if (!auto)
        {
            _lastFailure = reason;
        }

        AddEvent((sequence, timestamp) => new EmergencyHandoffEvent(
            sequence,
            timestamp,
            _emergencyStatus,
            auto));
        return false;
    }

    private async ValueTask EmergencyFromUnexpectedExceptionAsync(Exception exception)
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_controller?.State == ThermalControllerStateKind.Manual)
            {
                EmergencyHandoff($"Unexpected runtime exception: {exception.Message}");
            }
            else
            {
                Fault($"Unexpected runtime exception: {exception.Message}");
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void OnTelemetrySample(ThermalTelemetrySample sample, TimeSpan duration)
    {
        string message =
            $"CPU {FormatMetric(sample.CpuPackageTemperatureCelsius)}; " +
            $"GPU {FormatMetric(sample.GpuTemperatureCelsius)}; " +
            $"acquisition {duration.TotalMilliseconds:F1} ms.";
        AddEvent((sequence, timestamp) => new TelemetrySampleEvent(
            sequence,
            timestamp,
            message,
            sample,
            duration));
    }

    private void OnExchangeCompleted(RazerExchangeTrace exchange) =>
        AddEvent((sequence, timestamp) => new ProtocolExchangeEvent(
            sequence,
            timestamp,
            $"Razer command 0x{exchange.CombinedCommand:X4}, Tx 0x{exchange.TransactionId:X2}.",
            exchange));

    private void OnSchedulerOverrun(SchedulerCycle cycle, TimeSpan overrun) =>
        AddEvent((sequence, timestamp) => new SchedulerOverrunEvent(
            sequence,
            timestamp,
            $"Cycle {cycle.Sequence} exceeded its next deadline by {overrun.TotalMilliseconds:F1} ms.",
            overrun));

    /// <summary>
    /// Publishes every restoration capture the start attempt took, in order.
    /// </summary>
    /// <remarks>
    /// Called on both outcomes. A refused start is precisely the case where this matters: the
    /// captures are the only record of what firmware reported at those instants, and any later
    /// read observes a different moment. Publishing the whole sequence rather than the accepted
    /// one is what makes an unstable state legible after the fact.
    /// </remarks>
    private void PublishCapturedRestorationState(bool accepted)
    {
        if (_controller is not { } controller)
        {
            return;
        }

        IReadOnlyList<ThermalMachineState> captures = controller.RestorationCaptures;
        for (int index = 0; index < captures.Count; index++)
        {
            ThermalMachineState capture = captures[index];
            string label = ((char)('A' + index)).ToString();

            // Stabilization adopts the capture that corroborated its predecessor, which is
            // always the last one taken. Marked only when the start actually went on to
            // succeed — an adopted capture on a refused start would be a contradiction.
            bool isAccepted = accepted && index == captures.Count - 1;
            AddEvent((sequence, timestamp) => new RestorationStateCapturedEvent(
                sequence,
                timestamp,
                $"Restoration capture {label}: {capture.Describe()}.",
                label,
                capture.Zone1PerformanceMode.ToString(),
                capture.Zone2PerformanceMode.ToString(),
                capture.Zone1FanMode.ToString(),
                capture.Zone2FanMode.ToString(),
                capture.CpuLevel.ToString(),
                capture.GpuLevel.ToString(),
                capture.ZonesAgree,
                isAccepted));
        }
    }

    private void AddEvent(Func<long, DateTimeOffset, RuntimeEvent> factory)
    {
        long sequence = Interlocked.Increment(ref _eventSequence);
        RuntimeEvent item = factory(sequence, _clock.UtcNow);
        _events.Add(item);
        Action<RuntimeEvent>? handlers = EventPublished;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<RuntimeEvent> handler in handlers.GetInvocationList()
                     .Cast<Action<RuntimeEvent>>())
        {
            try
            {
                handler(item);
            }
            catch
            {
                // Event consumers must never alter control or recovery behavior.
            }
        }
    }

    private void EnsureReadOperationAllowed()
    {
        ThrowIfDisposed();
        if (!InitializeHost())
        {
            throw new InvalidOperationException(_lastFailure ?? "Runtime host initialization failed.");
        }

        if (State is RuntimeState.Starting or RuntimeState.Stopping or
            RuntimeState.EmergencyHandoff)
        {
            throw new RuntimeOwnershipException(
                $"Runtime read rejected during the {State} transition.");
        }
    }

    private void EnsureStaticOperationAllowed()
    {
        ThrowIfDisposed();
        if (!InitializeHost())
        {
            throw new InvalidOperationException(_lastFailure ?? "Runtime host initialization failed.");
        }

        RuntimeState state = State;
        if (state is RuntimeState.Starting or RuntimeState.Running or
            RuntimeState.Stopping or RuntimeState.EmergencyHandoff or RuntimeState.Faulted)
        {
            throw new RuntimeOwnershipException(
                $"Direct profile/diagnostic operation rejected while runtime state is {state}.");
        }
    }

    /// <summary>
    /// Records a safe start rejection and returns the runtime to Stopped.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Fault"/>: no SET was sent, firmware ownership is unchanged,
    /// and a later attempt may succeed without restarting anything. The reason is kept so the
    /// interface can explain why the last attempt did not take.
    /// </remarks>
    /// <summary>
    /// Refuses a start whose prerequisites were not met, without treating it as a fault.
    /// </summary>
    /// <remarks>
    /// The reason is recorded separately from <c>_lastFailure</c>. Writing a refusal into the
    /// failure slot made every consumer describe it as one: the interface rendered "Runtime
    /// failure: ..." beside the operation's own rejection message, so a machine that had simply
    /// declined to start — correctly, safely, with no write sent — reported a failure twice.
    /// Nothing is broken when a prerequisite is not met, and the runtime already draws that
    /// distinction; it was being erased one layer up.
    /// </remarks>
    private void RejectStart(string message)
    {
        lock (_sync)
        {
            _lastRejection = message;
            _lastFailure = null;
            _state = RuntimeState.Stopped;
            _sessionId = null;
            _startTimestamp = null;
            _currentProfile = null;
            _sessionPerformanceMode = null;
        }
    }

    private void Fault(string message)
    {
        lock (_sync)
        {
            _lastFailure = message;
            _state = RuntimeState.Faulted;
        }
    }

    private ThermalSessionResult? StopStandaloneManual()
    {
        if (_standaloneShutdownAttempted)
        {
            return _standaloneStopResult;
        }

        ThermalMachineState? original = _standaloneManualOriginal;
        if (original is null)
        {
            return null;
        }

        _standaloneShutdownAttempted = true;

        lock (_sync)
        {
            _state = RuntimeState.Stopping;
        }

        ThermalControlOperationResult auto = _hardware.ReturnToFirmwareAuto();
        ThermalControlOperationResult? restore = null;
        if (auto.Succeeded && auto.FinalState?.IsKnownAuto == true)
        {
            restore = _hardware.RestorePerformance(original);
        }

        bool succeeded = restore?.Succeeded == true;
        string message = succeeded
            ? "Standalone Manual fan profile stopped; Auto was verified before performance restoration."
            : auto.Succeeded
                ? "Standalone Manual fan profile reached Auto, but performance restoration failed."
                : "FAN AUTO RESTORATION FAILED";
        lock (_sync)
        {
            _state = succeeded ? RuntimeState.Stopped : RuntimeState.Faulted;
            _lastFailure = succeeded ? null : message;
        }

        if (succeeded)
        {
            _standaloneManualOriginal = null;
            _currentProfile = null;
            _sessionPerformanceMode = null;
            _currentTarget = null;
        }

        AddEvent((sequence, timestamp) => new SessionStoppedEvent(
            sequence,
            timestamp,
            message,
            _sessionId));
        _standaloneStopResult = new ThermalSessionResult(
            succeeded ? ThermalControllerStateKind.Stopped : ThermalControllerStateKind.EmergencyStopped,
            succeeded,
            message,
            original,
            restore?.FinalState ?? auto.FinalState,
            [],
            []);
        return _standaloneStopResult;
    }

    private static ThermalMachineState Convert(FanControlState state) => new(
        state.Device,
        state.Zone1Mode.PerformanceMode,
        state.Zone2Mode.PerformanceMode,
        state.Zone1Mode.FanMode,
        state.Zone2Mode.FanMode,
        state.CpuPerformanceLevel,
        state.GpuPerformanceLevel,
        state.Fan1.FirmwareReportedRpm,
        state.Fan2.FirmwareReportedRpm,
        state.InitialExchanges);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static string FormatMetric(TelemetryMetric<double> metric) =>
        metric.IsValid && metric.Value.HasValue
            ? $"{metric.Value.Value:F1} C"
            : "unavailable";

    private sealed class ThermalClockAdapter : IThermalClock
    {
        private readonly IRuntimeClock _clock;

        internal ThermalClockAdapter(IRuntimeClock clock)
        {
            _clock = clock;
        }

        public DateTimeOffset UtcNow => _clock.UtcNow;
    }

    private sealed class ControlTelemetryAdapter : ITelemetryProvider
    {
        private readonly IControlTelemetryProvider _provider;
        private readonly IRuntimeClock _clock;
        private readonly Action<ThermalTelemetrySample, TimeSpan> _observer;

        internal ControlTelemetryAdapter(
            IControlTelemetryProvider provider,
            IRuntimeClock clock,
            Action<ThermalTelemetrySample, TimeSpan> observer)
        {
            _provider = provider;
            _clock = clock;
            _observer = observer;
        }

        internal ThermalTelemetrySample? Latest { get; private set; }

        internal TimeSpan LastAcquisitionDuration { get; private set; }

        public string Name => $"{_provider.Name} fast control path";

        public TelemetryCapabilities Capabilities => _provider.Capabilities;

        public TelemetrySnapshot GetSnapshot()
        {
            TimeSpan start = _clock.MonotonicNow;
            ThermalTelemetrySample sample = _provider.GetControlSample();
            LastAcquisitionDuration = _clock.MonotonicNow - start;
            Latest = sample;
            _observer(sample, LastAcquisitionDuration);
            return sample.ToDiagnosticSnapshot();
        }

        public void Dispose()
        {
        }
    }
}
