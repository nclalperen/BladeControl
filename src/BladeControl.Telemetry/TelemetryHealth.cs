namespace BladeControl.Telemetry;

public enum TelemetryHealthKind
{
    Healthy,
    Missing,
    Invalid,
    Stale,
    Critical
}

/// <summary>
/// Graded CPU thermal condition, distinct from telemetry health: every tier here describes a
/// working sensor reporting a hot CPU, not a sensor problem.
/// </summary>
public enum CpuThermalSeverity
{
    /// <summary>Below the critical cooling threshold; the normal curve governs.</summary>
    Normal,

    /// <summary>Hot enough to demand maximum cooling, not hot enough to abandon control.</summary>
    CriticalCooling,

    /// <summary>Hot enough to hand off if it persists across consecutive samples.</summary>
    SustainedEmergency,

    /// <summary>At Tjunction; hand off from a single authoritative sample.</summary>
    ImmediateEmergency
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

    /// <summary>
    /// CPU temperature at or above which closed-loop control refuses to <i>start</i>.
    /// </summary>
    /// <remarks>
    /// Entry-gate only. Taking ownership of a machine that is already critical is not a
    /// reasonable starting condition, so this stays where it was. It is deliberately no
    /// longer the trigger for abandoning control once running — see
    /// <see cref="ClassifyCpuThermalSeverity"/>.
    /// </remarks>
    public const double CpuEmergencyTemperatureCelsius = 90;

    /// <summary>
    /// GPU temperature at or above which closed-loop control refuses to <i>start</i>.
    /// </summary>
    /// <remarks>
    /// Entry gate only, and deliberately conservative: on the reference part this is the
    /// hardware shutdown temperature, so a machine already there has no business starting a
    /// new thermal session. Once running, the graded ladder built from device-discovered
    /// limits governs instead — see <see cref="ClassifyGpuThermalSeverity"/>.
    /// </remarks>
    public const double GpuEmergencyTemperatureCelsius = 80;

    /// <summary>CPU temperature at which the running loop demands maximum cooling.</summary>
    public const double CpuCriticalCoolingTemperatureCelsius = 90;

    /// <summary>CPU temperature the loop must fall back to before maximum cooling is released.</summary>
    /// <remarks>
    /// Five degrees below entry, deliberately wider than the ordinary
    /// <c>CoolingHysteresisCelsius</c> of 3: releasing maximum fans is a safety decision, and
    /// oscillating across it costs far more than holding the fans high a little longer.
    /// </remarks>
    public const double CpuCriticalCoolingRecoveryTemperatureCelsius = 85;

    /// <summary>CPU temperature that starts qualifying a sustained emergency.</summary>
    public const double CpuSustainedEmergencyTemperatureCelsius = 95;

    /// <summary>CPU temperature that hands off to firmware from a single sample.</summary>
    /// <remarks>
    /// Tjunction for the reference Intel Core i9-13950HX. At this point the CPU is already
    /// throttling itself and there is nothing left for a fan curve to achieve, so waiting for
    /// confirmation would only delay the handoff.
    /// </remarks>
    public const double CpuImmediateEmergencyTemperatureCelsius = 100;

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

    /// <summary>
    /// Health as the running control loop sees it: identical to <see cref="Evaluate"/> except
    /// that a hot-but-valid CPU Package reading is <b>healthy</b>.
    /// </summary>
    /// <remarks>
    /// <para>This distinction is the whole point. Treating heat as a telemetry fault routed a
    /// 90 C sample down the unhealthy path, which skips curve evaluation and fan response
    /// entirely — so the only action left was to abandon control. A single boost spike on an
    /// idle desktop could therefore end a thermal session, which is what happened in the
    /// field.</para>
    /// <para>A 90 C reading is not a broken sensor; it is a working sensor reporting a hot
    /// CPU. The loop answers heat with the graded severity ladder
    /// (<see cref="ClassifyCpuThermalSeverity"/>) — maximum fans first, handoff only when heat
    /// persists or reaches Tjunction. Missing, invalid and stale readings are still faults and
    /// still hand off unchanged, and GPU policy is untouched.</para>
    /// </remarks>
    public static TelemetryHealth EvaluateForControlLoop(
        TelemetrySnapshot snapshot,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        TelemetryHealth cpu = EvaluateCpuTemperatureIntegrity(
            snapshot.CpuPackageTemperatureCelsius,
            now);
        if (!cpu.IsHealthy)
        {
            return cpu;
        }

        TelemetryHealth gpu = EvaluateGpuTemperatureIntegrity(
            snapshot.GpuTemperatureCelsius,
            now);
        if (!gpu.IsHealthy)
        {
            return gpu;
        }

        return new TelemetryHealth(TelemetryHealthKind.Healthy, "Required telemetry is healthy.");
    }

    /// <summary>
    /// Presence, plausibility and freshness of the GPU reading, with no opinion about heat.
    /// </summary>
    /// <remarks>
    /// The fixed 80 C GPU emergency it replaces on this path was, on the reference RTX 4090
    /// Laptop GPU, the temperature at which the hardware shuts itself down. Treating that as
    /// the software handoff meant no cooling response and no margin. The running loop now uses
    /// device-discovered limits; the preflight gate below keeps its conservative fixed bar.
    /// </remarks>
    public static TelemetryHealth EvaluateGpuTemperatureIntegrity(
        TelemetryMetric<double> metric,
        DateTimeOffset now) => EvaluateRequiredTemperature(metric, now, "GPU core");

    /// <summary>
    /// Places a valid GPU reading on the graded ladder defined by the device's own limits.
    /// </summary>
    public static GpuThermalSeverity ClassifyGpuThermalSeverity(
        double celsius,
        GpuThermalLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (celsius >= limits.ImmediateEmergencyCelsius)
        {
            return GpuThermalSeverity.ImmediateEmergency;
        }

        if (celsius >= limits.SustainedEmergencyCelsius)
        {
            return GpuThermalSeverity.SustainedEmergency;
        }

        return celsius >= limits.CriticalCoolingCelsius
            ? GpuThermalSeverity.CriticalCooling
            : GpuThermalSeverity.Normal;
    }

    /// <summary>
    /// Presence, plausibility and freshness of the CPU reading, with no opinion about heat.
    /// </summary>
    public static TelemetryHealth EvaluateCpuTemperatureIntegrity(
        TelemetryMetric<double> metric,
        DateTimeOffset now) => EvaluateRequiredTemperature(metric, now, "CPU Package");

    /// <summary>
    /// Places a valid CPU Package reading on the graded thermal-severity ladder.
    /// </summary>
    /// <remarks>
    /// Classification only — how many samples a tier needs before it acts is control policy
    /// and lives in the thermal decision engine.
    /// </remarks>
    public static CpuThermalSeverity ClassifyCpuThermalSeverity(double celsius)
    {
        if (celsius >= CpuImmediateEmergencyTemperatureCelsius)
        {
            return CpuThermalSeverity.ImmediateEmergency;
        }

        if (celsius >= CpuSustainedEmergencyTemperatureCelsius)
        {
            return CpuThermalSeverity.SustainedEmergency;
        }

        return celsius >= CpuCriticalCoolingTemperatureCelsius
            ? CpuThermalSeverity.CriticalCooling
            : CpuThermalSeverity.Normal;
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
