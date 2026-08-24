using BladeControl.Runtime;
using BladeControl.UI.Ipc;
using BladeControl.UI.Services;

namespace BladeControl.UI.ViewModels;

public sealed class DashboardViewModel : PageViewModel
{
    private readonly PerformanceViewModel _performance;

    public DashboardViewModel(
        RuntimeConnection connection,
        PerformanceViewModel performance,
        CancellationToken lifetime)
        : base(
            connection,
            lifetime,
            "Dashboard",
            "Dashboard",
            "Machine state reported by Runtime Core",
            Icons.Dashboard)
    {
        _performance = performance ?? throw new ArgumentNullException(nameof(performance));
        ApplyBalancedCommand = new AsyncRelayCommand(
            () => ApplyModeAsync("Balanced"),
            () => Connection.CanApplyStaticProfile);
        ApplySilentCommand = new AsyncRelayCommand(
            () => ApplyModeAsync("Silent"),
            () => Connection.CanApplyStaticProfile);
        ApplyCustomCommand = new AsyncRelayCommand(
            () => ApplyModeAsync("Custom"),
            () => Connection.CanApplyStaticProfile && _performance.CanApplyCustomSelection);
        StartCoolingCommand = new AsyncRelayCommand(
            StartCoolingAsync,
            () => Connection.CanStartThermalControl);
        StopCoolingCommand = new AsyncRelayCommand(
            StopCoolingAsync,
            () => Connection.CanStopThermalControl);
    }

    public AsyncRelayCommand ApplyBalancedCommand { get; }

    public AsyncRelayCommand ApplySilentCommand { get; }

    public AsyncRelayCommand ApplyCustomCommand { get; }

    public AsyncRelayCommand StartCoolingCommand { get; }

    public AsyncRelayCommand StopCoolingCommand { get; }

    // CPU -------------------------------------------------------------------
    public string CpuTemperature =>
        Display.Metric(Telemetry?.CpuPackageTemperatureCelsius, "0.0", "°C");

    public string? CpuTemperatureDetail =>
        Display.MetricDetail(Telemetry?.CpuPackageTemperatureCelsius);

    public string CpuPower => Display.Metric(Telemetry?.CpuPackagePowerWatts, "0.0", "W");

    public string? CpuPowerDetail => Display.MetricDetail(Telemetry?.CpuPackagePowerWatts);

    public string CpuLoad => Display.Metric(Telemetry?.CpuTotalLoadPercent, "0", "%");

    public string? CpuLoadDetail => Display.MetricDetail(Telemetry?.CpuTotalLoadPercent);

    // GPU -------------------------------------------------------------------
    public string GpuTemperature =>
        Display.Metric(Telemetry?.GpuTemperatureCelsius, "0.0", "°C");

    public string? GpuTemperatureDetail =>
        Display.MetricDetail(Telemetry?.GpuTemperatureCelsius);

    public string GpuPower => Display.Metric(Telemetry?.GpuPowerWatts, "0.0", "W");

    public string? GpuPowerDetail => Display.MetricDetail(Telemetry?.GpuPowerWatts);

    public string GpuUtilization => Display.Metric(Telemetry?.GpuUtilizationPercent, "0", "%");

    public string? GpuUtilizationDetail => Display.MetricDetail(Telemetry?.GpuUtilizationPercent);

    // Runtime ---------------------------------------------------------------
    public string ConnectionText => Connection.State switch
    {
        RuntimeConnectionState.Online => "Online",
        RuntimeConnectionState.Connecting => "Connecting",
        _ => "Offline"
    };

    public StatusTone ConnectionTone => Connection.State switch
    {
        RuntimeConnectionState.Online => StatusTone.Good,
        RuntimeConnectionState.Connecting => StatusTone.Warning,
        _ => StatusTone.Danger
    };

    public string RuntimeState => !Connection.IsOnline &&
        !string.IsNullOrWhiteSpace(Connection.RuntimeStateName)
        ? $"Last reported · {Connection.RuntimeStateName}"
        : Display.Text(Connection.RuntimeStateName);

    public StatusTone RuntimeStateTone => Connection.IsOnline
        ? Display.RuntimeStateTone(Connection.RuntimeStateName)
        : StatusTone.Muted;

    public string RuntimeStateDescription => !Connection.IsOnline
        ? string.IsNullOrWhiteSpace(Connection.RuntimeStateName)
            ? "Runtime Core is offline; no runtime state has been reported."
            : "Runtime Core is offline; this is the last state it reported."
        : Display.RuntimeStateDescription(
            Connection.RuntimeStateName,
            Status?.CurrentProfile);

