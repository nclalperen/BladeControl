using BladeControl.Razer;

namespace BladeControl.Thermal;

/// <summary>
/// Exactly the state a thermal session promises to put back, and nothing else.
/// </summary>
/// <remarks>
/// <para>Fan mode is deliberately absent. A session takes fan ownership and hands it back by
/// establishing firmware Auto, authorised by its own fresh 0x0D82 gate; the captured fan mode
/// is never written back, so letting it participate here would make an irrelevant field able
/// to destabilise a restoration that is otherwise identical.</para>
/// <para>Used to decide whether two consecutive captures describe the same machine. That is a
/// weaker and more honest claim than naming a physical cause: six sequential GETs with no
/// atomic firmware snapshot behind them cannot distinguish a brief hardware transition from a
/// read sequence that straddled one. What the software can establish is only whether the state
/// it intends to restore was observed twice in a row — that is, whether the restoration
/// snapshot was persistent.</para>
/// </remarks>
public readonly record struct ThermalRestorationFingerprint(
    RazerPerformanceMode Zone1PerformanceMode,
    RazerPerformanceMode Zone2PerformanceMode,
    RazerCpuPerformanceLevel CpuLevel,
    RazerGpuPerformanceLevel GpuLevel)
{
    /// <summary>
    /// Whether both zones report the same performance mode.
    /// </summary>
    /// <remarks>
    /// Restoration writes one performance mode to both zones, so an asymmetric fingerprint has
    /// no coherent restoration — even a perfectly stable one. Stability and symmetry are
    /// separate requirements and both are checked.
    /// </remarks>
    public bool ZonesAgree => Zone1PerformanceMode == Zone2PerformanceMode;
}

public sealed record ThermalMachineState(
    RazerDeviceInfo Device,
    RazerPerformanceMode Zone1PerformanceMode,
    RazerPerformanceMode Zone2PerformanceMode,
    RazerFanMode Zone1FanMode,
    RazerFanMode Zone2FanMode,
    RazerCpuPerformanceLevel CpuLevel,
    RazerGpuPerformanceLevel GpuLevel,
    int FirmwareReportedFan1Rpm,
    int FirmwareReportedFan2Rpm,
    IReadOnlyList<RazerExchangeTrace> Exchanges)
{
    public bool ZonesAgree =>
        Zone1PerformanceMode == Zone2PerformanceMode &&
        Zone1FanMode == Zone2FanMode;

    public bool IsAuto => ZonesAgree && Zone1FanMode == RazerFanMode.Auto;

    public bool IsBalancedManual => ZonesAgree &&
        Zone1PerformanceMode == RazerPerformanceMode.Balanced &&
        Zone1FanMode == RazerFanMode.Manual;

    public bool IsBalancedAuto => ZonesAgree &&
        Zone1PerformanceMode == RazerPerformanceMode.Balanced &&
        Zone1FanMode == RazerFanMode.Auto;

    /// <summary>The subset of this capture that restoration actually depends on.</summary>
    public ThermalRestorationFingerprint RestorationFingerprint => new(
        Zone1PerformanceMode,
        Zone2PerformanceMode,
        CpuLevel,
        GpuLevel);

    /// <summary>
    /// Everything the capture actually observed, for a rejection that explains itself.
    /// </summary>
    /// <remarks>
    /// A rejection that says only "the zones report different performance modes" throws away
    /// the one fact needed to act on it — <i>which</i> modes. Reproducing it afterwards means
    /// a second firmware read, by which time the machine has usually settled and the evidence
    /// is gone. That is exactly what happened in the field: the refusal was correct and
    /// completely unactionable.
    /// </remarks>
    public string Describe() =>
        $"zone 1 performance = {Zone1PerformanceMode}, " +
        $"zone 2 performance = {Zone2PerformanceMode}, " +
        $"zone 1 fan mode = {Zone1FanMode}, " +
        $"zone 2 fan mode = {Zone2FanMode}, " +
        $"CPU level = {CpuLevel}, GPU level = {GpuLevel}";
}

