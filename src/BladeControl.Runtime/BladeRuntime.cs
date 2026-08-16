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
    RuntimeRazerModeState? LastRazerWatchdogState,
    string? LastFailureReason,
    string? EmergencyStatus,
    TimeSpan LastTelemetryAcquisitionDuration,
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
    private readonly BoundedRuntimeEventLog _events;
    private readonly ControlTelemetryAdapter _telemetryAdapter;
    private IRuntimeOwnershipLease? _hostLease;
    private ThermalRuntimeController? _controller;
    private DeadlineScheduler _scheduler;
    private RuntimeState _state = RuntimeState.Stopped;
    private RuntimeRazerModeState? _lastWatchdog;
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

        if (!startupState.IsBalancedManual)
        {
            Fault($"Startup firmware state is not a safe known Auto state: {startupState}.");
            return false;
        }

        AddEvent((sequence, timestamp) => new RecoveryAttemptEvent(
            sequence,
            timestamp,
            "Potentially orphaned Balanced + Manual mode detected; attempting one Balanced + Auto recovery."));
        ThermalControlOperationResult recovery;
        try
        {
            recovery = _hardware.ReturnToBalancedAuto();
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

        bool succeeded = recovery.Succeeded && recovery.FinalState?.IsBalancedAuto == true;
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
                _sessionId = Guid.NewGuid();
                _startTimestamp = _clock.UtcNow;
                _lastFailure = null;
                _emergencyStatus = null;
            }

            try
            {
                _controller = new ThermalRuntimeController(
                    _telemetryAdapter,
                    _hardware,
                    _profile,
                    clock: new ThermalClockAdapter(_clock));
                _controller.Start();
                _currentTarget = ThermalCurve.MinimumDynamicRpm;
                _currentProfile = "Thermal/default";
                _nextWatchdog = _clock.MonotonicNow + _watchdogInterval;
                lock (_sync)
                {
                    _state = RuntimeState.Running;
                }

                AddEvent((sequence, timestamp) => new SessionStartedEvent(
                    sequence,
                    timestamp,
                    "Thermal session started with fast telemetry and deadline scheduling.",
                    _sessionId!.Value));
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
                    _scheduler.Metrics.OverrunCount == 0
                        ? "Healthy"
                        : $"Degraded: {_scheduler.Metrics.OverrunCount} deadline overruns.",
                    _lastWatchdog,
                    _lastFailure,
                    _emergencyStatus,
                    _telemetryAdapter.LastAcquisitionDuration,
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

            ThermalDecision decision = _controller.RunCycle();
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
                _emergencyLatched = true;
                _emergencyStatus = decision.Reason;
                lock (_sync)
                {
                    _state = RuntimeState.Faulted;
                    _currentProfile = null;
                }

                AddEvent((sequence, timestamp) => new EmergencyHandoffEvent(
                    sequence,
                    timestamp,
                    decision.Reason,
                    _controller.FinalState?.IsAuto == true));
                return false;
            }

            if (_clock.MonotonicNow >= _nextWatchdog)
            {
                if (!RunWatchdog())
                {
                    return false;
                }

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

    private bool RunWatchdog()
    {
        RuntimeRazerModeState mode;
        try
        {
            mode = _hardware.ReadModeState();
            _lastWatchdog = mode;
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

        if (mode.IsBalancedManual)
        {
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
            }

            AddEvent((sequence, timestamp) => new OwnershipLostEvent(
                sequence,
                timestamp,
                message));
            return false;
        }

        return EmergencyHandoff($"Razer watchdog found mismatched or unsafe state: {mode}.");
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
        _lastFailure = reason;
        lock (_sync)
        {
            _state = RuntimeState.Faulted;
            _currentProfile = null;
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

        ThermalControlOperationResult auto = _hardware.ReturnToBalancedAuto();
        ThermalControlOperationResult? restore = null;
        if (auto.Succeeded && auto.FinalState?.IsBalancedAuto == true)
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