    /// <summary>True when the runtime has served no thermal session since it started.</summary>
    /// <remarks>
    /// Distinct from stopped. A stopped runtime that ran a session has a last session to report;
    /// one that has never run a session does not, and labelling its zeroes "LAST SESSION
    /// SCHEDULER ... Healthy" describes a session that never happened. Active transition/state,
    /// SessionId, and completed cycles are all evidence: the latter retains compatibility with
    /// a status that has measurements but no ID.
    /// </remarks>
    public bool HasNoSessionHistory => Status is not null &&
        !Display.HasThermalSessionEvidence(Status);

    public string SchedulerHealth => Status is null
        ? Display.Unavailable
        : HasNoSessionHistory
        ? Observation == SessionObservationScope.LastReported
            ? "Last reported · no thermal session since runtime start"
            : "No thermal session since the runtime started"
        : !HasSchedulerMeasurements
            ? "No scheduler cycle has completed yet"
            : Display.Text(Status?.SchedulerHealth);

    public string SchedulerLabel => HasNoSessionHistory
        ? Observation == SessionObservationScope.LastReported
            ? "LAST REPORTED SCHEDULER"
            : "SCHEDULER"
        : Observation switch
        {
            SessionObservationScope.Current => "SCHEDULER",
            SessionObservationScope.Starting => "SCHEDULER · SESSION STARTING",
            SessionObservationScope.Stopping => "SCHEDULER · SESSION STOPPING",
            SessionObservationScope.LastReported => "LAST REPORTED SCHEDULER",
            SessionObservationScope.LastSession => "LAST SESSION SCHEDULER",
            _ => "SCHEDULER"
        };

    public StatusTone SchedulerHealthTone =>
        Status is null || HasNoSessionHistory || !HasSchedulerMeasurements ||
        Observation != SessionObservationScope.Current
        ? StatusTone.Muted
        : Display.SchedulerTone(Status?.SchedulerHealth);

    public string TelemetryHealth => Status is null
        ? Display.Unavailable
        : HasNoSessionHistory
        ? Observation == SessionObservationScope.LastReported
            ? Display.LastReportedNoSessionTelemetry
            : Display.NoSessionTelemetry
        : Status?.TelemetryHealth is { } health
        ? health.IsHealthy ? "Healthy" : health.Kind
        : Display.Unavailable;

    public string? TelemetryHealthDetail => Status is null
        ? null
        : HasNoSessionHistory
        ? Observation == SessionObservationScope.LastReported
            ? Display.LastReportedNoSessionTelemetryDetail
            : Display.NoSessionTelemetryDetail
        : Status?.TelemetryHealth?.Reason;

    public string TelemetryLabel => HasNoSessionHistory
        ? Observation == SessionObservationScope.LastReported
            ? "LAST REPORTED TELEMETRY"
            : "TELEMETRY"
        : Observation switch
        {
            SessionObservationScope.Current => "TELEMETRY",
            SessionObservationScope.Starting => "TELEMETRY · SESSION STARTING",
            SessionObservationScope.Stopping => "TELEMETRY · SESSION STOPPING",
            SessionObservationScope.LastReported => "LAST REPORTED TELEMETRY",
            SessionObservationScope.LastSession => "LAST SESSION TELEMETRY",
            _ => "TELEMETRY"
        };

    public StatusTone TelemetryHealthTone => Status is null || HasNoSessionHistory ||
        Observation != SessionObservationScope.Current
        ? StatusTone.Muted
        : Display.HealthTone(Status?.TelemetryHealth);

    public string ThermalSession => !Connection.IsOnline &&
        !string.IsNullOrWhiteSpace(Connection.RuntimeStateName)
        ? $"Last reported · {Display.ThermalSession(Connection.RuntimeStateName)}"
        : Display.ThermalSession(Connection.RuntimeStateName);

    public StatusTone ThermalSessionTone => Connection.IsOnline
        ? Display.RuntimeStateTone(Connection.RuntimeStateName)
        : StatusTone.Muted;

    public string SessionId => Status?.SessionId is { } id
        ? id.ToString("D")
        : Display.Unavailable;

    /// <summary>Freshness label. Stale telemetry is never presented as live.</summary>
    public string TelemetryFreshness
        => TelemetryFreshnessPresentation.Text;

    public StatusTone TelemetryFreshnessTone => TelemetryFreshnessPresentation.Tone;

    // Cooling ---------------------------------------------------------------
    public string FanTarget => Display.HasCurrentFanTarget(
        Connection.IsOnline,
        Connection.RuntimeStateName,
        Status?.CurrentProfile)
        ? Display.Rpm(Status?.CurrentEffectiveFanTargetRpm)
        : Display.Unavailable;

