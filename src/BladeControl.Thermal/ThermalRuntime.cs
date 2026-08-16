using BladeControl.Razer;
using BladeControl.Telemetry;

namespace BladeControl.Thermal;

public interface IThermalClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemThermalClock : IThermalClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public enum ThermalTraceKind
{
    Telemetry,
    Decision,
    Protocol,
    State
}

public sealed record ThermalTraceEntry(
    ThermalTraceKind Kind,
    long Sequence,
    DateTimeOffset Timestamp,
    string Message,
    RazerExchangeTrace? Exchange = null);

public sealed record ThermalSessionResult(
    ThermalControllerStateKind State,
    bool Succeeded,
    string Message,
    ThermalMachineState? OriginalState,
    ThermalMachineState? FinalState,
    IReadOnlyList<ThermalDecision> Decisions,
    IReadOnlyList<ThermalTraceEntry> Trace);

public sealed class ThermalPreflightException : Exception
{
    public ThermalPreflightException(string message)
        : base(message)
    {
    }
}

public sealed class ThermalRuntimeController
{
    private readonly ITelemetryProvider _telemetry;
    private readonly IThermalControlDevice _control;
    private readonly IThermalClock _clock;
    private readonly ThermalDecisionEngine _engine;
    private readonly List<ThermalDecision> _decisions = [];
    private readonly List<ThermalTraceEntry> _trace = [];
    private long _telemetrySequence;
    private long _protocolSequence;
    private bool _autoAttempted;
    private bool _restoreAttempted;

    public ThermalRuntimeController(
        ITelemetryProvider telemetry,
        IThermalControlDevice control,
        ThermalProfile profile,
        ThermalPolicy? policy = null,
        IThermalClock? clock = null)
    {
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _clock = clock ?? new SystemThermalClock();
        _engine = new ThermalDecisionEngine(profile, policy);
    }

    public ThermalControllerStateKind State { get; private set; } =
        ThermalControllerStateKind.Created;

    public ThermalMachineState? OriginalState { get; private set; }

    public ThermalMachineState? FinalState { get; private set; }

    public IReadOnlyList<ThermalDecision> Decisions => _decisions;

    public IReadOnlyList<ThermalTraceEntry> Trace => _trace;

    public void Start()
    {
        if (State != ThermalControllerStateKind.Created)
        {
            throw new InvalidOperationException("Thermal runtime can only be started once.");
        }

        TelemetrySnapshot qualification;
        try
        {
            qualification = CollectTelemetry();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new ThermalPreflightException(
                $"Authoritative telemetry qualification failed: {exception.Message}. No SET was sent.");
        }

        TelemetryHealth health = TelemetryHealthEvaluator.Evaluate(qualification, _clock.UtcNow);
        if (!health.IsHealthy)
        {
            throw new ThermalPreflightException(
                $"{health.Reason} Thermal controller cannot safely enter Manual fan mode. " +
                "No SET was sent.");
        }

        OriginalState = _control.CaptureState();
        AddProtocolTrace(OriginalState.Exchanges, "Capture original non-telemetry state");
        if (!OriginalState.IsAuto)
        {
            throw new ThermalPreflightException(
                "Thermal control must start from a consistent Auto fan mode. No SET was sent.");
        }

        try
        {
            _ = RazerThermalControlDevice.CreateRestorationProfile(OriginalState);
        }
        catch (InvalidOperationException exception)
        {
            throw new ThermalPreflightException(
                $"Original performance state cannot be restored safely: {exception.Message} No SET was sent.");
        }

        State = ThermalControllerStateKind.Ready;
        ThermalControlOperationResult entry = _control.EnterManualBaseline(
            new FanRpm(ThermalCurve.MinimumDynamicRpm));
        AddProtocolTrace(entry.Exchanges, "Enter Balanced + Manual and set 3000 RPM baseline");
        if (!entry.Succeeded || entry.FinalState?.IsBalancedManual != true)
        {
            State = entry.AutoActive
                ? ThermalControllerStateKind.EmergencyStopped
                : ThermalControllerStateKind.Stopped;
            _autoAttempted = entry.AutoRecoveryAttempted;
            throw new InvalidOperationException(
                $"Manual baseline entry failed: {entry.Message}");
        }

        FinalState = entry.FinalState;
        _engine.InitializeBaseline(_clock.UtcNow);
        State = ThermalControllerStateKind.Manual;
        _trace.Add(new ThermalTraceEntry(
            ThermalTraceKind.State,
            1,
            _clock.UtcNow,
            "Balanced + Manual active; both fans have a 3000 RPM firmware target."));
    }

    public ThermalDecision RunCycle()
    {
        if (State != ThermalControllerStateKind.Manual)
        {
            throw new InvalidOperationException("Thermal runtime is not in Manual control.");
        }

        TelemetrySnapshot? snapshot = null;
        ThermalDecision decision;
        try
        {
            snapshot = CollectTelemetry();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            decision = _engine.EvaluateProviderFailure(exception.Message, _clock.UtcNow);
            RecordDecision(decision);
            if (decision.EmergencyAuto)
            {
                EmergencyAutoAndRestore(decision.Reason);
            }

            return decision;
        }

        try
        {
            decision = _engine.Evaluate(snapshot, _clock.UtcNow);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            decision = _engine.EvaluateInternalFailure(exception.Message, _clock.UtcNow);
        }

        RecordDecision(decision);
        if (decision.EmergencyAuto)
        {
            EmergencyAutoAndRestore(decision.Reason);
            return decision;
        }

        if (!decision.ShouldWrite)
        {
            return decision;
        }

        ThermalControlOperationResult apply;
        try
        {
            apply = _control.SetBothFans(decision.EffectiveTarget);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            EmergencyAutoAndRestore($"Fan target operation threw: {exception.Message}");
            return decision;
        }

        AddProtocolTrace(apply.Exchanges, $"Set both fans to {decision.EffectiveTarget.Value} RPM");
        if (!apply.Succeeded)
        {
            _autoAttempted |= apply.AutoRecoveryAttempted;
            if (apply.AutoActive)
            {
                State = ThermalControllerStateKind.EmergencyStopped;
                FinalState = apply.FinalState;
                RestoreOriginalPerformanceOnce();
            }
            else
            {
                EmergencyAutoAndRestore($"Fan target operation failed: {apply.Message}");
            }

            return decision;
        }

        FinalState = apply.FinalState;
        _engine.RecordSuccessfulWrite(decision);
        return decision;
    }