/// <summary>
/// The cheapest firmware observation that can authorise taking thermal ownership: the
/// performance and fan mode of both zones, and nothing else.
/// </summary>
/// <remarks>
/// Two GET 0x0D82 exchanges. Fan RPM (0x0D81) and performance levels (0x0D87) are
/// deliberately absent — neither says anything about who owns the fans, so neither may gate
/// the decision to take ownership.
/// </remarks>
public sealed record ThermalFanModeObservation(
    RazerPerformanceMode Zone1PerformanceMode,
    RazerFanMode Zone1FanMode,
    RazerPerformanceMode Zone2PerformanceMode,
    RazerFanMode Zone2FanMode,
    IReadOnlyList<RazerExchangeTrace> Exchanges)
{
    public bool ZonesAgree =>
        Zone1PerformanceMode == Zone2PerformanceMode &&
        Zone1FanMode == Zone2FanMode;

    /// <summary>Both zones agree and both report firmware Auto.</summary>
    public bool IsAuto => ZonesAgree && Zone1FanMode == RazerFanMode.Auto;

    /// <summary>What was actually observed, for a rejection message that explains itself.</summary>
    public string Describe() =>
        $"zone 1 {Zone1PerformanceMode} / {Zone1FanMode}, " +
        $"zone 2 {Zone2PerformanceMode} / {Zone2FanMode}";
}

/// <param name="Ownership">
/// The zone modes read after the write, when the operation took a scoped ownership
/// observation. Carries its own completion timestamp, so a caller can decide whether it is
/// fresh enough to answer a question of its own rather than issuing another read.
/// </param>
public sealed record ThermalControlOperationResult(
    bool Succeeded,
    bool AnyWriteAttempted,
    bool AutoRecoveryAttempted,
    bool AutoActive,
    string Message,
    ThermalMachineState? FinalState,
    IReadOnlyList<RazerExchangeTrace> Exchanges,
    RazerOwnershipObservation? Ownership = null);

public interface IThermalControlDevice
{
    ThermalMachineState CaptureState();

    /// <summary>
    /// Reads both zones' performance and fan mode from firmware. Used immediately before
    /// taking ownership so the decision rests on what the firmware reports now, not on a
    /// watchdog observation or a diagnostics snapshot taken at some earlier moment.
    /// </summary>
    ThermalFanModeObservation ReadFanModeObservation();

    ThermalControlOperationResult EnterManualBaseline(FanRpm baseline);

    ThermalControlOperationResult SetBothFans(FanRpm target);

    ThermalControlOperationResult ReturnToBalancedAuto();

    ThermalControlOperationResult RestorePerformance(ThermalMachineState originalState);
}

public sealed class RazerThermalControlDevice : IThermalControlDevice
{
    private readonly RazerClient _client;

    public RazerThermalControlDevice(RazerClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public ThermalMachineState CaptureState() => Convert(_client.GetFanControlState());

    public ThermalFanModeObservation ReadFanModeObservation()
    {
        RazerModeReading zone1 = _client.GetPerformanceAndFanMode(RazerZone.Zone1);
        RazerModeReading zone2 = _client.GetPerformanceAndFanMode(RazerZone.Zone2);
        return new ThermalFanModeObservation(
            zone1.PerformanceMode,
            zone1.FanMode,
            zone2.PerformanceMode,
            zone2.FanMode,
            [zone1.Exchange, zone2.Exchange]);
    }

    public ThermalControlOperationResult EnterManualBaseline(FanRpm baseline)
    {
        if (baseline.Value != ThermalCurve.MinimumDynamicRpm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseline),
                baseline,
                "Thermal Control V1 Manual entry requires the exact 3000 RPM baseline.");
        }

