namespace BladeControl.Telemetry;

public enum TelemetryHealthKind
{
    Healthy,
    Missing,
    Invalid,
    Stale,
    Critical
}

public sealed record TelemetryHealth(
    TelemetryHealthKind Kind,
    string Reason)
{
    public bool IsHealthy => Kind == TelemetryHealthKind.Healthy;

    public bool RequiresImmediateAuto => Kind is
        TelemetryHealthKind.Stale or TelemetryHealthKind.Critical;
}

public static class TelemetryHealthEvaluator
{
    public static readonly TimeSpan MaximumRequiredSampleAge = TimeSpan.FromSeconds(2);

    public const double MinimumPlausibleTemperatureCelsius = 0;
    public const double MaximumPlausibleTemperatureCelsius = 120;
    public const double CpuEmergencyTemperatureCelsius = 90;
    public const double GpuEmergencyTemperatureCelsius = 80;

    public static TelemetryHealth Evaluate(
        TelemetrySnapshot snapshot,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        TelemetryHealth cpu = EvaluateRequiredCpuTemperature(
            snapshot.CpuPackageTemperatureCelsius,
            now);
        if (!cpu.IsHealthy)
        {
            return cpu;
        }

        TelemetryHealth gpu = EvaluateRequiredGpuTemperature(
            snapshot.GpuTemperatureCelsius,
            now);
        if (!gpu.IsHealthy)
        {
            return gpu;
        }

        return new TelemetryHealth(TelemetryHealthKind.Healthy, "Required telemetry is healthy.");
    }

    public static TelemetryHealth EvaluateRequiredCpuTemperature(
        TelemetryMetric<double> metric,
        DateTimeOffset now)
    {
        TelemetryHealth health = EvaluateRequiredTemperature(metric, now, "CPU Package");
        if (!health.IsHealthy)
        {
            return health;
        }

        return metric.Value!.Value >= CpuEmergencyTemperatureCelsius
            ? new TelemetryHealth(
                TelemetryHealthKind.Critical,
                $"CPU Package temperature reached {metric.Value.Value:F1} C.")
            : health;
    }

    public static TelemetryHealth EvaluateRequiredGpuTemperature(
        TelemetryMetric<double> metric,
        DateTimeOffset now)
    {
        TelemetryHealth health = EvaluateRequiredTemperature(metric, now, "GPU core");
        if (!health.IsHealthy)
        {
            return health;
        }

        return metric.Value!.Value >= GpuEmergencyTemperatureCelsius
            ? new TelemetryHealth(
                TelemetryHealthKind.Critical,
                $"GPU core temperature reached {metric.Value.Value:F1} C.")
            : health;
    }

    private static TelemetryHealth EvaluateRequiredTemperature(
        TelemetryMetric<double> metric,
        DateTimeOffset now,
        string name)
    {
        if (!metric.IsSupported || !metric.HasValue)
        {
            return new TelemetryHealth(
                TelemetryHealthKind.Missing,
                $"{name} temperature is unavailable: {metric.Diagnostic ?? "no sample"}.");
        }

        double value = metric.Value!.Value;
        if (!metric.IsValid || !double.IsFinite(value) ||
            value <= MinimumPlausibleTemperatureCelsius ||
            value >= MaximumPlausibleTemperatureCelsius)
        {
            return new TelemetryHealth(
                TelemetryHealthKind.Invalid,
                $"{name} temperature is invalid ({value}).");
        }

        if (metric.Freshness(now, MaximumRequiredSampleAge) != TelemetryFreshness.Fresh)
        {
            return new TelemetryHealth(
                TelemetryHealthKind.Stale,
                $"{name} temperature is stale " +
                $"(age {metric.Age(now)?.TotalMilliseconds:F0} ms).");
        }

        return new TelemetryHealth(TelemetryHealthKind.Healthy, $"{name} temperature is healthy.");
    }
}