    public string FanTargetDetail
    {
        get
        {
            if (!Display.HasCurrentFanTarget(
                    Connection.IsOnline,
                    Connection.RuntimeStateName,
                    Status?.CurrentProfile))
            {
                if (!Connection.IsOnline)
                {
                    return "No current target — Runtime Core is offline.";
                }

                return Connection.RuntimeStateName switch
                {
                    "EmergencyHandoff" =>
                        "No current target — firmware Auto owns cooling.",
                    "Faulted" =>
                        "No current target — cooling ownership is uncertain; check Diagnostics.",
                    "Starting" or "Stopping" =>
                        "No current target is shown while cooling ownership is changing.",
                    _ => "No current fan target is available."
                };
            }

            if (Status?.CurrentEffectiveFanTargetRpm is not null)
            {
                return "Target commanded by Runtime Core.";
            }

            return Display.IsLiveSession(Connection.RuntimeStateName)
                ? "Runtime Core has not reported a commanded target."
                : "Runtime Core reports a Fixed profile but no commanded target.";
        }
    }

    /// <summary>
    /// Firmware fan mode as reported by the last Razer watchdog check. This is the
    /// firmware's Auto/Manual flag, not a measured fan speed.
    /// </summary>
    public string FirmwareFanMode
    {
        get
        {
            RuntimeRazerModeStateDto? watchdog = Status?.LastRazerWatchdogState;
            if (watchdog is null)
            {
                return Display.Unavailable;
            }

            return watchdog.IsKnownAuto
                ? "Auto"
                : watchdog.IsAuto ? "Auto (unconfirmed)" : watchdog.Zone1FanMode;
        }
    }

    public string FirmwareFanModeLabel => Status?.LastRazerWatchdogState is null ||
        Observation == SessionObservationScope.Current
        ? "FIRMWARE MODE"
        : "LAST WATCHDOG OBSERVATION";

    public string FirmwareFan1Value => Display.HasCurrentCoolingSnapshot(
        Connection.IsOnline,
        Connection.RuntimeStateName)
        ? Display.FirmwareFanValue(Connection.Fan?.Fan1Rpm ?? 0)
        : Display.Unavailable;

    public string FirmwareFan2Value => Display.HasCurrentCoolingSnapshot(
        Connection.IsOnline,
        Connection.RuntimeStateName)
        ? Display.FirmwareFanValue(Connection.Fan?.Fan2Rpm ?? 0)
        : Display.Unavailable;

    // Performance -----------------------------------------------------------
    public string PerformanceMode => !Connection.IsOnline && Connection.Performance is not null
        ? $"Last reported · {Display.Text(Connection.Performance.Mode.Zone1PerformanceMode)}"
        : Display.Text(Connection.Performance?.Mode.Zone1PerformanceMode);

    public string CpuPerformanceLevel => Display.Text(Connection.Performance?.CpuLevel);

    public string GpuPerformanceLevel => Display.Text(Connection.Performance?.GpuLevel);

    public string CustomButtonLabel
    {
        get
        {
            (string cpu, string gpu) = _performance.CustomLevels;
            return $"Custom · CPU {cpu} / GPU {gpu}";
        }
    }

    // Gating ----------------------------------------------------------------
    public string? ProfileBlockedReason => Connection.StaticProfileBlockedReason;

    public bool HasProfileBlockedReason => !string.IsNullOrEmpty(ProfileBlockedReason);

    public string? StartBlockedReason => !Connection.IsOnline
        ? null
        : Connection.CanStartThermalControl
        ? null
        : Connection.IsThermalOwnershipReady
            ? Connection.StaticProfileBlockedReason ??
                $"Runtime Core is {Display.Text(Connection.RuntimeStateName)}."
            : Connection.ThermalReadinessReason;

    public bool HasStartBlockedReason => !string.IsNullOrEmpty(StartBlockedReason);

    /// <summary>Set when Runtime Core reports an emergency handoff or a fault.</summary>
    public string? RuntimeAlert => RuntimeAlertPresentation.Text;

    public bool HasRuntimeAlert => RuntimeAlertPresentation.Text is not null;

    /// <summary>How loudly to render the alert. Not everything in this banner is a fault.</summary>
    /// <remarks>
    /// An emergency handoff is a latched but <i>safe</i> state: the ladder did its job, firmware
    /// owns cooling, and nothing is broken. The banner was hardcoded to the danger palette, so it
    /// rendered in red while its own text said "the machine is safe" — the colour contradicting
    /// the sentence. Protection having worked is not the same as protection having failed, and
    /// this is the same distinction already drawn in <c>Display.EmergencyHandoff</c>.
    /// <para>A fault stays danger-red, because a fault is a fault.</para>
    /// </remarks>
    public StatusTone RuntimeAlertTone => RuntimeAlertPresentation.Tone;

