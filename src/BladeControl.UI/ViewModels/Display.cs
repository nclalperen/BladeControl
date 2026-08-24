using System.Globalization;
using BladeControl.Runtime;

namespace BladeControl.UI.ViewModels;

/// <summary>Semantic colour role for a status readout.</summary>
public enum StatusTone
{
    Neutral,
    Good,
    Warning,
    Danger,
    Muted
}

/// <summary>How a cached session observation relates to the runtime now.</summary>
public enum SessionObservationScope
{
    Unavailable,
    Current,
    Starting,
    Stopping,
    LastSession,
    LastReported
}

/// <summary>Presentation helpers shared by the pages. Formatting only, no runtime logic.</summary>
public static class Display
{
    public const string Unavailable = "—";
    public const string NoSessionTelemetry = "No session telemetry yet";
    public const string NoSessionTelemetryDetail =
        "The runtime has not run a thermal session since it started.";
    public const string LastReportedNoSessionTelemetry =
        "Last reported · no session telemetry yet";
    public const string LastReportedNoSessionTelemetryDetail =
        "Retained status snapshot; no session telemetry had been reported before disconnect.";

    public static string Metric(
        TelemetryMetricDto<double>? metric,
        string format = "0.0",
        string unit = "")
    {
        if (metric is null || !metric.HasValue || !metric.IsValid || metric.Value is not { } value)
        {
            return Unavailable;
        }

        string text = value.ToString(format, CultureInfo.CurrentCulture);
        return string.IsNullOrEmpty(unit) ? text : $"{text} {unit}";
    }

    /// <summary>
    /// Why a reading is missing. Returns the provider's own diagnostic so a sensor problem is
    /// never silently rendered as a dash.
    /// </summary>
    public static string? MetricDetail(TelemetryMetricDto<double>? metric)
    {
        if (metric is null)
        {
            return "No telemetry sample yet.";
        }

        if (metric.HasValue && metric.IsValid)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(metric.Diagnostic))
        {
            return metric.Diagnostic;
        }

