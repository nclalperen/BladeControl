using BladeControl.Razer;
using BladeControl.Telemetry;

namespace BladeControl.Thermal;

public enum ThermalDemandSource
{
    Cpu,
    Gpu,
    Equal
}

public sealed record ThermalPolicy
{
    public TimeSpan SamplingInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public double CoolingHysteresisCelsius { get; init; } = 3;

    public int LowerTargetQualificationSamples { get; init; } = 3;

    public int DownwardRampRpmPerSecond { get; init; } = 300;

    public TimeSpan MinimumWriteInterval { get; init; } = TimeSpan.FromSeconds(1);

    public int ConsecutiveInvalidSamplesBeforeEmergency { get; init; } = 2;

    /// <summary>
    /// Consecutive authoritative samples at or above the sustained-emergency temperature
    /// before control is handed back to firmware.
    /// </summary>
    /// <remarks>
    /// Three samples at the 500 ms cadence is roughly 1–1.5 s continuously near Tjunction —
    /// long enough to distinguish real sustained heat from the single-sample boost spikes a
    /// desktop workload produces, short enough that genuine runaway is still caught quickly.
    /// </remarks>
    public int SustainedEmergencySamples { get; init; } = 3;

    /// <summary>
    /// Consecutive samples at or below the recovery temperature before maximum cooling is
    /// released back to the normal curve.
    /// </summary>
    public int CriticalCoolingRecoverySamples { get; init; } = 3;

    /// <summary>
    /// Device-discovered GPU thermal limits. Null means the GPU could not be qualified, which
    /// is refused at start rather than papered over with an assumed threshold.
    /// </summary>
    public GpuThermalLimits? GpuLimits { get; init; }
}

public sealed record ThermalDecision(
    long Sequence,
    DateTimeOffset Timestamp,
    TelemetryHealth Health,
    FanRpm? CpuCurveTarget,
    FanRpm? GpuCurveTarget,
    FanRpm? RequestedTarget,
    FanRpm EffectiveTarget,
    ThermalDemandSource? DemandSource,
    bool ShouldWrite,
    bool EmergencyAuto,
    string Reason);

public enum ThermalControllerStateKind
{
    Created,
    Ready,
    Manual,
    EmergencyStopped,
    Stopped
}

public sealed class ThermalDecisionEngine
{
    private readonly ThermalProfile _profile;
    // Not readonly: the GPU thermal limits are derived from the performance mode's thermal
    // anchor, and the mode can change under a running session. Rebuilding the engine to adopt
    // new limits would discard the ladder's sustained-sample counters and hysteresis state,
    // which is exactly the history that stops it reacting to a single reading.
    private ThermalPolicy _policy;
    private FanRpm _writtenTarget = new(ThermalCurve.MinimumDynamicRpm);
    private DateTimeOffset _lastWrite;
    private DateTimeOffset _lastDecision;
    private ThermalDemandSource _triggerSource = ThermalDemandSource.Equal;
    private double _triggerTemperature;
    private int _lowerSamples;
    private int _invalidSamples;
    private int _sustainedEmergencySamples;
    private int _criticalRecoverySamples;
    private bool _cpuCriticalCoolingActive;
    private int _gpuSustainedEmergencySamples;
    private int _gpuCriticalRecoverySamples;
    private bool _gpuCriticalCoolingActive;
    private bool _initialized;
    private bool _emergencyStopped;
    private long _sequence;

    /// <summary>
    /// Adopts GPU thermal limits derived for a new performance mode, keeping ladder state.
    /// </summary>
    /// <remarks>
    /// A performance-mode change moves the driver's thermal target, so limits qualified for the
    /// previous mode stop describing the machine. The session does not have to end for that: it
    /// has to start using the right numbers. Counters and hysteresis are deliberately preserved,
    /// because a GPU that has been sitting at its slowdown limit is still doing so a moment
    /// later under a different ceiling.
    /// </remarks>
    public void AdoptGpuThermalLimits(GpuThermalLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _policy = _policy with { GpuLimits = limits };
    }

    public ThermalDecisionEngine(ThermalProfile profile, ThermalPolicy? policy = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _policy = policy ?? new ThermalPolicy();
    }

