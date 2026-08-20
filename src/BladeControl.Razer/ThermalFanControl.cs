using System.Diagnostics;

namespace BladeControl.Razer;

/// <summary>
/// Both zones' performance and fan mode, read as a pair, with the moment the second response
/// arrived.
/// </summary>
/// <remarks>
/// <para>Two GET 0x0D82 exchanges and nothing else. This is what "does BladeControl still own
/// the fans" actually needs; fan state (0x0D81) and performance levels (0x0D87) answer
/// different questions and are read separately when those questions are being asked.</para>
/// <para><see cref="CompletedTimestamp"/> is a raw <see cref="Stopwatch"/> timestamp taken
/// immediately after the second response, not when the enclosing operation finished. An
/// observation that reports itself fresher than it is would let a stale reading satisfy a
/// watchdog deadline, so the stamp is placed at the read, never at the return.</para>
/// </remarks>
public sealed record RazerOwnershipObservation(
    RazerPerformanceMode Zone1PerformanceMode,
    RazerFanMode Zone1FanMode,
    RazerPerformanceMode Zone2PerformanceMode,
    RazerFanMode Zone2FanMode,
    long CompletedTimestamp,
    IReadOnlyList<RazerExchangeTrace> Exchanges)
{
    public bool ZonesAgree =>
        Zone1PerformanceMode == Zone2PerformanceMode && Zone1FanMode == Zone2FanMode;

    public bool IsBalancedManual => ZonesAgree &&
        Zone1PerformanceMode == RazerPerformanceMode.Balanced &&
        Zone1FanMode == RazerFanMode.Manual;

    public bool IsAuto => ZonesAgree && Zone1FanMode == RazerFanMode.Auto;

    /// <summary>How long ago the second 0x0D82 response arrived.</summary>
    public TimeSpan Age => Stopwatch.GetElapsedTime(CompletedTimestamp);

    public string Describe() =>
        $"zone 1 {Zone1PerformanceMode} / {Zone1FanMode}, " +
        $"zone 2 {Zone2PerformanceMode} / {Zone2FanMode}";
}

/// <summary>
/// The firmware-reported fan state of both zones after a target write.
/// </summary>
/// <remarks>
/// 0x0D81 returns what firmware reports for the fan, which is the commanded target echoed
/// back. It is not a tachometer reading and no part of this system treats it as one.
/// </remarks>
public sealed record RazerFanStateObservation(
    int Zone1FirmwareReportedRpm,
    int Zone2FirmwareReportedRpm,
    IReadOnlyList<RazerExchangeTrace> Exchanges);

/// <summary>Outcome of a thermal fan-target write, with the reads that justified it.</summary>
public sealed record ThermalFanTargetResult(
    bool Succeeded,
    bool AnyWriteAttempted,
    bool AutoRecoveryAttempted,
    bool AutoActive,
    string Message,
    RazerOwnershipObservation? OwnershipAfterWrite,
    FanControlState? RecoveredState,
    IReadOnlyList<RazerExchangeTrace> Exchanges);

public sealed partial class RazerClient
{
    /// <summary>
    /// Reads both zones' performance and fan mode: two GET 0x0D82 exchanges.
    /// </summary>
    internal RazerOwnershipObservation ReadOwnershipObservation()
    {
        RazerModeReading zone1 = GetPerformanceAndFanMode(RazerZone.Zone1);
        RazerModeReading zone2 = GetPerformanceAndFanMode(RazerZone.Zone2);

        // Stamped here, between the second response and anything else, so the age this
        // observation reports is the age of the read rather than of the operation.
        long completed = Stopwatch.GetTimestamp();
        return new RazerOwnershipObservation(
            zone1.PerformanceMode,
            zone1.FanMode,
            zone2.PerformanceMode,
            zone2.FanMode,
            completed,
            [zone1.Exchange, zone2.Exchange]);
    }

    /// <summary>Reads both zones' firmware-reported fan state: two GET 0x0D81 exchanges.</summary>
    internal RazerFanStateObservation ReadFanStateObservation()
    {
        RazerFanReading fan1 = GetFanRpm(RazerZone.Zone1);
        RazerFanReading fan2 = GetFanRpm(RazerZone.Zone2);
        return new RazerFanStateObservation(
            fan1.FirmwareReportedRpm,
            fan2.FirmwareReportedRpm,
            [fan1.Exchange, fan2.Exchange]);
    }