    public ThermalSessionResult Stop()
    {
        if (State == ThermalControllerStateKind.Manual)
        {
            ReturnToAutoOnce("Normal stop: firmware handoff before performance restoration.");
            if (FinalState?.IsBalancedAuto == true)
            {
                RestoreOriginalPerformanceOnce();
            }
        }

        bool restored = OriginalState is not null && FinalState is not null &&
            NonTelemetryStateEquals(OriginalState, FinalState);
        if (State != ThermalControllerStateKind.EmergencyStopped || restored)
        {
            State = ThermalControllerStateKind.Stopped;
        }

        return new ThermalSessionResult(
            State,
            restored,
            restored
                ? "Thermal session stopped; Balanced + Auto was established before the original performance state was restored."
                : FinalState?.IsAuto == true
                    ? "Thermal session stopped in Auto, but original performance restoration was incomplete."
                    : "FAN AUTO RESTORATION FAILED",
            OriginalState,
            FinalState,
            _decisions.ToArray(),
            _trace.ToArray());
    }

    private TelemetrySnapshot CollectTelemetry()
    {
        TelemetrySnapshot snapshot = _telemetry.GetSnapshot();
        _trace.Add(new ThermalTraceEntry(
            ThermalTraceKind.Telemetry,
            ++_telemetrySequence,
            snapshot.Timestamp,
            FormatSample(snapshot)));
        return snapshot;
    }

    private void RecordDecision(ThermalDecision decision)
    {
        _decisions.Add(decision);
        _trace.Add(new ThermalTraceEntry(
            ThermalTraceKind.Decision,
            decision.Sequence,
            decision.Timestamp,
            decision.Reason));
    }

    private void EmergencyAutoAndRestore(string reason)
    {
        _engine.MarkEmergencyStopped();
        ReturnToAutoOnce($"Emergency firmware handoff: {reason}");
        State = ThermalControllerStateKind.EmergencyStopped;
        if (FinalState?.IsBalancedAuto == true)
        {
            RestoreOriginalPerformanceOnce();
        }
    }

    private void ReturnToAutoOnce(string reason)
    {
        if (_autoAttempted)
        {
            return;
        }

        _autoAttempted = true;
        ThermalControlOperationResult auto = _control.ReturnToBalancedAuto();
        AddProtocolTrace(auto.Exchanges, reason);
        FinalState = auto.FinalState;
        if (!auto.Succeeded || !auto.AutoActive)
        {
            State = ThermalControllerStateKind.EmergencyStopped;
            _trace.Add(new ThermalTraceEntry(
                ThermalTraceKind.State,
                0,
                _clock.UtcNow,
                $"FAN AUTO RESTORATION FAILED: {auto.Message}. No more writes are permitted."));
        }
    }

    private void RestoreOriginalPerformanceOnce()
    {
        if (_restoreAttempted || OriginalState is null || FinalState?.IsAuto != true)
        {
            return;
        }

        _restoreAttempted = true;
        ThermalControlOperationResult restore = _control.RestorePerformance(OriginalState);
        AddProtocolTrace(restore.Exchanges, "Restore captured performance state after Auto verification");
        FinalState = restore.FinalState;
        if (!restore.Succeeded)
        {
            _trace.Add(new ThermalTraceEntry(
                ThermalTraceKind.State,
                0,
                _clock.UtcNow,
                $"PERFORMANCE RESTORATION FAILED: {restore.Message}"));
        }
    }

    private void AddProtocolTrace(
        IEnumerable<RazerExchangeTrace> exchanges,
        string operation)
    {
        foreach (RazerExchangeTrace exchange in exchanges)
        {
            _trace.Add(new ThermalTraceEntry(
                ThermalTraceKind.Protocol,
                ++_protocolSequence,
                _clock.UtcNow,
                operation,
                exchange));
        }
    }

    private static bool NonTelemetryStateEquals(
        ThermalMachineState expected,
        ThermalMachineState actual) =>
        expected.Zone1PerformanceMode == actual.Zone1PerformanceMode &&
        expected.Zone2PerformanceMode == actual.Zone2PerformanceMode &&
        expected.Zone1FanMode == actual.Zone1FanMode &&
        expected.Zone2FanMode == actual.Zone2FanMode &&
        expected.CpuLevel == actual.CpuLevel &&
        expected.GpuLevel == actual.GpuLevel;

    private static string FormatSample(TelemetrySnapshot snapshot) =>
        $"CPU {FormatTemperature(snapshot.CpuPackageTemperatureCelsius)}; " +
        $"GPU {FormatTemperature(snapshot.GpuTemperatureCelsius)}";

    private static string FormatTemperature(TelemetryMetric<double> metric) =>
        metric.IsValid && metric.Value.HasValue
            ? $"{metric.Value.Value:F1} C"
            : $"unavailable ({metric.Diagnostic})";

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}
