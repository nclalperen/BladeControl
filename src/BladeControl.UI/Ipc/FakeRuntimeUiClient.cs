using System.Diagnostics;
using BladeControl.Runtime;

namespace BladeControl.UI.Ipc;

/// <summary>
/// Scriptable in-memory runtime used by tests and by the <c>--design</c> development
/// preview. It is never selected by a production launch: <see cref="IsLiveRuntimeChannel"/>
/// is false and <see cref="App"/> refuses to bind a non-live client unless the preview
/// switch was passed explicitly on the command line.
/// </summary>
public sealed class FakeRuntimeUiClient : IRuntimeUiClient
{
    private readonly object _sync = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<RuntimeEventDto> _events = [];
    private long _sequence;

    public FakeRuntimeUiClient()
    {
        Status = RuntimeUiSampleData.Status(
            telemetry: RuntimeUiSampleData.Telemetry(),
            health: new TelemetryHealthDto("Healthy", "Telemetry is live.", true),
            watchdog: RuntimeUiSampleData.Watchdog());
        Doctor = RuntimeUiSampleData.Doctor();
        PerformanceState = new PerformanceStateDto(
            new RuntimeModeDto("Balanced", "Auto", "Balanced", "Auto"),
            "Medium",
            "Low",
            0,
            0);
        FanState = new FanStateDto(
            new RuntimeModeDto("Balanced", "Auto", "Balanced", "Auto"),
            0,
            0);
        Append("SessionStopped", "Runtime host ready; no thermal session active.");
    }

    public bool IsLiveRuntimeChannel => false;

    /// <summary>When false every operation fails as a transport error.</summary>
    public bool IsOnline { get; set; } = true;

    /// <summary>Drifts telemetry over time so the preview and graphs look alive.</summary>
    public bool SimulateDrift { get; set; }

    public RuntimeStatusDto Status { get; set; }

    public RuntimeDoctorReportDto Doctor { get; set; }

    public PerformanceStateDto PerformanceState { get; set; }

    public FanStateDto FanState { get; set; }

    public StoredThermalCurveDocument Curve { get; set; } = RuntimeUiSampleData.DefaultCurve();

    /// <summary>
    /// When set, every state-changing command awaits this task before completing. Tests use
    /// it to hold a command "in flight" and assert that duplicate requests are refused.
    /// </summary>
    public TaskCompletionSource? CommandGate { get; set; }

    /// <summary>When set, the next state-changing command is rejected with this message.</summary>
    public string? RejectCommandsWith { get; set; }

    /// <summary>When set, read operations are rejected (mirrors a runtime state transition).</summary>
    public string? RejectReadsWith { get; set; }

    public int StatusRequestCount { get; private set; }

    public int TelemetryRequestCount { get; private set; }

    public int EventRequestCount { get; private set; }

    public int DoctorRequestCount { get; private set; }

    public long LastRequestedEventCursor { get; private set; } = -1;

    public List<ApplyPerformanceProfileRequest> PerformanceRequests { get; } = [];

    public List<ApplyFanProfileRequest> FanRequests { get; } = [];

    public List<string> StartThermalRequests { get; } = [];

    public int StopThermalRequestCount { get; private set; }

