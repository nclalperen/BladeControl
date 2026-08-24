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

/// <summary>Presentation helpers shared by the pages. Formatting only, no runtime logic.</summary>
public static class Display
{
    public const string Unavailable = "—";

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

    /// <summary>Tone for a Runtime Core state name.</summary>
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

    public static string RuntimeStateDescription(string? state) => state switch
    {
        "Stopped" => "Idle — firmware owns cooling.",
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