    public FanRpm CurrentTarget => _writtenTarget;

    public bool IsEmergencyStopped => _emergencyStopped;

    /// <summary>
    /// True while the critical cooling override holds the fans at the validated maximum,
    /// overriding the curve.
    /// </summary>
    /// <remarks>
    /// Composed: either sensor alone can demand maximum cooling, and it is released only when
    /// <b>both</b> have individually recovered. One sensor cooling down must never withdraw
    /// cooling the other still needs.
    /// </remarks>
    public bool IsCriticalCoolingActive =>
        _cpuCriticalCoolingActive || _gpuCriticalCoolingActive;

    /// <summary>True while the CPU ladder alone demands maximum cooling.</summary>
    public bool IsCpuCriticalCoolingActive => _cpuCriticalCoolingActive;

    /// <summary>True while the GPU ladder alone demands maximum cooling.</summary>
    public bool IsGpuCriticalCoolingActive => _gpuCriticalCoolingActive;

    public void InitializeBaseline(DateTimeOffset timestamp)
    {
        if (_initialized)
        {
            throw new InvalidOperationException("The thermal decision engine is already initialized.");
        }

        _writtenTarget = new FanRpm(ThermalCurve.MinimumDynamicRpm);
        _lastWrite = timestamp;
        _lastDecision = timestamp;
        _initialized = true;
    }

    public ThermalDecision Evaluate(TelemetrySnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureOperational();
        // Control-loop health deliberately treats a hot-but-valid CPU reading as healthy, so
        // that heat reaches the severity ladder below instead of skipping straight to handoff.
        // Missing, invalid, stale and GPU-critical telemetry are unchanged.
        TelemetryHealth health = TelemetryHealthEvaluator.EvaluateForControlLoop(snapshot, now);
        if (!health.IsHealthy)
        {
            return EvaluateUnhealthy(health, now);
        }

        _invalidSamples = 0;
        double cpuTemperature = snapshot.CpuPackageTemperatureCelsius.Value!.Value;
        double gpuTemperature = snapshot.GpuTemperatureCelsius.Value!.Value;

        // Either sensor can demand cooling or end the session. Both ladders are advanced every
        // sample so neither can be starved by the other reaching its threshold first.
        ThermalDecision? emergency = EvaluateCpuThermalSeverity(cpuTemperature, now);
        ThermalDecision? gpuEmergency = EvaluateGpuThermalSeverity(gpuTemperature, now);
        if (emergency is not null)
        {
            return emergency;
        }

        if (gpuEmergency is not null)
        {
            return gpuEmergency;
        }

        FanRpm cpuTarget = _profile.CpuCurve.Evaluate(cpuTemperature);
        FanRpm gpuTarget = _profile.GpuCurve.Evaluate(gpuTemperature);
        ThermalDemandSource source = cpuTarget.Value == gpuTarget.Value
            ? ThermalDemandSource.Equal
            : cpuTarget.Value > gpuTarget.Value
                ? ThermalDemandSource.Cpu
                : ThermalDemandSource.Gpu;
        FanRpm requested = cpuTarget.Value >= gpuTarget.Value ? cpuTarget : gpuTarget;
        FanRpm effective;
        string reason;
        bool canWrite;
        if (IsCriticalCoolingActive)
        {
            // Safety override. The curve, the 3 C hysteresis, the three-sample downward
            // qualification and the 300 RPM/s slew limit all exist to keep ordinary fan
            // behaviour calm; none of them may delay an upward response to a critical CPU.
            // The one-second write-coalescing interval is bypassed for the same reason: this
            // is the next safe opportunity, and waiting for it would be waiting while hot.
            _lastCpuTemperature = cpuTemperature;
            _lastGpuTemperature = gpuTemperature;
            _lowerSamples = 0;
            effective = new FanRpm(FanRpm.MaximumValue);
            requested = effective;
            reason = DescribeCriticalOverride(cpuTemperature, gpuTemperature);
            canWrite = effective != _writtenTarget;
        }
        else
        {
            (effective, reason) = Stabilize(
                requested,
                source,
                cpuTemperature,
                gpuTemperature,
                now);
            canWrite = effective != _writtenTarget &&
                now - _lastWrite >= _policy.MinimumWriteInterval;
        }
        _lastDecision = now;
        return new ThermalDecision(
            ++_sequence,
            now,
            health,
            cpuTarget,
            gpuTarget,
            requested,
            effective,
            source,
            canWrite,
            false,
            canWrite ? reason : effective == _writtenTarget
                ? reason
                : $"{reason} Waiting for the one-second write interval.");
    }