    public Task<RuntimeStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOnline();
        lock (_sync)
        {
            StatusRequestCount++;
            if (SimulateDrift && Status.LatestAuthoritativeTelemetry is not null)
            {
                Status = Status with { LatestAuthoritativeTelemetry = Drift() };
            }

            return Task.FromResult(Status);
        }
    }

    public Task<TelemetrySnapshotDto> GetTelemetrySnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOnline();
        EnsureReadable();
        lock (_sync)
        {
            TelemetryRequestCount++;
            return Task.FromResult(new TelemetrySnapshotDto(
                SimulateDrift ? Drift() : RuntimeUiSampleData.Telemetry(),
                [],
                null,
                []));
        }
    }

    public Task<PerformanceStateDto> GetPerformanceStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOnline();
        EnsureReadable();
        return Task.FromResult(PerformanceState);
    }

    public Task<FanStateDto> GetFanStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOnline();
        EnsureReadable();
        return Task.FromResult(FanState);
    }

    public Task<RuntimeDoctorReportDto> GetDoctorAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOnline();
        EnsureReadable();
        lock (_sync)
        {
            DoctorRequestCount++;
            return Task.FromResult(Doctor);
        }
    }

    public Task<RuntimeEventBatchDto> GetEventsAsync(
        long afterSequence,
        int maximumEvents,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOnline();
        lock (_sync)
        {
            EventRequestCount++;
            LastRequestedEventCursor = afterSequence;
            RuntimeEventDto[] batch = _events
                .Where(item => item.Sequence > afterSequence)
                .Take(maximumEvents)
                .ToArray();
            long oldest = _events.Count == 0 ? 0 : _events[0].Sequence;
            long latest = _events.Count == 0 ? 0 : _events[^1].Sequence;
            return Task.FromResult(new RuntimeEventBatchDto(
                Status,
                batch,
                oldest,
                latest,
                afterSequence > 0 && oldest > afterSequence + 1));
        }
    }

    public Task<IReadOnlyList<string>> ListBuiltInCurvesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOnline();
        return Task.FromResult<IReadOnlyList<string>>(["default"]);
    }

    public Task<StoredThermalCurveDocument> GetThermalCurveAsync(
        string name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOnline();
        if (!string.Equals(name, "default", StringComparison.Ordinal))
        {
            throw new RuntimeUiException(
                RuntimeUiFailureKind.Rejected,
                "Unknown thermal curve.");
        }

        return Task.FromResult(Curve);
    }

    public async Task<RuntimeCommandResultDto> ApplyPerformanceProfileAsync(
        ApplyPerformanceProfileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_sync)
        {
            PerformanceRequests.Add(request);
        }

        await BeginCommandAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            PerformanceState = new PerformanceStateDto(
                new RuntimeModeDto(request.Mode, "Auto", request.Mode, "Auto"),
                request.CpuLevel ?? PerformanceState.CpuLevel,
                request.GpuLevel ?? PerformanceState.GpuLevel,
                PerformanceState.Fan1Rpm,
                PerformanceState.Fan2Rpm);
            Append("RecoveryResult", $"Performance profile applied: {request.Mode}.");
            return new RuntimeCommandResultDto(true, "Applied", "Verified against firmware.");
        }
    }

    public async Task<RuntimeCommandResultDto> ApplyFanProfileAsync(
        ApplyFanProfileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_sync)
        {
            FanRequests.Add(request);
        }

        await BeginCommandAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            FanState = new FanStateDto(
                new RuntimeModeDto(
                    FanState.Mode.Zone1PerformanceMode,
                    request.Mode == "Auto" ? "Auto" : "Manual",
                    FanState.Mode.Zone2PerformanceMode,
                    request.Mode == "Auto" ? "Auto" : "Manual"),
                request.Fan1Rpm ?? 0,
                request.Fan2Rpm ?? 0);
            Status = Status with
            {
                CurrentEffectiveFanTargetRpm = request.Mode == "Auto" ? null : request.Fan1Rpm
            };
            Append("FanTargetChanged", $"Fan profile applied: {request.Mode}.");
            return new RuntimeCommandResultDto(true, "Applied", "Verified against firmware.");
        }
    }

    public async Task<RuntimeStatusDto> StartThermalControlAsync(
        string curve,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            StartThermalRequests.Add(curve);
        }

        await BeginCommandAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            Status = Status with
            {
                State = "Running",
                SessionId = Guid.NewGuid(),
                StartTimestamp = DateTimeOffset.UtcNow,
                CurrentProfile = curve,
                CurrentEffectiveFanTargetRpm = 3300,
                LatestAuthoritativeTelemetry =
                    Status.LatestAuthoritativeTelemetry ?? RuntimeUiSampleData.Telemetry(),
                TelemetryHealth = new TelemetryHealthDto("Healthy", "Telemetry is live.", true)
            };
            Append("SessionStarted", $"Thermal session started on curve '{curve}'.");
            return Status;
        }
    }

    public async Task<StopThermalControlResultDto> StopThermalControlAsync(
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            StopThermalRequestCount++;
        }

        await BeginCommandAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            bool wasActive = Status.SessionId.HasValue;
            Status = Status with
            {
                State = "Stopped",
                SessionId = null,
                StartTimestamp = null,
                CurrentProfile = null,
                CurrentEffectiveFanTargetRpm = null
            };
            Append("SessionStopped", "Thermal session stopped; cooling returned to firmware Auto.");
            return new StopThermalControlResultDto(
                wasActive,
                true,
                wasActive
                    ? "Thermal session stopped and cooling handed back to firmware Auto."
                    : "No thermal-control session was active.",
                Status);
        }
    }

    /// <summary>Appends a runtime event, mirroring the bounded server-side log.</summary>
    public void Append(string kind, string message)
    {
        lock (_sync)
        {
            _events.Add(RuntimeUiSampleData.Event(kind, ++_sequence, message));
            if (_events.Count > 256)
            {
                _events.RemoveAt(0);
            }

            Status = Status with { TotalEventCount = _sequence };
        }
    }

    private async Task BeginCommandAsync(CancellationToken cancellationToken)
    {
        EnsureOnline();
        TaskCompletionSource? gate = CommandGate;
        if (gate is not null)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (RejectCommandsWith is { } rejection)
        {
            throw new RuntimeUiException(RuntimeUiFailureKind.Rejected, rejection);
        }
    }

    private void EnsureOnline()
    {
        if (!IsOnline)
        {
            throw new RuntimeUiException(
                RuntimeUiFailureKind.Transport,
                "Runtime Core is not reachable (simulated).");
        }
    }

    private void EnsureReadable()
    {
        if (RejectReadsWith is { } rejection)
        {
            throw new RuntimeUiException(RuntimeUiFailureKind.Rejected, rejection);
        }
    }

    private ThermalTelemetrySampleDto Drift()
    {
        double seconds = _clock.Elapsed.TotalSeconds;
        return RuntimeUiSampleData.Telemetry(
            cpuTemperature: 58 + (12 * Math.Sin(seconds / 9)) + (3 * Math.Sin(seconds / 1.7)),
            gpuTemperature: 49 + (9 * Math.Sin((seconds / 11) + 1.2)),
            cpuPower: 32 + (18 * Math.Abs(Math.Sin(seconds / 6))),
            cpuLoad: 14 + (30 * Math.Abs(Math.Sin(seconds / 4.5))),
            gpuPower: 20 + (26 * Math.Abs(Math.Sin((seconds / 7) + 0.6))),
            gpuUtilization: 6 + (40 * Math.Abs(Math.Sin((seconds / 5) + 2.1))));
    }
}
