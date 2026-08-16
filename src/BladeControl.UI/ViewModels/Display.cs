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
    public static StatusTone RuntimeStateTone(string? state) => state switch
    {
        "Running" => StatusTone.Good,
        "Starting" or "Stopping" => StatusTone.Warning,
        "EmergencyHandoff" => StatusTone.Danger,
        "Faulted" => StatusTone.Danger,
        "Stopped" => StatusTone.Neutral,
        _ => StatusTone.Muted
    };

    public static string RuntimeStateDescription(string? state) => state switch
    {
        "Stopped" => "Idle — firmware owns cooling.",
        "Starting" => "Acquiring hardware ownership.",
        "Running" => "Runtime Core owns cooling.",
        "Stopping" => "Handing cooling back to firmware.",
        "EmergencyHandoff" => "Emergency handoff to firmware Auto in progress.",
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
