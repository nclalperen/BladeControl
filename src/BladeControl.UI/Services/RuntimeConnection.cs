using System.ComponentModel;
using System.Runtime.CompilerServices;
using BladeControl.Runtime;
using BladeControl.UI.Ipc;

namespace BladeControl.UI.Services;

public enum RuntimeConnectionState
{
    Connecting,
    Online,
    Offline
}

/// <summary>Where the currently displayed telemetry came from.</summary>
public enum TelemetryOrigin
{
    None,

    /// <summary>The authoritative sample the thermal controller is acting on.</summary>
    ThermalSession,

    /// <summary>An explicit full diagnostic acquisition.</summary>
    DiagnosticSnapshot,

    /// <summary>A provider-only sample acquired through Runtime Core.</summary>
    ProviderSample
}

public sealed record RuntimeCommandOutcome(bool Succeeded, string Message)
{
    public static RuntimeCommandOutcome Ok(string message) => new(true, message);

    public static RuntimeCommandOutcome Fail(string message) => new(false, message);
}

/// <summary>
/// Owns the single conversation with Runtime Core: a bounded sequential poll loop while
/// connected, a conservative read-only reconnect probe while offline, and a one-at-a-time
/// gate for state-changing commands. Failed state-changing commands are never retried.
/// </summary>
public sealed class RuntimeConnection : INotifyPropertyChanged, IDisposable
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan DefaultReconnectInterval = TimeSpan.FromSeconds(5);

    /// <summary>Telemetry older than this is presented as stale rather than live.</summary>
    public static readonly TimeSpan StaleTelemetryThreshold = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long after launch a runtime that has never answered is presented as still starting
    /// rather than as offline.
    /// </summary>
    /// <remarks>
    /// The UI is registered to start at sign-in, which is exactly when the Runtime service is
    /// itself still coming up — the service uses delayed automatic start so it does not
    /// contend with logon. Without this window the very first probe fails and the panel would
    /// greet every login with a hard error that resolves itself seconds later. This changes
    /// presentation only: the connection state, and therefore every command gate, is
    /// unaffected, and the existing 5-second read-only probe keeps its cadence.
    /// </remarks>
    public static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(90);

    private const int EventTickDivisor = 2;

    private readonly IRuntimeUiClient _client;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _reconnectInterval;
    private readonly TimeSpan _startupGracePeriod;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly object _startSync = new();

    private Task? _loop;
    private long _tick;
    private long _eventCursor;
    private DateTimeOffset? _lastTelemetryTimestamp;
    private RuntimeConnectionState _state = RuntimeConnectionState.Connecting;
    private RuntimeStatusDto? _status;
    private ThermalTelemetrySampleDto? _telemetry;
    private TelemetryOrigin _telemetryOrigin;
    private PerformanceStateDto? _performance;
    private FanStateDto? _fan;
    private RuntimeDoctorReportDto? _doctor;
    private string? _transportError;
    private string? _lastReadError;
    private int _commandInFlight;
    private volatile bool _doctorRefreshRequired = true;
    private volatile bool _profilesRefreshRequired = true;
    private volatile bool _statusConnected;
    private volatile bool _hasEverConnected;
    private DateTimeOffset? _startedAt;
    private bool _disposed;

    public RuntimeConnection(
        IRuntimeUiClient client,
        IUiDispatcher dispatcher,
        TimeSpan? pollInterval = null,
        TimeSpan? reconnectInterval = null,
        Func<DateTimeOffset>? now = null,
        TimeSpan? startupGracePeriod = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _reconnectInterval = reconnectInterval ?? DefaultReconnectInterval;
        _startupGracePeriod = startupGracePeriod ?? StartupGracePeriod;
    }

    /// <summary>
    /// True while the runtime has never answered and we are still inside the startup grace
    /// window — i.e. the service is plausibly still starting rather than absent.
    /// </summary>
    /// <remarks>
    /// Presentation only. Callers must keep using <see cref="State"/> for gating: this is
    /// deliberately true while the connection is Offline, and commands must stay disabled.
    /// Becomes permanently false after the first successful connection, so a runtime that
    /// later goes away is reported as the genuine fault it is.
    /// </remarks>
    public bool IsAwaitingRuntimeStartup =>
        !_hasEverConnected &&
        State != RuntimeConnectionState.Online &&
        _startedAt is { } started &&
        _now() - started < _startupGracePeriod;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised on the UI thread after every poll tick, successful or not.</summary>
    public event Action? Updated;

    /// <summary>Raised on the UI thread with newly observed runtime events, in order.</summary>
    public event Action<IReadOnlyList<RuntimeEventDto>, bool>? EventsReceived;

    /// <summary>Raised when Runtime Core's event sequence moved backwards after a restart.</summary>
    public event Action? EventStreamReset;

    /// <summary>Raised on the UI thread once per distinct telemetry timestamp.</summary>
    public event Action<ThermalTelemetrySampleDto>? TelemetryObserved;

    public IRuntimeUiClient Client => _client;

    public RuntimeConnectionState State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    public bool IsOnline => _state == RuntimeConnectionState.Online;

    public RuntimeStatusDto? Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
            {
                Raise(nameof(RuntimeStateName));
                RaiseGating();
            }
        }
    }

    public ThermalTelemetrySampleDto? Telemetry
    {
        get => _telemetry;
        private set => Set(ref _telemetry, value);
    }

    public TelemetryOrigin TelemetryOrigin
    {
        get => _telemetryOrigin;
        private set => Set(ref _telemetryOrigin, value);
    }

    public PerformanceStateDto? Performance
    {
        get => _performance;
        private set => Set(ref _performance, value);
    }

    public FanStateDto? Fan
    {
        get => _fan;
        private set => Set(ref _fan, value);
    }

    public RuntimeDoctorReportDto? Doctor
    {
        get => _doctor;
        private set => Set(ref _doctor, value);
    }

    /// <summary>The transport failure that took the connection offline, if any.</summary>
    public string? TransportError
    {
        get => _transportError;
        private set => Set(ref _transportError, value);
    }

    /// <summary>
    /// The most recent rejection of a read operation. Runtime Core refuses reads during
    /// Starting/Stopping/EmergencyHandoff transitions; that is not a disconnect.
    /// </summary>
    public string? LastReadError
    {
        get => _lastReadError;
        private set => Set(ref _lastReadError, value);
    }

    public bool IsCommandInFlight => Volatile.Read(ref _commandInFlight) != 0;

    public bool CanIssueCommand => IsOnline && !IsCommandInFlight;

    public string? RuntimeStateName => _status?.State;

    /// <summary>
    /// Runtime Core accepts direct performance/fan profile writes only while it is Stopped
    /// (BladeRuntime.EnsureStaticOperationAllowed), so those controls are disabled during a
    /// thermal session and during every transition rather than failing at the hardware.
    /// </summary>
    public bool CanApplyStaticProfile =>
        CanIssueCommand && string.Equals(RuntimeStateName, "Stopped", StringComparison.Ordinal);

    public string? StaticProfileBlockedReason
    {
        get
        {
            if (!IsOnline)
            {
                return "Runtime Core is offline.";
            }

            if (IsCommandInFlight)
            {
                return "Another Runtime Core request is still in flight.";
            }

            return RuntimeStateName switch
            {
                "Stopped" => null,
                "Running" =>
                    "Runtime Core owns cooling. Stop dynamic cooling before changing " +
                    "performance or fan profiles.",
                null => "Waiting for the first Runtime Core status.",
                { } state => $"Runtime Core is {state}; profile writes are rejected during " +
                    "this transition."
            };
        }
    }

    /// <summary>Start is offered only from a stopped runtime with qualified thermal ownership.</summary>
    public bool CanStartThermalControl =>
        CanIssueCommand &&
        IsThermalOwnershipReady &&
        string.Equals(RuntimeStateName, "Stopped", StringComparison.Ordinal);

    public bool CanStopThermalControl =>
        CanIssueCommand &&
        RuntimeStateName is "Running" or "Faulted" or "EmergencyHandoff";

    public long EventCursor => Interlocked.Read(ref _eventCursor);

    public long TickCount => Interlocked.Read(ref _tick);

    /// <summary>Age of the displayed telemetry sample, or null when there is none.</summary>
    public TimeSpan? TelemetryAge => _telemetry is null
        ? null
        : _now() - _telemetry.Timestamp;

    public bool IsTelemetryStale
    {
        get
        {
            if (_telemetry is null)
            {
                return false;
            }

            return !IsOnline || TelemetryAge > StaleTelemetryThreshold;
        }
    }

    /// <summary>
    /// True when Runtime Core has qualified thermal ownership. Until the doctor report has
    /// been read this is false, so Start Dynamic Cooling stays disabled by default.
    /// </summary>
    public bool IsThermalOwnershipReady => _doctor?.ThermalOwnershipReady == true;

    public string ThermalReadinessReason
    {
        get
        {
            if (!IsOnline)
            {
                return "Runtime Core is offline.";
            }

            if (_doctor is null)
            {
                return "Waiting for the Runtime Core qualification report.";
            }

            if (_doctor.ThermalOwnershipReady)
            {
                return "Runtime Core has qualified thermal ownership.";
            }

            return _doctor.Reasons is { Count: > 0 } reasons
                ? string.Join(" ", reasons)
                : "Runtime Core has not qualified thermal ownership.";
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_startSync)
        {
            _startedAt ??= _now();
            _loop ??= Task.Run(() => RunLoopAsync(_lifetime.Token), CancellationToken.None);
        }
    }

    public Task Completion => _loop ?? Task.CompletedTask;

    /// <summary>
    /// Executes exactly one poll tick. The loop calls this on a timer; tests call it
    /// directly so no test depends on wall-clock timing.
    /// </summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PollOnceCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    /// <summary>
    /// Forces an immediate read-only reconnect refresh. Safe at any time: it uses only
    /// bounded IPC reads and never changes hardware state.
    /// </summary>
    public async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        Publish(() =>
        {
            if (State == RuntimeConnectionState.Offline)
            {
                State = RuntimeConnectionState.Connecting;
            }
        });
        await PollOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a single state-changing command. Refuses to start while another is in flight,
    /// refuses while offline, surfaces the backend message verbatim, and never retries.
    /// </summary>
    public async Task<RuntimeCommandOutcome> ExecuteAsync(
        Func<IRuntimeUiClient, CancellationToken, Task<RuntimeCommandOutcome>> command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsOnline)
        {
            return RuntimeCommandOutcome.Fail(
                "Runtime Core is offline. Reconnect before changing hardware state.");
        }

        if (IsCommandInFlight)
        {
            return RuntimeCommandOutcome.Fail(
                "Another Runtime Core request is still in flight.");
        }

        if (Interlocked.CompareExchange(ref _commandInFlight, 1, 0) != 0)
        {
            return RuntimeCommandOutcome.Fail(
                "Another Runtime Core request is still in flight.");
        }

        Publish(NotifyCommandStateChanged);
        try
        {
            return await command(_client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return RuntimeCommandOutcome.Fail("The request was cancelled.");
        }
        catch (RuntimeUiException exception)
        {
            if (exception.IsDisconnect)
            {
                GoOffline(exception.Message);
            }

            return RuntimeCommandOutcome.Fail(exception.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _commandInFlight, 0);
            Publish(NotifyCommandStateChanged);
        }
    }

    /// <summary>Publishes the authoritative state returned by a successful start/stop command.</summary>
    public void AcceptCommandStatus(RuntimeStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Publish(() =>
        {
            Status = status;
            LastReadError = null;
            State = RuntimeConnectionState.Online;
        });
    }

    /// <summary>Re-reads the performance and fan state after a state change.</summary>
    public async Task<bool> RefreshProfilesNowAsync(CancellationToken cancellationToken)
    {
        try
        {
            PerformanceStateDto performance = await _client
                .GetPerformanceStateAsync(cancellationToken).ConfigureAwait(false);
            FanStateDto fan = await _client.GetFanStateAsync(cancellationToken)
                .ConfigureAwait(false);
            Publish(() =>
            {
                Performance = performance;
                Fan = fan;
                LastReadError = null;
            });
            _profilesRefreshRequired = false;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RuntimeUiException exception) when (exception.IsDisconnect)
        {
            GoOffline(exception.Message);
            return false;
        }
        catch (RuntimeUiException exception)
        {
            _profilesRefreshRequired = false;
            Publish(() => LastReadError = exception.Message);
            return false;
        }
    }

    /// <summary>
    /// Performs the heavyweight diagnostic reads only when the user explicitly requests
    /// them. Ordinary Dashboard and Monitoring polling never calls this method.
    /// </summary>
    public async Task<bool> RefreshDiagnosticsNowAsync(CancellationToken cancellationToken)
    {
        try
        {
            RuntimeDoctorReportDto doctor = await _client.GetDoctorAsync(cancellationToken)
                .ConfigureAwait(false);
            _doctorRefreshRequired = false;
            Publish(() =>
            {
                Doctor = doctor;
                LastReadError = null;
                Raise(nameof(IsThermalOwnershipReady));
                Raise(nameof(ThermalReadinessReason));
                RaiseGating();
            });

            TelemetrySnapshotDto snapshot = await _client
                .GetTelemetrySnapshotAsync(cancellationToken).ConfigureAwait(false);
            ApplyTelemetry(snapshot.Telemetry, TelemetryOrigin.DiagnosticSnapshot);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RuntimeUiException exception) when (exception.IsDisconnect)
        {
            GoOffline(exception.Message);
            return false;
        }
        catch (RuntimeUiException exception)
        {
            Publish(() => LastReadError = exception.Message);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        (_client as IDisposable)?.Dispose();
    }

    /// <summary>Stops and awaits the background poller before the IPC client is disposed.</summary>
    public async Task StopAsync()
    {
        _lifetime.Cancel();
        Task? loop;
        lock (_startSync)
        {
            loop = _loop;
        }

        if (loop is null)
        {
            return;
        }

        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    private async Task RefreshTelemetryAsync(
        RuntimeStatusDto status,
        CancellationToken cancellationToken)
    {
        if (string.Equals(status.State, "Running", StringComparison.Ordinal) &&
            status.LatestAuthoritativeTelemetry is { } authoritative)
        {
            ApplyTelemetry(authoritative, TelemetryOrigin.ThermalSession);
            return;
        }

        try
        {
            ThermalTelemetrySampleDto sample = await _client
                .GetTelemetrySampleAsync(cancellationToken).ConfigureAwait(false);
            ApplyTelemetry(sample, TelemetryOrigin.ProviderSample);
        }
        catch (RuntimeUiException exception) when (!exception.IsDisconnect)
        {
            // Runtime Core refuses reads during Starting/Stopping transitions. Keep the last
            // known sample and let the staleness indicator tell the truth about its age.
            Publish(() => LastReadError = exception.Message);
        }
    }

    private void ApplyTelemetry(ThermalTelemetrySampleDto sample, TelemetryOrigin origin)
    {
        bool isNew = _lastTelemetryTimestamp != sample.Timestamp;
        _lastTelemetryTimestamp = sample.Timestamp;
        Publish(() =>
        {
            Telemetry = sample;
            TelemetryOrigin = origin;
            Raise(nameof(TelemetryAge));
            Raise(nameof(IsTelemetryStale));
            if (isNew)
            {
                TelemetryObserved?.Invoke(sample);
            }
        });
    }

    private async Task RefreshEventsAsync(long tick, CancellationToken cancellationToken)
    {
        if (tick % EventTickDivisor != 0)
        {
            return;
        }

        try
        {
            long cursor = Interlocked.Read(ref _eventCursor);
            RuntimeEventBatchDto batch = await _client.GetEventsAsync(
                cursor,
                RuntimeIpcDispatcher.MaximumEventBatchSize,
                cancellationToken).ConfigureAwait(false);

            if (cursor > 0 && batch.LatestAvailableSequence < cursor)
            {
                Interlocked.Exchange(ref _eventCursor, 0);
                Publish(() => EventStreamReset?.Invoke());
                return;
            }

            if (batch.Events.Count == 0)
            {
                return;
            }

            RuntimeEventDto[] fresh = batch.Events
                .Where(item => item.Sequence > cursor)
                .OrderBy(item => item.Sequence)
                .ToArray();
            if (fresh.Length == 0)
            {
                return;
            }

            Interlocked.Exchange(ref _eventCursor, fresh[^1].Sequence);
            Publish(() => EventsReceived?.Invoke(fresh, batch.GapDetected));
        }
        catch (RuntimeUiException exception) when (!exception.IsDisconnect)
        {
            Publish(() => LastReadError = exception.Message);
        }
    }

    private async Task RefreshProfilesAsync(CancellationToken cancellationToken)
    {
        if (!_profilesRefreshRequired)
        {
            return;
        }

        _ = await RefreshProfilesNowAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshDoctorAsync(CancellationToken cancellationToken)
    {
        if (!_doctorRefreshRequired)
        {
            return;
        }

        try
        {
            RuntimeDoctorReportDto doctor = await _client.GetDoctorAsync(cancellationToken)
                .ConfigureAwait(false);
            _doctorRefreshRequired = false;
            Publish(() =>
            {
                Doctor = doctor;
                Raise(nameof(IsThermalOwnershipReady));
                Raise(nameof(ThermalReadinessReason));
                RaiseGating();
            });
        }
        catch (RuntimeUiException exception) when (!exception.IsDisconnect)
        {
            _doctorRefreshRequired = false;
            Publish(() => LastReadError = exception.Message);
        }
    }

    private void GoOffline(string reason)
    {
        _statusConnected = false;
        _doctorRefreshRequired = true;
        _profilesRefreshRequired = true;
        Publish(() =>
        {
            State = RuntimeConnectionState.Offline;
            TransportError = reason;
            Doctor = null;
            Raise(nameof(IsTelemetryStale));
            Raise(nameof(IsThermalOwnershipReady));
            Raise(nameof(ThermalReadinessReason));
            RaiseGating();
        });
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            TimeSpan delay = State == RuntimeConnectionState.Online
                ? _pollInterval
                : _reconnectInterval;
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Publish(Action action) => _dispatcher.Post(action);

    private async Task PollOnceCoreAsync(CancellationToken cancellationToken)
    {
        long tick = Interlocked.Increment(ref _tick);
        try
        {
            RuntimeStatusDto status = await _client.GetStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            bool reconnected = !_statusConnected;
            _statusConnected = true;
            _hasEverConnected = true;
            if (reconnected)
            {
                _doctorRefreshRequired = true;
                _profilesRefreshRequired = true;
            }

            Publish(() =>
            {
                if (reconnected)
                {
                    Doctor = null;
                }

                Status = status;
                TransportError = null;
                LastReadError = null;
                State = RuntimeConnectionState.Online;
            });

            await RefreshTelemetryAsync(status, cancellationToken).ConfigureAwait(false);
            await RefreshEventsAsync(tick, cancellationToken).ConfigureAwait(false);
            await RefreshProfilesAsync(cancellationToken).ConfigureAwait(false);
            await RefreshDoctorAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RuntimeUiException exception) when (exception.IsDisconnect)
        {
            GoOffline(exception.Message);
        }
        catch (RuntimeUiException exception)
        {
            Publish(() => LastReadError = exception.Message);
        }

        Publish(() => Updated?.Invoke());
    }

    private void NotifyCommandStateChanged()
    {
        Raise(nameof(IsCommandInFlight));
        Raise(nameof(CanIssueCommand));
        RaiseGating();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        if (name == nameof(State))
        {
            Raise(nameof(IsOnline));
            Raise(nameof(CanIssueCommand));
            RaiseGating();
        }

        return true;
    }

    private void RaiseGating()
    {
        Raise(nameof(CanApplyStaticProfile));
        Raise(nameof(StaticProfileBlockedReason));
        Raise(nameof(CanStartThermalControl));
        Raise(nameof(CanStopThermalControl));
    }

    private void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
