using BladeControl.Razer;
using BladeControl.Telemetry;

namespace BladeControl.Thermal;

public interface IThermalDelay
{
    void Wait(TimeSpan delay);
}

public sealed class ThreadThermalDelay : IThermalDelay
{
    public void Wait(TimeSpan delay) => Thread.Sleep(delay);
}

public sealed record ThermalSelfTestStageResult(
    string Stage,
    bool Succeeded,
    string Message);

public sealed record ThermalSelfTestResult(
    bool Succeeded,
    string Message,
    ThermalMachineState? InitialState,
    ThermalMachineState? FinalState,
    IReadOnlyList<ThermalSelfTestStageResult> Stages,
    IReadOnlyList<ThermalTraceEntry> Trace,
    double? CpuMinimumCelsius,
    double? CpuMaximumCelsius,
    double? GpuMinimumCelsius,
    double? GpuMaximumCelsius);

public sealed class ThermalSelfTestRunner
{
    private const int QualificationSamples = 10;
    private static readonly TimeSpan QualificationInterval = TimeSpan.FromMilliseconds(500);

    private readonly ITelemetryProvider _telemetry;
    private readonly IThermalControlDevice _control;
    private readonly IThermalClock _clock;
    private readonly IThermalDelay _delay;

    public ThermalSelfTestRunner(
        ITelemetryProvider telemetry,
        IThermalControlDevice control,
        IThermalClock? clock = null,
        IThermalDelay? delay = null)
    {
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _clock = clock ?? new SystemThermalClock();
        _delay = delay ?? new ThreadThermalDelay();
    }