    public ThermalDecision EvaluateProviderFailure(string reason, DateTimeOffset now)
    {
        EnsureOperational();
        return EvaluateUnhealthy(
            new TelemetryHealth(TelemetryHealthKind.Missing, $"Telemetry provider failure: {reason}"),
            now);
    }

    public ThermalDecision EvaluateInternalFailure(string reason, DateTimeOffset now)
    {
        EnsureOperational();
        _emergencyStopped = true;
        _lastDecision = now;
        return new ThermalDecision(
            ++_sequence,
            now,
            new TelemetryHealth(
                TelemetryHealthKind.Invalid,
                $"Internal thermal-controller invariant failed: {reason}"),
            null,
            null,
            null,
            _writtenTarget,
            null,
            false,
            true,
            $"Emergency firmware handoff required: internal invariant failure: {reason}");
    }

    public void RecordSuccessfulWrite(ThermalDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (!decision.ShouldWrite || decision.RequestedTarget is null)
        {
            throw new ArgumentException("The decision does not contain a fan write.", nameof(decision));
        }

        _writtenTarget = decision.EffectiveTarget;
        _lastWrite = decision.Timestamp;
        _lowerSamples = 0;
        _triggerSource = decision.DemandSource ?? ThermalDemandSource.Equal;
        _triggerTemperature = _triggerSource switch
        {
            ThermalDemandSource.Cpu => decision.CpuCurveTarget is null
                ? 0
                : _lastCpuTemperature,
            ThermalDemandSource.Gpu => decision.GpuCurveTarget is null
                ? 0
                : _lastGpuTemperature,
            _ => Math.Max(_lastCpuTemperature, _lastGpuTemperature)
        };
    }

    public void MarkEmergencyStopped() => _emergencyStopped = true;

    private double _lastCpuTemperature;
    private double _lastGpuTemperature;

    private (FanRpm Target, string Reason) Stabilize(
        FanRpm requested,
        ThermalDemandSource source,
        double cpuTemperature,
        double gpuTemperature,
        DateTimeOffset now)
    {
        _lastCpuTemperature = cpuTemperature;
        _lastGpuTemperature = gpuTemperature;
        if (requested.Value > _writtenTarget.Value)
        {
            _lowerSamples = 0;
            return (requested, "Temperature demand increased; upward response is immediate.");
        }

        if (requested == _writtenTarget)
        {
            _lowerSamples = 0;
            return (_writtenTarget, "Requested target is unchanged; fan write coalesced.");
        }

        double currentTriggerTemperature = _triggerSource switch
        {
            ThermalDemandSource.Cpu => cpuTemperature,
            ThermalDemandSource.Gpu => gpuTemperature,
            _ => Math.Max(cpuTemperature, gpuTemperature)
        };
        if (currentTriggerTemperature >
            _triggerTemperature - _policy.CoolingHysteresisCelsius)
        {
            _lowerSamples = 0;
            return (_writtenTarget, "Lower request held by 3 C cooling hysteresis.");
        }

        _lowerSamples++;
        if (_lowerSamples < _policy.LowerTargetQualificationSamples)
        {
            return (_writtenTarget,
                $"Lower request qualification sample {_lowerSamples}/" +
                $"{_policy.LowerTargetQualificationSamples}.");
        }

        double elapsedSeconds = Math.Max(0, (now - _lastDecision).TotalSeconds);
        int rampAllowance = (int)Math.Floor(
            (_policy.DownwardRampRpmPerSecond * elapsedSeconds) / FanRpm.Increment) *
            FanRpm.Increment;
        if (rampAllowance < FanRpm.Increment)
        {
            return (_writtenTarget, "Lower request held by the downward slew-rate limit.");
        }

        int next = Math.Max(requested.Value, _writtenTarget.Value - rampAllowance);
        return (new FanRpm(next),
            $"Lower request qualified; limited to {_policy.DownwardRampRpmPerSecond} RPM/s.");
    }