        return metric.IsSupported
            ? "The provider returned no valid reading."
            : "Not supported on this machine.";
    }

    public static string Rpm(int? rpm) => rpm is { } value
        ? $"{value.ToString("N0", CultureInfo.CurrentCulture)} RPM"
        : Unavailable;

    public static string Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Unavailable : value;

    public static string Duration(TimeSpan value) =>
        value.TotalMilliseconds < 1000
            ? $"{value.TotalMilliseconds.ToString("0.0", CultureInfo.CurrentCulture)} ms"
            : $"{value.TotalSeconds.ToString("0.00", CultureInfo.CurrentCulture)} s";

    public static string Boolean(bool value) => value ? "Yes" : "No";

    public static StatusTone BooleanTone(bool value) => value ? StatusTone.Good : StatusTone.Danger;

    /// <summary>
    /// Runtime state colouring, which distinguishes protection working from protection failing.
    /// </summary>
    /// <remarks>
    /// <para>EmergencyHandoff is <see cref="StatusTone.Warning"/>, not Danger. The runtime
    /// reaches that state only after firmware Auto has been established and verified — cooling
    /// is safely with the firmware and the machine is fine. It warrants attention because a
    /// thermal event occurred and the session will not resume by itself, which is what Warning
    /// means.</para>
    /// <para>Faulted keeps Danger. That is the state where the handoff could <i>not</i> be
    /// established, so who owns cooling is genuinely uncertain. The runtime went to the trouble
    /// of separating these two outcomes; painting them the same colour throws that away and
    /// tells a user their machine is broken when the safety system just did its job.</para>
    /// </remarks>
    public static StatusTone RuntimeStateTone(string? state) => state switch
    {
        "Running" => StatusTone.Good,
        "Starting" or "Stopping" => StatusTone.Warning,
        "EmergencyHandoff" => StatusTone.Warning,
        "Faulted" => StatusTone.Danger,
        "Stopped" => StatusTone.Neutral,
        _ => StatusTone.Muted
    };

    /// <summary>
    /// Whether a thermal session is actually running, and therefore whether telemetry may be
    /// presented as live session data.
    /// </summary>
    /// <remarks>
    /// Only Running qualifies. The UI keeps polling the provider while idle, so a sample can be
    /// genuinely one second old with no session behind it — that freshness is real and worth
    /// reporting, but calling it "Live" beside a runtime state of "Stopped" invites exactly the
    /// wrong reading, that BladeControl is still driving the fans. Stopped, Faulted and
    /// EmergencyHandoff are all states where it is not, and the distinction is what
    /// known-limitations.md promises: only a Running session reports current readings.
    /// </remarks>
    public static bool IsLiveSession(string? state) =>
        string.Equals(state, "Running", StringComparison.Ordinal);

    /// <summary>Whether status contains evidence that a thermal session exists or existed.</summary>
    public static bool HasThermalSessionEvidence(RuntimeStatusDto? status) => status is not null &&
        (status.State is "Starting" or "Running" or "Stopping" ||
            status.SessionId is not null ||
            status.Scheduler is { CompletedCycles: > 0 });

    /// <summary>
    /// Classifies session-derived values without mistaking a retained state for a current one.
    /// </summary>
    /// <remarks>
    /// RuntimeConnection deliberately keeps the last status snapshot when transport is lost.
    /// State alone therefore cannot make an observation current: an offline snapshot whose
    /// retained state says Running is still only the last thing the UI received. Starting and
    /// Stopping are kept distinct because "not current" does not mean "the last session ended".
    /// </remarks>
    public static SessionObservationScope SessionObservation(
        bool isOnline,
        string? state) => string.IsNullOrWhiteSpace(state)
            ? SessionObservationScope.Unavailable
            : !isOnline
            ? SessionObservationScope.LastReported
            : state switch
            {
                "Running" => SessionObservationScope.Current,
                "Starting" => SessionObservationScope.Starting,
                "Stopping" => SessionObservationScope.Stopping,
                "Stopped" or "Faulted" or "EmergencyHandoff" =>
                    SessionObservationScope.LastSession,
                _ => SessionObservationScope.Unavailable
            };

    /// <summary>Whether the Dynamic controls represent a session being acquired or running.</summary>
    /// <remarks>
    /// Starting belongs here so the UI does not offer a second start while ownership acquisition
    /// is in progress. It deliberately does not belong to <see cref="IsLiveSession"/>: a
    /// transition can be engaged without making its retained telemetry current.
    /// </remarks>
    public static bool IsDynamicSessionEngaged(bool isOnline, string? state) =>
        isOnline && (state is "Starting" or "Running");

    /// <summary>
    /// Whether the runtime's effective target still describes a command that is in force.
    /// </summary>
    /// <remarks>
    /// A target can be current while Running (dynamic control) or while authoritative status
    /// identifies a standalone Fixed profile in Stopped. Starting and Stopping are ownership
    /// transitions, while Faulted and
    /// EmergencyHandoff can retain the last numeric target after cooling has become uncertain or
    /// returned to firmware. Showing that retained number under a current-target heading would
    /// turn history into state, so those cases deliberately render no target.
    /// </remarks>
    public static bool HasCurrentFanTarget(
        bool isOnline,
        string? state,
        string? currentProfile) => isOnline &&
        (state is "Running" ||
            state is "Stopped" &&
            string.Equals(currentProfile, "Fan/Fixed", StringComparison.Ordinal));

    /// <summary>
    /// Whether an untimestamped direct fan/profile read may be shown as stable-state context.
    /// </summary>
    /// <remarks>
    /// During Running it can lag the controller's authoritative target, and transitions or a
    /// transport loss can retain it after ownership changes. Stable online Stopped is the only
    /// state where pages without an explicit snapshot label may show it.
    /// </remarks>
    public static bool HasCurrentCoolingSnapshot(bool isOnline, string? state) =>
        isOnline && state is "Stopped";

    public static string RuntimeStateDescription(
        string? state,
        string? currentProfile = null) => state switch
        {
            // Stopped means there is no dynamic thermal session. It does not by itself identify
            // fan ownership: the runtime deliberately supports a standalone Fixed profile while
            // remaining Stopped. CurrentProfile is part of the authoritative status snapshot;
            // the separately fetched fan profile has no timestamp and cannot prove ownership.
            "Stopped" when string.Equals(currentProfile, "Fan/Fixed", StringComparison.Ordinal) =>
                "Idle — Runtime Core holds a Fixed fan target.",
            "Stopped" => "Idle — no dynamic thermal session.",
            "Starting" => "Acquiring hardware ownership.",
            "Running" => "Runtime Core owns cooling.",
            "Stopping" => "Handing cooling back to firmware.",
            // Not "in progress": the runtime only reports this state once the handoff is done and
            // verified. Describing a completed, successful safety action as ongoing reads like the
            // machine is still in trouble when it is already safe.
            "EmergencyHandoff" =>
                "Firmware Auto owns cooling after a thermal emergency. Restart to resume.",
            "Faulted" => "Runtime Core faulted; check Diagnostics.",
            _ => "Unknown runtime state."
        };

    /// <summary>
    /// Thermal session presentation derived from the runtime state, kept separate from the
    /// process-level connection state so "Offline" never reads as "Stopped".
    /// </summary>
    public static string ThermalSession(string? state) => state switch
    {
        "Running" => "Running",
        "Starting" => "Starting",
        "Stopping" => "Stopping",
        "EmergencyHandoff" => "Emergency",
        "Faulted" => "Faulted",
        "Stopped" => "Stopped",
        _ => Unavailable
    };

    public static StatusTone HealthTone(TelemetryHealthDto? health) => health is null
        ? StatusTone.Muted
        : health.IsHealthy
            ? StatusTone.Good
            : StatusTone.Warning;

    public static StatusTone SchedulerTone(string? schedulerHealth) =>
        string.IsNullOrEmpty(schedulerHealth)
            ? StatusTone.Muted
            : schedulerHealth.StartsWith("Healthy", StringComparison.Ordinal)
                ? StatusTone.Good
                : StatusTone.Warning;

    /// <summary>
    /// The Razer 0x0D81 field has not been proven to be a physical tachometer, so it is
    /// always presented as a firmware-reported value and never as measured fan speed.
    /// </summary>
    public static string FirmwareFanValue(int rpm) =>
        rpm <= 0 ? Unavailable : rpm.ToString("N0", CultureInfo.CurrentCulture);

    public static string EventTimestamp(DateTimeOffset value) =>
        value.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture);
}
