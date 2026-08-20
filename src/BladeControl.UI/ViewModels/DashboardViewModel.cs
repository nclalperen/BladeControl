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
            "Live machine state reported by Runtime Core",
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

    public string RuntimeState => Display.Text(Connection.RuntimeStateName);

    public StatusTone RuntimeStateTone => Display.RuntimeStateTone(Connection.RuntimeStateName);

    public string RuntimeStateDescription =>
        Display.RuntimeStateDescription(Connection.RuntimeStateName);

    public string SchedulerHealth => Display.Text(Status?.SchedulerHealth);

    public string SchedulerLabel => IsStopped ? "LAST SESSION SCHEDULER" : "SCHEDULER";

    public StatusTone SchedulerHealthTone => IsStopped
        ? StatusTone.Muted
        : Display.SchedulerTone(Status?.SchedulerHealth);

    public string TelemetryHealth => Status?.TelemetryHealth is { } health
        ? health.IsHealthy ? "Healthy" : health.Kind
        : Display.Unavailable;

    public string? TelemetryHealthDetail => Status?.TelemetryHealth?.Reason;

    public string TelemetryLabel => IsStopped ? "LAST SESSION TELEMETRY" : "TELEMETRY";

    public StatusTone TelemetryHealthTone => IsStopped
        ? StatusTone.Muted
        : Display.HealthTone(Status?.TelemetryHealth);

    public string ThermalSession => Display.ThermalSession(Connection.RuntimeStateName);

    public StatusTone ThermalSessionTone => Display.RuntimeStateTone(Connection.RuntimeStateName);

    public string SessionId => Status?.SessionId is { } id
        ? id.ToString("D")
        : Display.Unavailable;

    /// <summary>Freshness label. Stale telemetry is never presented as live.</summary>
    public string TelemetryFreshness
    {
        get
        {
            if (Telemetry is null)
            {
                return "No telemetry yet";
            }

            if (IsStopped && Connection.IsTelemetryStale)
            {
                return "Last session telemetry · Stopped";
            }

            if (!Connection.IsOnline)
            {
                return "Last known — Runtime Core offline";
            }

            TimeSpan? age = Connection.TelemetryAge;
            if (Connection.IsTelemetryStale)
            {
                return age is { } value
                    ? $"Stale — {value.TotalSeconds:0} s old"
                    : "Stale";
            }

            // Where a live sample came from is real information, but it is engineering
            // vocabulary: "provider-only sample" tells a user nothing they can act on, and the
            // distinction between acquisition routes is a debugging concern. The dashboard
            // answers is-it-live and how-old; Diagnostics carries the provenance.
            return age is { } live
                ? $"Live — {live.TotalSeconds:0} s old"
                : "Live";
        }
    }

    public StatusTone TelemetryFreshnessTone => Telemetry is null
        ? StatusTone.Muted
        : IsStopped ? StatusTone.Muted
        : Connection.IsTelemetryStale ? StatusTone.Warning : StatusTone.Good;

    // Cooling ---------------------------------------------------------------
    public string FanTarget => Display.Rpm(Status?.CurrentEffectiveFanTargetRpm);

    public string FanTargetDetail => Status?.CurrentEffectiveFanTargetRpm is null
        ? "No commanded target — firmware Auto owns the fans."
        : "Target commanded by Runtime Core.";

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

    public string FirmwareFanModeLabel => IsStopped
        ? "LAST WATCHDOG OBSERVATION"
        : "FIRMWARE MODE";

    public string FirmwareFan1Value => Display.FirmwareFanValue(Connection.Fan?.Fan1Rpm ?? 0);

    public string FirmwareFan2Value => Display.FirmwareFanValue(Connection.Fan?.Fan2Rpm ?? 0);

    // Performance -----------------------------------------------------------
    public string PerformanceMode =>
        Display.Text(Connection.Performance?.Mode.Zone1PerformanceMode);

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
    public string? RuntimeAlert
    {
        get
        {
            if (Status is null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(Status.EmergencyStatus))
            {
                return $"Emergency handoff: {Status.EmergencyStatus}";
            }

            if (!string.IsNullOrWhiteSpace(Status.LastFailureReason))
            {
                return $"Runtime failure: {Status.LastFailureReason}";
            }

            // A refused start is deliberately not an alert. It is already reported by the
            // operation that was refused, in that operation's own status message, and the
            // runtime is Stopped and healthy. Raising a second banner for it duplicated the
            // text and called a safe refusal a failure.

            if (string.Equals(Status.State, "EmergencyHandoff", StringComparison.Ordinal))
            {
                // Say what happened, that the machine is safe, and what to do — in that order.
                // The previous wording described the handoff as still happening, which is both
                // untrue by the time this state is reported and needlessly alarming. The
                // "Emergency handoff" lead is kept: it is the term users and support search for.
                return "Emergency handoff: a thermal limit was reached and cooling was handed " +
                    "back to firmware Auto. The machine is safe. Restart the runtime to resume " +
                    "thermal control.";
            }

            if (string.Equals(Status.State, "Faulted", StringComparison.Ordinal))
            {
                return "Runtime Core is faulted. Open Diagnostics before attempting recovery.";
            }

            return null;
        }
    }

    public bool HasRuntimeAlert => !string.IsNullOrEmpty(RuntimeAlert);

    private RuntimeStatusDto? Status => Connection.Status;

    private ThermalTelemetrySampleDto? Telemetry => Connection.Telemetry;

    private bool IsStopped =>
        string.Equals(Connection.RuntimeStateName, "Stopped", StringComparison.Ordinal);

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
            nameof(RuntimeAlert), nameof(HasRuntimeAlert));
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
            return string.Equals(status.State, "Running", StringComparison.Ordinal)
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