    /// <summary>
    /// Advances the graded CPU thermal-safety ladder for one authoritative sample.
    /// </summary>
    /// <returns>
    /// An emergency handoff decision when the ladder demands one, otherwise null and the
    /// caller continues with normal (or critical-override) fan control.
    /// </returns>
    private ThermalDecision? EvaluateCpuThermalSeverity(double cpuTemperature, DateTimeOffset now)
    {
        CpuThermalSeverity severity =
            TelemetryHealthEvaluator.ClassifyCpuThermalSeverity(cpuTemperature);

        // Tjunction: the CPU is already throttling itself and no fan target can help, so a
        // single authoritative sample is enough.
        if (severity == CpuThermalSeverity.ImmediateEmergency)
        {
            return EmergencyDecision(
                $"CPU Package temperature reached {cpuTemperature:F1} C, at or above the " +
                $"{TelemetryHealthEvaluator.CpuImmediateEmergencyTemperatureCelsius:F0} C " +
                "immediate limit.",
                now);
        }

        if (severity == CpuThermalSeverity.SustainedEmergency)
        {
            _sustainedEmergencySamples++;
            if (_sustainedEmergencySamples >= _policy.SustainedEmergencySamples)
            {
                return EmergencyDecision(
                    $"CPU Package temperature held at or above " +
                    $"{TelemetryHealthEvaluator.CpuSustainedEmergencyTemperatureCelsius:F0} C " +
                    $"for {_sustainedEmergencySamples} consecutive samples " +
                    $"(latest {cpuTemperature:F1} C).",
                    now);
            }
        }
        else
        {
            // Any sample below the sustained threshold means the heat did not persist, so
            // qualification restarts from zero rather than accumulating across a cool spell.
            _sustainedEmergencySamples = 0;
        }

        UpdateCriticalCoolingOverride(cpuTemperature);
        return null;
    }

    /// <summary>
    /// Enters and leaves the maximum-cooling override, with recovery hysteresis wide enough
    /// that temperatures oscillating around the entry threshold cannot chatter the fans.
    /// </summary>
    private void UpdateCriticalCoolingOverride(double cpuTemperature)
    {
        if (cpuTemperature >= TelemetryHealthEvaluator.CpuCriticalCoolingTemperatureCelsius)
        {
            _cpuCriticalCoolingActive = true;
            _criticalRecoverySamples = 0;
            return;
        }

        if (!_cpuCriticalCoolingActive)
        {
            return;
        }

        if (cpuTemperature > TelemetryHealthEvaluator.CpuCriticalCoolingRecoveryTemperatureCelsius)
        {
            // Between recovery and entry: hold maximum cooling. This is the band that would
            // otherwise produce 89/90 C chatter.
            _criticalRecoverySamples = 0;
            return;
        }

        if (++_criticalRecoverySamples >= _policy.CriticalCoolingRecoverySamples)
        {
            _cpuCriticalCoolingActive = false;
            _criticalRecoverySamples = 0;
        }
    }

