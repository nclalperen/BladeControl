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
    private readonly List<ThermalMachineState> _restorationCaptures = [];
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

    /// <summary>
    /// The stabilised capture, retained even when the start is later refused.
    /// </summary>
    /// <remarks>
    /// <see cref="OriginalState"/> is the restoration source for a session that ran.
    /// This is the forensic record: it survives a rejected start, so "why was this refused"
    /// can be answered from what was seen rather than from a later, different read.
    /// </remarks>
    public ThermalMachineState? CapturedRestorationState { get; private set; }

    /// <summary>
    /// Every capture the start attempt took, in order — A, then B, then C if needed.
    /// </summary>
    /// <remarks>
    /// Kept whole rather than collapsed to the accepted one. When stabilisation fails, the
    /// sequence <i>is</i> the finding: it shows what changed between reads, which a single
    /// surviving capture cannot.
    /// </remarks>
    public IReadOnlyList<ThermalMachineState> RestorationCaptures => _restorationCaptures;

    public ThermalMachineState? FinalState { get; private set; }

    /// <summary>
    /// The zone modes observed by the most recent successful fan write, or null when the last
    /// cycle wrote nothing.
    /// </summary>
    /// <remarks>
    /// Offered so a caller with its own ownership question can weigh this observation's age
    /// against its own deadline instead of issuing a second, near-identical 0x0D82 pair. It is
    /// an observation, not a conclusion: it carries when it was read, and says nothing about
    /// whether that is recent enough for any particular purpose.
    /// </remarks>
    public RazerOwnershipObservation? LastOwnershipObservation { get; private set; }

    public IReadOnlyList<ThermalDecision> Decisions => _decisions.ToArray();

    public IReadOnlyList<ThermalTraceEntry> Trace => _trace.ToArray();

    /// <summary>
    /// Reads the restoration state until the machine reports the same one twice in a row, or
    /// gives up after three attempts.
    /// </summary>
    /// <remarks>
    /// <para><b>Why every start, not only visibly odd ones.</b> A capture is six sequential
    /// GETs with no atomic firmware snapshot behind it. A pair of agreeing zones proves the two
    /// zone reads matched each other; it proves nothing about whether the CPU and GPU level
    /// reads that followed belonged to the same moment. Requiring two consecutive identical
    /// fingerprints replaces "the first read looked internally consistent" with the stronger and
    /// far simpler invariant: <i>the state we intend to restore was observed twice in a
    /// row</i>.</para>
    /// <para><b>What instability does and does not prove.</b> Two differing captures establish
    /// that the restoration state was not persistent across the read window. They cannot
    /// distinguish a brief firmware transition from a read sequence that straddled one, and this
    /// code does not claim to. It refuses either way, because either way the captured state is
    /// not something to promise to put back.</para>
    /// <para>Bounded and unpaced: at most three captures, no sleeps, no retry loop. Start is a
    /// one-shot ownership transition, so twelve GETs on the normal path is a cost worth paying
    /// once; the 500 ms telemetry path is untouched by any of this.</para>
    /// </remarks>
    /// <returns>The stabilised capture: B when A and B agree, otherwise C.</returns>
    /// <exception cref="ThermalPreflightException">
    /// The state never settled, or settled on something asymmetric. No SET has been sent.
    /// </exception>
    private ThermalMachineState StabilizeRestorationState()
    {
        ThermalMachineState first = Capture("A");
        ThermalMachineState second = Capture("B");
        if (IsStable(first, second))
        {
            return second;
        }

        ThermalMachineState third = Capture("C");
        if (IsStable(second, third))
        {
            return third;
        }

        throw new ThermalPreflightException(
            "Original performance state did not stabilize safely. " +
            DescribeCaptures() +
            " No SET was sent.");
    }

    /// <summary>
    /// Two captures are usable only if they describe the same restoration state <i>and</i> that
    /// state is one restoration can express.
    /// </summary>
    /// <remarks>
    /// Both halves are load-bearing. A stable asymmetric reading is still unrestorable —
    /// restoration writes a single performance mode to both zones — so it must never be accepted
    /// merely because the machine reported it consistently.
    /// </remarks>
    private static bool IsStable(ThermalMachineState earlier, ThermalMachineState later)
    {
        ThermalRestorationFingerprint first = earlier.RestorationFingerprint;
        ThermalRestorationFingerprint second = later.RestorationFingerprint;
        return first == second && first.ZonesAgree && second.ZonesAgree;
    }

    private ThermalMachineState Capture(string label)
    {
        ThermalMachineState state = _control.CaptureState();
        _restorationCaptures.Add(state);
        AddProtocolTrace(
            state.Exchanges,
            $"Restoration capture {label}: {state.Describe()}");
        return state;
    }

    /// <summary>Every capture taken, so a rejection carries the whole sequence.</summary>
    private string DescribeCaptures() => string.Join(
        " ",
        _restorationCaptures.Select((state, index) =>
            $"Capture {(char)('A' + index)}: {state.Describe()}."));

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

        // Steps 2-4: capture what will be restored when the session ends, and require the
        // machine to report it twice in a row before trusting it.
        OriginalState = StabilizeRestorationState();
        CapturedRestorationState = OriginalState;

        // Step 5: validate the stabilised capture as restoration data, before spending the
        // ownership read. A typed result rather than a caught exception: an unrestorable
        // capture is an expected prerequisite failure, and classifying it by exception type
        // would risk treating it as a poisoned runtime.
        if (!RazerThermalControlDevice.TryCreateRestorationProfile(
                OriginalState,
                out _,
                out string? restorationRejection))
        {
            throw new ThermalPreflightException(
                $"Original performance state cannot be restored safely: " +
                $"{restorationRejection}. No SET was sent.");
        }

        // Step 6: the single authoritative fan-ownership gate, and the last meaningful
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

        // Step 7: the ownership decision.
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

        // The same two GETs also close the last gap in the restoration promise. Stabilization
        // established what to put back; between then and now the machine could have moved to a
        // different performance mode, and the session would restore a state that was already
        // stale when it was adopted.
        //
        // No extra read: 0x0D82 returns performance mode alongside fan mode, so this costs
        // nothing and adds nothing to the window between deciding and acting. CPU and GPU
        // levels are deliberately not rechecked here — 0x0D87 would be two more GETs after the
        // ownership decision, widening exactly the window this ordering exists to keep narrow.
        // They stay covered by the two-consecutive-fingerprint requirement.
        if (observed.Zone1PerformanceMode != OriginalState.Zone1PerformanceMode ||
            observed.Zone2PerformanceMode != OriginalState.Zone2PerformanceMode)
        {
            throw new ThermalPreflightException(
                "Performance state changed after restoration stabilization; ownership was " +
                "not taken. " +
                $"Stabilized: Z1 {OriginalState.Zone1PerformanceMode}, " +
                $"Z2 {OriginalState.Zone2PerformanceMode}. " +
                $"Final: Z1 {observed.Zone1PerformanceMode}, " +
                $"Z2 {observed.Zone2PerformanceMode}. No SET was sent.");
        }

        // Step 8.
        State = ThermalControllerStateKind.Ready;
        ThermalControlOperationResult entry = _control.EnterManualBaseline(
            new FanRpm(ThermalCurve.MinimumDynamicRpm));
        AddProtocolTrace(
            entry.Exchanges,
            "Enter Manual in the current performance mode and set 3000 RPM baseline");
        if (!entry.Succeeded || entry.FinalState?.IsOwnedManual != true)
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
        LastOwnershipObservation = null;
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
            // No write, no fresh observation. Leaving the previous one in place would let a
            // reading from an earlier cycle answer a later question.
            LastOwnershipObservation = null;
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

        // The scoped write reads ownership, not a whole machine state, so the last fully read
        // state stands. Every consumer of FinalState — stop, emergency handoff, restoration
        // comparison — is fed by a path that does read one.
        FinalState = apply.FinalState ?? FinalState;
        LastOwnershipObservation = apply.Ownership;
        _engine.RecordSuccessfulWrite(decision);
        return decision;
    }

    public ThermalSessionResult Stop()
    {
        if (State == ThermalControllerStateKind.Manual)
        {
            ReturnToAutoOnce("Normal stop: firmware handoff before performance restoration.");
            if (FinalState?.IsKnownAuto == true)
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
        if (FinalState?.IsKnownAuto == true)
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
        ThermalControlOperationResult auto = _control.ReturnToFirmwareAuto();
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