    public ThermalSelfTestResult Run()
    {
        var stages = new List<ThermalSelfTestStageResult>();
        ThermalMachineState initial;
        try
        {
            initial = _control.CaptureState();
        }
        catch (Exception exception)
        {
            return Failure(stages, null, null, $"Initial state read failed: {exception.Message}");
        }

        if (!IsRequiredInitialState(initial))
        {
            stages.Add(new ThermalSelfTestStageResult(
                "Preflight",
                false,
                "Requires Custom + Auto, CPU Medium, GPU Low. No SET was sent."));
            return Failure(
                stages,
                initial,
                initial,
                "Thermal selftest precondition failed before any SET.");
        }

        var samples = new List<TelemetrySnapshot>(QualificationSamples);
        for (int index = 0; index < QualificationSamples; index++)
        {
            TelemetrySnapshot sample;
            try
            {
                sample = _telemetry.GetSnapshot();
            }
            catch (Exception exception)
            {
                stages.Add(new ThermalSelfTestStageResult(
                    "A - Telemetry qualification",
                    false,
                    $"Sample {index + 1} failed: {exception.Message}. No SET was sent."));
                return Failure(stages, initial, initial, "Telemetry qualification failed.");
            }

            TelemetryHealth health = TelemetryHealthEvaluator.Evaluate(sample, _clock.UtcNow);
            if (!health.IsHealthy)
            {
                stages.Add(new ThermalSelfTestStageResult(
                    "A - Telemetry qualification",
                    false,
                    $"Sample {index + 1} invalid: {health.Reason} No SET was sent."));
                return Failure(stages, initial, initial, "Telemetry qualification failed.");
            }

            samples.Add(sample);
            if (index + 1 < QualificationSamples)
            {
                _delay.Wait(QualificationInterval);
            }
        }

        double cpuMin = samples.Min(sample => sample.CpuPackageTemperatureCelsius.Value!.Value);
        double cpuMax = samples.Max(sample => sample.CpuPackageTemperatureCelsius.Value!.Value);
        double gpuMin = samples.Min(sample => sample.GpuTemperatureCelsius.Value!.Value);
        double gpuMax = samples.Max(sample => sample.GpuTemperatureCelsius.Value!.Value);
        stages.Add(new ThermalSelfTestStageResult(
            "A - Telemetry qualification",
            true,
            $"10/10 valid samples. CPU {cpuMin:F1}-{cpuMax:F1} C; " +
            $"GPU {gpuMin:F1}-{gpuMax:F1} C. Optional: " +
            $"CPU package power {FormatOptionalAverage(samples, sample => sample.CpuPackagePowerWatts, "W")}; " +
            $"GPU power {FormatOptionalAverage(samples, sample => sample.GpuPowerWatts, "W")}; " +
            $"GPU utilization {FormatOptionalAverage(samples, sample => sample.GpuUtilizationPercent, "%")}."));

        double cpuBaseline = samples.Average(sample =>
            sample.CpuPackageTemperatureCelsius.Value!.Value);
        double gpuBaseline = samples.Average(sample =>
            sample.GpuTemperatureCelsius.Value!.Value);
        ThermalProfile selfTestProfile = CreateSelfTestProfile(cpuBaseline, gpuBaseline);
        using var faultInjector = new SelfTestFaultInjectingTelemetryProvider(_telemetry);
        var runtime = new ThermalRuntimeController(
            faultInjector,
            _control,
            selfTestProfile,
            clock: _clock);

        try
        {
            runtime.Start();
            stages.Add(new ThermalSelfTestStageResult(
                "B - Safe Manual baseline",
                true,
                "Balanced + Manual and firmware-reported 3000/3000 baseline validated."));

            for (int cycle = 0; cycle < 4 &&
                 runtime.State == ThermalControllerStateKind.Manual; cycle++)
            {
                _delay.Wait(QualificationInterval);
                ThermalDecision decision = runtime.RunCycle();
                if (decision.EffectiveTarget.Value is < 3200 or > 4000)
                {
                    throw new InvalidOperationException(
                        $"Selftest target {decision.EffectiveTarget.Value} RPM is outside 3200..4000.");
                }
            }

            stages.Add(new ThermalSelfTestStageResult(
                "C - Live controller decision",
                runtime.State == ThermalControllerStateKind.Manual,
                "Real authoritative telemetry drove the production curve/decision/fan path without load generation."));

            faultInjector.InjectMissingCpuTemperature = true;
            for (int cycle = 0; cycle < 2 &&
                 runtime.State == ThermalControllerStateKind.Manual; cycle++)
            {
                _delay.Wait(QualificationInterval);
                _ = runtime.RunCycle();
            }

            bool emergencyStopped = runtime.State == ThermalControllerStateKind.EmergencyStopped;
            stages.Add(new ThermalSelfTestStageResult(
                "D - Fail-safe fault injection",
                emergencyStopped,
                emergencyStopped
                    ? "Production controller detected missing CPU telemetry and handed control to firmware Auto."
                    : "Production controller did not enter its emergency stop state."));
        }
        catch (Exception exception)
        {
            ThermalSessionResult cleanup = runtime.Stop();
            stages.Add(new ThermalSelfTestStageResult(
                "Controller failure recovery",
                cleanup.FinalState?.IsAuto == true,
                exception.Message));
            return BuildResult(
                false,
                cleanup.FinalState?.IsAuto == true
                    ? "SELFTEST FAILED - INITIAL STATE RECOVERY ATTEMPTED"
                    : "FAN AUTO RESTORATION FAILED",
                initial,
                cleanup.FinalState,
                stages,
                cleanup.Trace,
                cpuMin,
                cpuMax,
                gpuMin,
                gpuMax);
        }

        ThermalSessionResult result = runtime.Stop();
        bool restored = result.FinalState is not null && StateEquals(initial, result.FinalState);
        stages.Add(new ThermalSelfTestStageResult(
            "E - Restore initial state",
            restored,
            restored
                ? "Custom + Auto, CPU Medium, GPU Low restored exactly."
                : result.FinalState?.IsAuto == true
                    ? "PERFORMANCE RESTORATION FAILED"
                    : "FAN AUTO RESTORATION FAILED"));
        return BuildResult(
            restored,
            restored ? "PASS - Thermal Control V1 selftest completed." : stages[^1].Message,
            initial,
            result.FinalState,
            stages,
            result.Trace,
            cpuMin,
            cpuMax,
            gpuMin,
            gpuMax);
    }

    internal static ThermalProfile CreateSelfTestProfile(
        double cpuBaseline,
        double gpuBaseline)
    {
        return new ThermalProfile(
            "selftest-live-baseline",
            CreateCenteredCurve(cpuBaseline),
            CreateCenteredCurve(gpuBaseline));
    }

    private static ThermalCurve CreateCenteredCurve(double baseline)
    {
        double center = Math.Clamp(baseline, 6, 114);
        return new ThermalCurve(
        [
            new ThermalCurvePoint(center - 5, new FanRpm(3200)),
            new ThermalCurvePoint(center, new FanRpm(3500)),
            new ThermalCurvePoint(center + 5, new FanRpm(3900))
        ]);
    }