    /// <summary>
    /// Advances the graded GPU thermal-safety ladder using the device's own discovered limits.
    /// </summary>
    /// <remarks>
    /// Without limits there is no ladder and no safe assumption to fall back on, so the start
    /// path refuses to qualify the device rather than inventing a threshold here.
    /// </remarks>
    private ThermalDecision? EvaluateGpuThermalSeverity(double gpuTemperature, DateTimeOffset now)
    {
        if (_policy.GpuLimits is not { } limits)
        {
            return null;
        }

        GpuThermalSeverity severity =
            TelemetryHealthEvaluator.ClassifyGpuThermalSeverity(gpuTemperature, limits);

        // Within the policy margin of the temperature at which the GPU shuts itself down.
        // Waiting any longer would mean racing the hardware's own protection.
        if (severity == GpuThermalSeverity.ImmediateEmergency)
        {
            return EmergencyDecision(
                $"GPU core temperature reached {gpuTemperature:F1} C, within " +
                $"{GpuThermalLimits.PreShutdownPolicyMarginCelsius:F0} C of the " +
                $"{limits.HardwareShutdownCelsius:F0} C hardware shutdown limit.",
                now);
        }

        if (severity == GpuThermalSeverity.SustainedEmergency)
        {
            _gpuSustainedEmergencySamples++;
            if (_gpuSustainedEmergencySamples >= _policy.SustainedEmergencySamples)
            {
                return EmergencyDecision(
                    $"GPU core temperature held at or above the " +
                    $"{limits.HardwareSlowdownCelsius:F0} C hardware slowdown limit for " +
                    $"{_gpuSustainedEmergencySamples} consecutive samples " +
                    $"(latest {gpuTemperature:F1} C).",
                    now);
            }
        }
        else
        {
            _gpuSustainedEmergencySamples = 0;
        }

        UpdateGpuCriticalCoolingOverride(gpuTemperature, limits);
        return null;
    }

    private void UpdateGpuCriticalCoolingOverride(double gpuTemperature, GpuThermalLimits limits)
    {
        if (gpuTemperature >= limits.CriticalCoolingCelsius)
        {
            _gpuCriticalCoolingActive = true;
            _gpuCriticalRecoverySamples = 0;
            return;
        }

        if (!_gpuCriticalCoolingActive)
        {
            return;
        }

        if (gpuTemperature > limits.CriticalRecoveryCelsius)
        {
            // Between recovery and entry: hold. This is the band that would otherwise chatter.
            _gpuCriticalRecoverySamples = 0;
            return;
        }

        if (++_gpuCriticalRecoverySamples >= _policy.CriticalCoolingRecoverySamples)
        {
            _gpuCriticalCoolingActive = false;
            _gpuCriticalRecoverySamples = 0;
        }
    }

    /// <summary>Names whichever sensor or sensors are holding maximum cooling.</summary>
    private string DescribeCriticalOverride(double cpuTemperature, double gpuTemperature)
    {
        string who = (_cpuCriticalCoolingActive, _gpuCriticalCoolingActive) switch
        {
            (true, true) =>
                $"CPU Package {cpuTemperature:F1} C and GPU core {gpuTemperature:F1} C",
            (true, false) => $"CPU Package {cpuTemperature:F1} C",
            _ => $"GPU core {gpuTemperature:F1} C"
        };

        return $"Critical cooling override: {who} at or above the critical threshold; " +
            $"holding {FanRpm.MaximumValue} RPM until every critical sensor recovers.";
    }

    private ThermalDecision EmergencyDecision(string reason, DateTimeOffset now)
    {
        _emergencyStopped = true;
        _lastDecision = now;
        return new ThermalDecision(
            ++_sequence,
            now,
            new TelemetryHealth(TelemetryHealthKind.Critical, reason),
            null,
            null,
            null,
            _writtenTarget,
            null,
            false,
            true,
            $"Emergency firmware handoff required: {reason}");
    }

    private ThermalDecision EvaluateUnhealthy(TelemetryHealth health, DateTimeOffset now)
    {
        bool immediate = health.RequiresImmediateAuto;
        _invalidSamples++;
        bool emergency = immediate ||
            _invalidSamples >= _policy.ConsecutiveInvalidSamplesBeforeEmergency;
        if (emergency)
        {
            _emergencyStopped = true;
        }

        _lastDecision = now;
        return new ThermalDecision(
            ++_sequence,
            now,
            health,
            null,
            null,
            null,
            _writtenTarget,
            null,
            false,
            emergency,
            emergency
                ? $"Emergency firmware handoff required: {health.Reason}"
                : $"Required telemetry failure {_invalidSamples}/" +
                  $"{_policy.ConsecutiveInvalidSamplesBeforeEmergency}: {health.Reason}");
    }

    private void EnsureOperational()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("InitializeBaseline must be called first.");
        }

        if (_emergencyStopped)
        {
            throw new InvalidOperationException(
                "The decision engine stopped after emergency Auto and cannot re-enter Manual.");
        }
    }
}
