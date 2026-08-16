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
    private readonly ThermalPolicy _policy;
    private FanRpm _writtenTarget = new(ThermalCurve.MinimumDynamicRpm);
    private DateTimeOffset _lastWrite;
    private DateTimeOffset _lastDecision;
    private ThermalDemandSource _triggerSource = ThermalDemandSource.Equal;
    private double _triggerTemperature;
    private int _lowerSamples;
    private int _invalidSamples;
    private bool _initialized;
    private bool _emergencyStopped;
    private long _sequence;

    public ThermalDecisionEngine(ThermalProfile profile, ThermalPolicy? policy = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _policy = policy ?? new ThermalPolicy();
    }

    public FanRpm CurrentTarget => _writtenTarget;

    public bool IsEmergencyStopped => _emergencyStopped;

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
        TelemetryHealth health = TelemetryHealthEvaluator.Evaluate(snapshot, now);
        if (!health.IsHealthy)
        {
            return EvaluateUnhealthy(health, now);
        }

        _invalidSamples = 0;
        double cpuTemperature = snapshot.CpuPackageTemperatureCelsius.Value!.Value;
        double gpuTemperature = snapshot.GpuTemperatureCelsius.Value!.Value;
        FanRpm cpuTarget = _profile.CpuCurve.Evaluate(cpuTemperature);
        FanRpm gpuTarget = _profile.GpuCurve.Evaluate(gpuTemperature);
        ThermalDemandSource source = cpuTarget.Value == gpuTarget.Value
            ? ThermalDemandSource.Equal
            : cpuTarget.Value > gpuTarget.Value
                ? ThermalDemandSource.Cpu
                : ThermalDemandSource.Gpu;
        FanRpm requested = cpuTarget.Value >= gpuTarget.Value ? cpuTarget : gpuTarget;
        (FanRpm effective, string reason) = Stabilize(
            requested,
            source,
            cpuTemperature,
            gpuTemperature,
            now);
        bool canWrite = effective != _writtenTarget &&
            now - _lastWrite >= _policy.MinimumWriteInterval;
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