    private RuntimeStatusDto? Status => Connection.Status;

    private ThermalTelemetrySampleDto? Telemetry => Connection.Telemetry;

    private SessionObservationScope Observation => Display.SessionObservation(
        Connection.IsOnline,
        Connection.RuntimeStateName);

    private bool IsHistoricalSession =>
        Observation != SessionObservationScope.Current;

    private bool HasSchedulerMeasurements =>
        Status?.Scheduler is { CompletedCycles: > 0 };

    private (string? Text, StatusTone Tone) RuntimeAlertPresentation
    {
        get
        {
            if (Status is null)
            {
                return (null, StatusTone.Muted);
            }

            if (!Connection.IsOnline)
            {
                if (!string.IsNullOrWhiteSpace(Status.EmergencyStatus))
                {
                    return ($"Last reported emergency handoff: {Status.EmergencyStatus} " +
                        "Runtime Core is offline.", StatusTone.Muted);
                }

                if (!string.IsNullOrWhiteSpace(Status.LastFailureReason))
                {
                    return ($"Last reported runtime failure: {Status.LastFailureReason} " +
                        "Runtime Core is offline.", StatusTone.Muted);
                }

                return Status.State switch
                {
                    "EmergencyHandoff" =>
                        ("Last reported runtime state: Emergency handoff. Runtime Core is " +
                            "offline.", StatusTone.Muted),
                    "Faulted" =>
                        ("Last reported runtime state: Faulted. Runtime Core is offline.",
                            StatusTone.Muted),
                    _ => (null, StatusTone.Muted)
                };
            }

            if (!string.IsNullOrWhiteSpace(Status.EmergencyStatus))
            {
                StatusTone tone = Display.RuntimeStateTone(Status.State);
                return ($"Emergency handoff: {Status.EmergencyStatus}",
                    tone is StatusTone.Warning or StatusTone.Danger
                        ? tone
                        : StatusTone.Warning);
            }

            if (!string.IsNullOrWhiteSpace(Status.LastFailureReason))
            {
                return ($"Runtime failure: {Status.LastFailureReason}", StatusTone.Danger);
            }

            // A refused start is deliberately not an alert. It is already reported by the
            // operation that was refused, in that operation's own status message, and the
            // runtime is Stopped and healthy. Raising a second banner for it duplicated the
            // text and called a safe refusal a failure.
            return Status.State switch
            {
                // Say what happened, that the machine is safe, and what to do — in that order.
                // This state is only reported after firmware Auto is verified.
                "EmergencyHandoff" =>
                    ("Emergency handoff: a thermal limit was reached and cooling was handed " +
                        "back to firmware Auto. The machine is safe and firmware owns cooling. " +
                        "The service is still running, but the thermal session has ended and " +
                        "Dynamic will not resume by itself — start it again deliberately once " +
                        "the machine has cooled.", Display.RuntimeStateTone(Status.State)),
                "Faulted" =>
                    ("Runtime Core is faulted. Open Diagnostics before attempting recovery.",
                        Display.RuntimeStateTone(Status.State)),
                _ => (null, StatusTone.Muted)
            };
        }
    }

    private (string Text, StatusTone Tone) TelemetryFreshnessPresentation
    {
        get
        {
            if (Telemetry is null)
            {
                return ("No telemetry yet", StatusTone.Muted);
            }

            if (!Connection.IsOnline)
            {
                return ("Last known — Runtime Core offline", StatusTone.Muted);
            }

            TimeSpan? age = Connection.TelemetryAge;
            if (Connection.IsTelemetryStale)
            {
                if (IsHistoricalSession)
                {
                    return ($"Last known telemetry · " +
                        $"{Display.ThermalSession(Connection.RuntimeStateName)}",
                        StatusTone.Muted);
                }

                return (age is { } value
                    ? $"Stale — {value.TotalSeconds:0} s old"
                    : "Stale", StatusTone.Warning);
            }

            // A fresh sample without a running session is a monitoring read, not a live
            // session. The age is still reported because it is true and useful; only the
            // ownership claim changes. "Monitoring" is already used by the compact panel for
            // this exact state, so the two surfaces keep one word for one fact.
            if (IsHistoricalSession)
            {
                return (age is { } idle
                    ? $"Monitoring — {idle.TotalSeconds:0} s old"
                    : "Monitoring", StatusTone.Muted);
            }

            // Where a live sample came from is real information, but it is engineering
            // vocabulary: the dashboard answers is-it-live and how-old; Diagnostics carries
            // the provenance.
            return (age is { } live
                ? $"Live — {live.TotalSeconds:0} s old"
                : "Live", StatusTone.Good);
        }
    }

