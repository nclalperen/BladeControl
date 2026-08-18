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
    private const int DefaultHistoryCapacity = 4096;

    private readonly ITelemetryProvider _telemetry;
    private readonly IThermalControlDevice _control;
    private readonly IThermalClock _clock;
    private readonly ThermalDecisionEngine _engine;
    private readonly Queue<ThermalDecision> _decisions = [];
    private readonly Queue<ThermalTraceEntry> _trace = [];
    private readonly int _historyCapacity;
    private long _telemetrySequence;
    private long _protocolSequence;
    private bool _autoAttempted;
    private bool _restoreAttempted;

    public ThermalRuntimeController(
        ITelemetryProvider telemetry,
        IThermalControlDevice control,
        ThermalProfile profile,
        ThermalPolicy? policy = null,
        IThermalClock? clock = null,
        int historyCapacity = DefaultHistoryCapacity)
    {
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _clock = clock ?? new SystemThermalClock();
        _engine = new ThermalDecisionEngine(profile, policy);
        if (historyCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(historyCapacity));
        }

        _historyCapacity = historyCapacity;
    }

    public ThermalControllerStateKind State { get; private set; } =
        ThermalControllerStateKind.Created;

    public ThermalMachineState? OriginalState { get; private set; }

    public ThermalMachineState? FinalState { get; private set; }

    public IReadOnlyList<ThermalDecision> Decisions => _decisions.ToArray();

    public IReadOnlyList<ThermalTraceEntry> Trace => _trace.ToArray();

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

        // Step 2: capture what will be restored when the session ends. Performance data only —
        // its fan-mode fields carry no authority over whether the session may start.
        OriginalState = _control.CaptureState();
        AddProtocolTrace(OriginalState.Exchanges, "Capture original non-telemetry state");

        // Step 3: validate that capture as restoration data, before spending the ownership
        // read. A typed result rather than a caught exception: an unrestorable capture is an
        // expected prerequisite failure, and classifying it by exception type would risk
        // treating it as a poisoned runtime.
        if (!RazerThermalControlDevice.TryCreateRestorationProfile(
                OriginalState,
                out _,
                out string? restorationRejection))
        {
            throw new ThermalPreflightException(
                $"Original performance state cannot be restored safely: " +
                $"{restorationRejection}. No SET was sent.");
        }

        // Step 4: the single authoritative fan-ownership gate, and the last meaningful
        // operation before the first SET. A fresh two-GET firmware read (0x0D82 zone 1 and
        // zone 2).
        //
        // Authorising from a watchdog observation, a diagnostics snapshot, or the capture
        // above is how a machine sitting in firmware Auto gets told it is not in Auto. Nothing
        // may sit between this read and the SET below — not another validation, not a profile
        // construction — so the window between deciding and acting stays as small as the
        // protocol allows.
        ThermalFanModeObservation observed;
        try
        {
            observed = _control.ReadFanModeObservation();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new ThermalPreflightException(
                "Fan mode could not be read from firmware immediately before taking " +
                $"ownership: {exception.Message} No SET was sent.");
        }

        AddProtocolTrace(observed.Exchanges, "Fresh fan-mode qualification before ownership");

        // Step 5: the ownership decision.
        if (!observed.ZonesAgree)
        {
            throw new ThermalPreflightException(
                "Thermal control requires both zones to report the same mode; firmware " +
                $"reported {observed.Describe()}. No SET was sent.");
        }

        if (!observed.IsAuto)
        {
            throw new ThermalPreflightException(
                "Thermal control requires both zones in Auto; firmware reported " +
                $"{observed.Describe()}. No SET was sent.");
        }

        // Step 6.
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
        AddTrace(new ThermalTraceEntry(
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

    public void EmergencyHandoff(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (State != ThermalControllerStateKind.Manual)
        {
            return;
        }

        EmergencyAutoAndRestore(reason);
    }

    public void AbandonAfterOwnershipLoss(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (State != ThermalControllerStateKind.Manual)
        {
            return;
        }

        _engine.MarkEmergencyStopped();
        State = ThermalControllerStateKind.EmergencyStopped;
        AddTrace(new ThermalTraceEntry(
            ThermalTraceKind.State,
            0,
            _clock.UtcNow,
            $"Manual ownership lost: {reason}. No further firmware write was attempted."));
    }

    private TelemetrySnapshot CollectTelemetry()
    {
        TelemetrySnapshot snapshot = _telemetry.GetSnapshot();
        AddTrace(new ThermalTraceEntry(
            ThermalTraceKind.Telemetry,
            ++_telemetrySequence,
            snapshot.Timestamp,
            FormatSample(snapshot)));
        return snapshot;
    }

    private void RecordDecision(ThermalDecision decision)
    {
        AddDecision(decision);
        AddTrace(new ThermalTraceEntry(
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
            AddTrace(new ThermalTraceEntry(
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
            AddTrace(new ThermalTraceEntry(
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
            AddTrace(new ThermalTraceEntry(
                ThermalTraceKind.Protocol,
                ++_protocolSequence,
                _clock.UtcNow,
                operation,
                exchange));
        }
    }

    private void AddDecision(ThermalDecision decision)
    {
        if (_decisions.Count == _historyCapacity)
        {
            _decisions.Dequeue();
        }

        _decisions.Enqueue(decision);
    }

    private void AddTrace(ThermalTraceEntry entry)
    {
        if (_trace.Count == _historyCapacity)
        {
            _trace.Dequeue();
        }

        _trace.Enqueue(entry);
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