    /// <summary>
    /// Applies a dynamic thermal fan target using only the exchanges the operation consumes.
    /// </summary>
    /// <remarks>
    /// <para>This used to call the generic six-GET <c>ReadCompleteStatus</c> helper twice — once
    /// as a precondition and once as verification — for a total of sixteen HID exchanges per
    /// target change. Ten of those reads fed no predicate: the precondition only ever checked
    /// ownership, and CPU/GPU performance levels (0x0D87) have no bearing on whether a fan
    /// target was written. On a 500 ms control period with roughly 390 ms already spent
    /// acquiring telemetry, that surplus was the direct cause of the observed cycle
    /// overruns.</para>
    /// <para>The sequence is now exactly what is validated:</para>
    /// <code>
    /// 0x0D82 Z1, Z2   precondition: ownership still Balanced + Manual, zones agree
    /// 0x0D01 Z1, Z2   the write, echo-validated as before
    /// 0x0D81 Z1, Z2   verification: firmware-reported fan state equals the commanded target
    /// 0x0D82 Z1, Z2   verification: ownership still held, zones still agree
    /// </code>
    /// <para>Nothing is weakened. Every check that existed still runs against a read that was
    /// actually taken; the reads that were dropped were the ones nothing looked at. Failure
    /// still routes to <c>AttemptEmergencyAuto</c>, which keeps its full state read because a
    /// recovery genuinely needs the complete picture.</para>
    /// <para>Verification reads fan state before ownership deliberately: it puts the 0x0D82
    /// pair last, so the ownership observation this returns is the freshest thing in the
    /// operation.</para>
    /// </remarks>
    internal ThermalFanTargetResult ApplyThermalFanTarget(FanRpm target)
    {
        if (!target.IsValid || target.Value < 3000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "Thermal Control V1 fan targets must be 3000..5000 RPM in 100-RPM increments.");
        }

        var exchanges = new List<RazerExchangeTrace>(8);

        // --- Precondition: ownership only ---------------------------------------------------
        RazerOwnershipObservation before = ReadOwnershipObservation();
        exchanges.AddRange(before.Exchanges);
        if (!before.IsBalancedManual)
        {
            throw new FanControlStateException(
                "A dynamic thermal target requires verified Balanced + Manual state in both " +
                $"zones; firmware reported {before.Describe()}. No SET was sent.");
        }

        // --- The write ------------------------------------------------------------------------
        FanControlOperation[] operations =
        [
            new(FanControlOperationKind.SetFan1Rpm, target),
            new(FanControlOperationKind.SetFan2Rpm, target)
        ];
        foreach (FanControlOperation operation in operations)
        {
            try
            {
                exchanges.Add(ExecuteFanOperation(operation));
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                return Recover(
                    exchanges,
                    $"{operation.Description} failed: {exception.Message}");
            }
        }

        // --- Verification: fan state, then ownership last -------------------------------------
        RazerFanStateObservation fanState;
        RazerOwnershipObservation after;
        try
        {
            fanState = ReadFanStateObservation();
            exchanges.AddRange(fanState.Exchanges);
            after = ReadOwnershipObservation();
            exchanges.AddRange(after.Exchanges);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            return Recover(exchanges, $"Thermal target readback failed: {exception.Message}");
        }

        if (!after.IsBalancedManual)
        {
            return Recover(
                exchanges,
                "Fan ownership was lost during the target write; firmware reported " +
                $"{after.Describe()}.");
        }

        if (fanState.Zone1FirmwareReportedRpm != target.Value ||
            fanState.Zone2FirmwareReportedRpm != target.Value)
        {
            return Recover(
                exchanges,
                $"Exact thermal target validation failed: expected {target.Value}/{target.Value}, " +
                $"received {fanState.Zone1FirmwareReportedRpm}/" +
                $"{fanState.Zone2FirmwareReportedRpm}.");
        }

        return new ThermalFanTargetResult(
            true,
            true,
            false,
            false,
            $"Firmware reported the exact {target.Value} RPM target for both fans.",
            after,
            null,
            exchanges);
    }

    /// <summary>
    /// Hands the fans back to firmware after a failed target write.
    /// </summary>
    /// <remarks>
    /// The recovery keeps its full state read. Narrow reads are right when the question is
    /// narrow; establishing that firmware has safely taken the fans back is not.
    /// </remarks>
    private ThermalFanTargetResult Recover(
        List<RazerExchangeTrace> exchanges,
        string message)
    {
        FanAutoRecoveryResult recovery = AttemptEmergencyAuto();
        exchanges.AddRange(recovery.Operations
            .Where(operation => operation.Exchange is not null)
            .Select(operation => operation.Exchange!));
        return new ThermalFanTargetResult(
            false,
            true,
            true,
            recovery.FinalState?.IsBalancedAuto == true,
            message,
            null,
            recovery.FinalState,
            exchanges);
    }
}