    public override void Refresh()
    {
        RaiseAll(
            nameof(CpuTemperature), nameof(CpuTemperatureDetail),
            nameof(CpuPower), nameof(CpuPowerDetail),
            nameof(CpuLoad), nameof(CpuLoadDetail),
            nameof(GpuTemperature), nameof(GpuTemperatureDetail),
            nameof(GpuPower), nameof(GpuPowerDetail),
            nameof(GpuUtilization), nameof(GpuUtilizationDetail),
            nameof(ConnectionText), nameof(ConnectionTone),
            nameof(RuntimeState), nameof(RuntimeStateTone), nameof(RuntimeStateDescription),
            nameof(SchedulerHealth), nameof(SchedulerLabel), nameof(SchedulerHealthTone),
            nameof(HasNoSessionHistory),
            nameof(TelemetryHealth), nameof(TelemetryLabel),
            nameof(TelemetryHealthDetail), nameof(TelemetryHealthTone),
            nameof(ThermalSession), nameof(ThermalSessionTone), nameof(SessionId),
            nameof(TelemetryFreshness), nameof(TelemetryFreshnessTone),
            nameof(FanTarget), nameof(FanTargetDetail),
            nameof(FirmwareFanMode), nameof(FirmwareFanModeLabel),
            nameof(FirmwareFan1Value), nameof(FirmwareFan2Value),
            nameof(PerformanceMode), nameof(CpuPerformanceLevel), nameof(GpuPerformanceLevel),
            nameof(CustomButtonLabel),
            nameof(ProfileBlockedReason), nameof(HasProfileBlockedReason),
            nameof(StartBlockedReason), nameof(HasStartBlockedReason),
            nameof(RuntimeAlert), nameof(HasRuntimeAlert), nameof(RuntimeAlertTone));
        ApplyBalancedCommand.RaiseCanExecuteChanged();
        ApplySilentCommand.RaiseCanExecuteChanged();
        ApplyCustomCommand.RaiseCanExecuteChanged();
        StartCoolingCommand.RaiseCanExecuteChanged();
        StopCoolingCommand.RaiseCanExecuteChanged();
    }

    public override void Activate() => Refresh();

    private async Task ApplyModeAsync(string mode)
    {
        bool custom = string.Equals(mode, "Custom", StringComparison.Ordinal);
        if (custom && !_performance.CanApplyCustomSelection)
        {
            return;
        }

        (string cpu, string gpu) = _performance.CustomLevels;
        await RunCommandAsync(async (client, token) =>
        {
            RuntimeCommandResultDto result = await client.ApplyPerformanceProfileAsync(
                new ApplyPerformanceProfileRequest(
                    mode,
                    custom ? cpu : null,
                    custom ? gpu : null),
                token).ConfigureAwait(false);
            string message = string.IsNullOrWhiteSpace(result.Message)
                ? $"{mode}: {result.Outcome ?? "applied"}."
                : $"{mode}: {result.Outcome ?? "applied"} — {result.Message}";
            return result.Succeeded
                ? RuntimeCommandOutcome.Ok(message)
                : RuntimeCommandOutcome.Fail(message);
        }).ConfigureAwait(true);
    }

    private async Task StartCoolingAsync() =>
        await RunCommandAsync(async (client, token) =>
        {
            RuntimeStatusDto status = await client
                .StartThermalControlAsync("default", token).ConfigureAwait(false);
            Connection.AcceptCommandStatus(status);
            return Display.IsLiveSession(status.State)
                ? RuntimeCommandOutcome.Ok(
                    "Dynamic cooling started on the built-in curve.")
                : RuntimeCommandOutcome.Fail(
                    status.LastFailureReason ??
                    $"Runtime Core reported {status.State} instead of Running.");
        }).ConfigureAwait(true);

    private async Task StopCoolingAsync() =>
        await RunCommandAsync(async (client, token) =>
        {
            StopThermalControlResultDto result = await client
                .StopThermalControlAsync(token).ConfigureAwait(false);
            Connection.AcceptCommandStatus(result.FinalStatus);
            return result.Succeeded
                ? RuntimeCommandOutcome.Ok(result.Message)
                : RuntimeCommandOutcome.Fail(result.Message);
        }).ConfigureAwait(true);
}