        return ApplyFanProfile(FanControlProfile.Fixed(baseline, baseline));
    }

    public ThermalControlOperationResult SetBothFans(FanRpm target)
    {
        ThermalFanTargetResult result = _client.ApplyThermalFanTarget(target);
        return new ThermalControlOperationResult(
            result.Succeeded,
            result.AnyWriteAttempted,
            result.AutoRecoveryAttempted,
            result.AutoActive,
            result.Message,

            // A successful scoped write reports no full machine state, because it deliberately
            // did not read one. The caller keeps the last state it actually read rather than
            // being handed a synthesized one; a recovery still returns the complete picture.
            result.RecoveredState is null ? null : Convert(result.RecoveredState),
            result.Exchanges,
            result.OwnershipAfterWrite);
    }

    public ThermalControlOperationResult ReturnToBalancedAuto() =>
        ApplyFanProfile(FanControlProfile.Auto);

    public ThermalControlOperationResult RestorePerformance(ThermalMachineState originalState)
    {
        ArgumentNullException.ThrowIfNull(originalState);
        PerformanceProfile profile = CreateRestorationProfile(originalState);
        PerformanceApplyResult result = _client.ApplyPerformanceProfile(profile);
        var exchanges = new List<RazerExchangeTrace>();
        exchanges.AddRange(result.InitialState.Exchanges);
        exchanges.AddRange(result.Operations
            .Where(operation => operation.Exchange is not null)
            .Select(operation => operation.Exchange!));
        if (result.FinalState is not null)
        {
            exchanges.AddRange(result.FinalState.Exchanges);
        }

        if (result.Restoration is not null)
        {
            exchanges.AddRange(result.Restoration.Operations
                .Where(operation => operation.Exchange is not null)
                .Select(operation => operation.Exchange!));
        }

        PerformanceState? final = result.Restoration?.FinalState ?? result.FinalState;
        return new ThermalControlOperationResult(
            result.Succeeded,
            result.Plan.Operations.Count > 0,
            false,
            final?.Zone1Mode.FanMode == RazerFanMode.Auto &&
                final.Zone2Mode.FanMode == RazerFanMode.Auto,
            result.Verification.Message,
            final is null ? null : Convert(final),
            exchanges);
    }

    /// <summary>
    /// Builds the performance profile that will restore the machine when the session ends.
    /// </summary>
    /// <remarks>
    /// <para>This answers "what should be restored later", never "is it safe to take ownership
    /// now". It previously required <c>state.IsAuto</c>, which bundled two unrelated
    /// conditions: that the zones agree (a real requirement — the profile is built from zone
    /// 1's performance mode alone, so disagreeing zones cannot be restored coherently) and
    /// that the fan mode was Auto (not a requirement at all — no branch below reads the fan
    /// mode).</para>
    /// <para>That fan-mode half was a second ownership gate sitting in front of the real one,
    /// evaluated against the six-GET capture rather than the fresh two-GET observation. Fan
    /// ownership is decided in exactly one place now, and it is not here. Restoring the fan
    /// mode is a separate step: the stop path calls ReturnToBalancedAuto before
    /// RestorePerformance, so the captured fan mode is never consulted for restoration.</para>
    /// </remarks>
    /// <returns>
    /// False with a caller-presentable <paramref name="rejection"/> when the captured data
    /// cannot describe a restorable state. Expected outcomes are returned, not thrown, so the
    /// start path can classify them as prerequisite rejections without catching broadly.
    /// </returns>
    internal static bool TryCreateRestorationProfile(
        ThermalMachineState state,
        out PerformanceProfile profile,
        out string? rejection)
    {
        ArgumentNullException.ThrowIfNull(state);
        profile = default!;

        if (state.Zone1PerformanceMode != state.Zone2PerformanceMode)
        {
            // The values, not merely the verdict. ReadCompleteStatus issues six sequential
            // GETs with no atomic firmware snapshot behind them, so a disagreeing pair may be
            // a non-persistent observation rather than the machine's settled state. The
            // captured values are what let the two be told apart afterwards.
            rejection =
                "the captured performance state cannot be represented by the current " +
                $"restoration model ({state.Describe()})";
            return false;
        }

        if (state.Zone1PerformanceMode == RazerPerformanceMode.Balanced)
        {
            profile = PerformanceProfile.Balanced;
            rejection = null;
            return true;
        }

        if (state.Zone1PerformanceMode == RazerPerformanceMode.Silent)
        {
            profile = PerformanceProfile.Silent;
            rejection = null;
            return true;
        }

        if (state.Zone1PerformanceMode == RazerPerformanceMode.Custom &&
            (state.CpuLevel == RazerCpuPerformanceLevel.Low ||
             state.CpuLevel == RazerCpuPerformanceLevel.Medium) &&
            state.GpuLevel == RazerGpuPerformanceLevel.Low)
        {
            profile = PerformanceProfile.Custom(state.CpuLevel, state.GpuLevel);
            rejection = null;
            return true;
        }

        rejection =
            "the captured performance state is outside the hardware-validated restoration " +
            $"policy ({state.Describe()})";
        return false;
    }

    /// <summary>
    /// Throwing form, for the stop path where an unrestorable capture is genuinely exceptional:
    /// the start path already validated it, so failing here means something changed underneath.
    /// </summary>
    internal static PerformanceProfile CreateRestorationProfile(ThermalMachineState state)
    {
        if (!TryCreateRestorationProfile(state, out PerformanceProfile profile, out string? rejection))
        {
            throw new InvalidOperationException(
                $"Cannot build a restoration profile: {rejection}.");
        }

        return profile;
    }

    private ThermalControlOperationResult ApplyFanProfile(FanControlProfile profile)
    {
        FanControlApplyResult result = _client.ApplyFanControlProfile(profile);
        return ConvertFanApply(result);
    }

    private static ThermalControlOperationResult ConvertFanApply(
        FanControlApplyResult result)
    {
        var exchanges = new List<RazerExchangeTrace>();
        exchanges.AddRange(result.InitialState.InitialExchanges);
        exchanges.AddRange(result.Operations
            .Where(operation => operation.Exchange is not null)
            .Select(operation => operation.Exchange!));
        exchanges.AddRange(result.ObservationExchanges);
        if (result.FinalState is not null && result.ObservationExchanges.Count == 0)
        {
            exchanges.AddRange(result.FinalState.InitialExchanges);
        }
        if (result.AutoRecovery is not null)
        {
            exchanges.AddRange(result.AutoRecovery.Operations
                .Where(operation => operation.Exchange is not null)
                .Select(operation => operation.Exchange!));
        }

        FanControlState? final = result.AutoRecovery?.FinalState ?? result.FinalState;
        return new ThermalControlOperationResult(
            result.Succeeded,
            result.Plan.Operations.Count > 0,
            result.AutoRecovery is not null,
            final?.IsBalancedAuto == true,
            result.Verification.Message,
            final is null ? null : Convert(final),
            exchanges);
    }

    private static ThermalMachineState Convert(FanControlState state) => new(
        state.Device,
        state.Zone1Mode.PerformanceMode,
        state.Zone2Mode.PerformanceMode,
        state.Zone1Mode.FanMode,
        state.Zone2Mode.FanMode,
        state.CpuPerformanceLevel,
        state.GpuPerformanceLevel,
        state.Fan1.FirmwareReportedRpm,
        state.Fan2.FirmwareReportedRpm,
        state.InitialExchanges.Concat(state.ObservationExchanges).ToArray());

    private static ThermalMachineState Convert(PerformanceState state) => new(
        state.Device,
        state.Zone1Mode.PerformanceMode,
        state.Zone2Mode.PerformanceMode,
        state.Zone1Mode.FanMode,
        state.Zone2Mode.FanMode,
        state.CpuPerformanceLevel,
        state.GpuPerformanceLevel,
        state.Fan1.FirmwareReportedRpm,
        state.Fan2.FirmwareReportedRpm,
        state.Exchanges);
}