    private static bool IsRequiredInitialState(ThermalMachineState state) =>
        state.ZonesAgree &&
        state.Zone1PerformanceMode == RazerPerformanceMode.Custom &&
        state.Zone1FanMode == RazerFanMode.Auto &&
        state.CpuLevel == RazerCpuPerformanceLevel.Medium &&
        state.GpuLevel == RazerGpuPerformanceLevel.Low;

    private static bool StateEquals(ThermalMachineState expected, ThermalMachineState actual) =>
        expected.Zone1PerformanceMode == actual.Zone1PerformanceMode &&
        expected.Zone2PerformanceMode == actual.Zone2PerformanceMode &&
        expected.Zone1FanMode == actual.Zone1FanMode &&
        expected.Zone2FanMode == actual.Zone2FanMode &&
        expected.CpuLevel == actual.CpuLevel &&
        expected.GpuLevel == actual.GpuLevel;

    private static string FormatOptionalAverage(
        IEnumerable<TelemetrySnapshot> samples,
        Func<TelemetrySnapshot, TelemetryMetric<double>> selector,
        string unit)
    {
        double[] values = samples
            .Select(selector)
            .Where(metric => metric.IsValid && metric.Value.HasValue)
            .Select(metric => metric.Value!.Value)
            .ToArray();
        return values.Length == 0
            ? "unavailable"
            : $"{values.Average():F1} {unit}";
    }

    private static ThermalSelfTestResult Failure(
        IReadOnlyList<ThermalSelfTestStageResult> stages,
        ThermalMachineState? initial,
        ThermalMachineState? final,
        string message) =>
        BuildResult(false, message, initial, final, stages, [], null, null, null, null);

    private static ThermalSelfTestResult BuildResult(
        bool succeeded,
        string message,
        ThermalMachineState? initial,
        ThermalMachineState? final,
        IReadOnlyList<ThermalSelfTestStageResult> stages,
        IReadOnlyList<ThermalTraceEntry> trace,
        double? cpuMin,
        double? cpuMax,
        double? gpuMin,
        double? gpuMax) => new(
            succeeded,
            message,
            initial,
            final,
            stages.ToArray(),
            trace.ToArray(),
            cpuMin,
            cpuMax,
            gpuMin,
            gpuMax);
}

internal sealed class SelfTestFaultInjectingTelemetryProvider : ITelemetryProvider
{
    private readonly ITelemetryProvider _inner;

    internal SelfTestFaultInjectingTelemetryProvider(ITelemetryProvider inner)
    {
        _inner = inner;
    }

    internal bool InjectMissingCpuTemperature { get; set; }

    public string Name => $"{_inner.Name} + selftest-only fault injector";

    public TelemetryCapabilities Capabilities => _inner.Capabilities;

    public TelemetrySnapshot GetSnapshot()
    {
        TelemetrySnapshot snapshot = _inner.GetSnapshot();
        if (!InjectMissingCpuTemperature)
        {
            return snapshot;
        }

        return new TelemetrySnapshot(
            snapshot.Timestamp,
            TelemetryMetric<double>.Missing(
                snapshot.Timestamp,
                TelemetrySources.CpuPackageTemperature,
                "Selftest-only injected missing CPU Package sample."),
            snapshot.GpuTemperatureCelsius)
        {
            CpuCoreMaxTemperatureCelsius = snapshot.CpuCoreMaxTemperatureCelsius,
            CpuPackagePowerWatts = snapshot.CpuPackagePowerWatts,
            CpuTotalLoadPercent = snapshot.CpuTotalLoadPercent,
            CpuClockMegahertz = snapshot.CpuClockMegahertz,
            GpuPowerWatts = snapshot.GpuPowerWatts,
            GpuUtilizationPercent = snapshot.GpuUtilizationPercent,
            GpuMemoryUtilizationPercent = snapshot.GpuMemoryUtilizationPercent,
            GpuGraphicsClockMegahertz = snapshot.GpuGraphicsClockMegahertz,
            GpuMemoryClockMegahertz = snapshot.GpuMemoryClockMegahertz,
            GpuVramUsedBytes = snapshot.GpuVramUsedBytes,
            GpuVramTotalBytes = snapshot.GpuVramTotalBytes,
            AcpiThermalZonesCelsius = snapshot.AcpiThermalZonesCelsius,
            RazerFirmwareState = snapshot.RazerFirmwareState,
            Warnings = snapshot.Warnings
        };
    }

    public void Dispose()
    {
        // The selftest runner does not own the wrapped provider.
    }
}
