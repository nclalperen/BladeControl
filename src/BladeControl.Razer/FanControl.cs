using BladeControl.Razer.Protocol;

namespace BladeControl.Razer;

public static class FanControlSafety
{
    public const int RpmTolerance = 100;
    public const int MaximumObservationMilliseconds = 5000;
    public const int ObservationIntervalMilliseconds = 500;
}

public sealed partial class RazerClient
{
    private const int MaximumObservationIntervals =
        FanControlSafety.MaximumObservationMilliseconds /
        FanControlSafety.ObservationIntervalMilliseconds;

    public FanControlState GetFanControlState() => new(GetPerformanceState());

    public FanControlApplyResult ApplyFanControlProfile(FanControlProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateFanProfile(profile);

        FanControlState initialState = GetFanControlState();
        ValidateFanControlState(initialState);
        FanApplyAttempt attempt = ExecuteFanApply(initialState, profile);
        if (attempt.Succeeded)
        {
            return new FanControlApplyResult(
                initialState,
                profile,
                attempt.Plan,
                attempt.Operations,
                attempt.FinalState,
                attempt.ObservationExchanges,
                attempt.Verification,
                attempt.Plan.IsNoOp
                    ? FanControlApplyOutcome.NoChangesRequired
                    : FanControlApplyOutcome.Applied,
                autoRecovery: null);
        }

        FanAutoRecoveryResult? autoRecovery = null;
        bool manualSafetyApplies = initialState.IsManual ||
            attempt.ManualMayHaveBeenEntered;
        if (manualSafetyApplies)
        {
            // Recover into the mode the plan was operating in, so a failed write in Silent
            // hands the fans back to firmware without also moving the machine to Balanced.
            autoRecovery = profile.IsFixed
                ? AttemptEmergencyAuto(initialState.OwnershipPerformanceMode)
                : AssessFailedAutoTransition(attempt);
        }

        FanControlApplyOutcome outcome = autoRecovery is null
            ? FanControlApplyOutcome.Failed
            : autoRecovery.Succeeded
                ? FanControlApplyOutcome.AutoRestored
                : FanControlApplyOutcome.AutoRestorationFailed;
        return new FanControlApplyResult(
            initialState,
            profile,
            attempt.Plan,
            attempt.Operations,
            attempt.FinalState,
            attempt.ObservationExchanges,
            attempt.Verification,
            outcome,
            autoRecovery);
    }

    public FanControlSelfTestResult RunFanControlSelfTest()
    {
        FanControlState initialState = GetFanControlState();
        ValidateFanSelfTestInitialState(initialState);
        PerformanceProfile initialProfile = PerformanceProfile.Custom(
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        var stages = new List<FanControlSelfTestStageResult>(capacity: 5);

        ManualEntryAttempt manualEntry = ExecuteManualEntryForSelfTest();
        stages.Add(manualEntry.Stage);
        if (!manualEntry.Stage.Succeeded)
        {
            return RecoverFailedFanSelfTest(
                initialState,
                initialProfile,
                stages,
                manualEntry.ManualMayHaveBeenEntered,
                existingAutoRecovery: null);
        }

        FanControlApplyResult fixed4000 = ApplyFanControlProfile(
            FanControlProfile.Fixed(new FanRpm(4000), new FanRpm(4000)));
        stages.Add(CreateFanApplySelfTestStage(
            "C - Fixed 4000/4000",
            fixed4000));
        if (!fixed4000.Succeeded)
        {
            return RecoverFailedFanSelfTest(
                initialState,
                initialProfile,
                stages,
                manualMayBeActive: true,
                fixed4000.AutoRecovery);
        }

        FanControlApplyResult asymmetric = ApplyFanControlProfile(
            FanControlProfile.Fixed(new FanRpm(3000), new FanRpm(3500)));
        stages.Add(CreateFanApplySelfTestStage(
            "D - Independent 3000/3500",
            asymmetric));
        if (!asymmetric.Succeeded)
        {
            return RecoverFailedFanSelfTest(
                initialState,
                initialProfile,
                stages,
                manualMayBeActive: true,
                asymmetric.AutoRecovery);
        }

        FanControlApplyResult auto = ApplyFanControlProfile(FanControlProfile.Auto);
        stages.Add(CreateFanApplySelfTestStage("E - Balanced + Auto", auto));
        if (!auto.Succeeded)
        {
            return RecoverFailedFanSelfTest(
                initialState,
                initialProfile,
                stages,
                manualMayBeActive: true,
                auto.AutoRecovery);
        }

        PerformanceApplyResult performanceRestore = ApplyPerformanceProfile(
            initialProfile,
            initialProfile);
        PerformanceState? finalPerformanceState =
            performanceRestore.Restoration?.FinalState ??
            performanceRestore.FinalState;
        FanControlState? finalState = finalPerformanceState is null
            ? null
            : new FanControlState(finalPerformanceState);
        bool exactRestore = performanceRestore.Succeeded &&
            finalState is not null &&
            FanNonTelemetryStateEquals(initialState, finalState);
        bool restoredAfterFailure = !performanceRestore.Succeeded &&
            finalState is not null &&
            FanNonTelemetryStateEquals(initialState, finalState);
        stages.Add(new FanControlSelfTestStageResult(
            "F - Restore initial performance",
            exactRestore,
            exactRestore
                ? "Initial non-telemetry state restored."
                : performanceRestore.Verification.Message,
            finalState,
            [],
            performanceApply: performanceRestore));

        return new FanControlSelfTestResult(
            initialState,
            stages,
            exactRestore,
            exactRestore
                ? "PASS - Fan Control V1 completed and the initial state was restored."
                : restoredAfterFailure
                    ? "SELFTEST FAILED - INITIAL STATE RESTORED"
                    : "PERFORMANCE RESTORATION FAILED",
            performanceRestoration: performanceRestore,
            finalState: finalState);
    }

    private FanApplyAttempt ExecuteFanApply(
        FanControlState initialState,
        FanControlProfile profile)
    {
        FanControlPlan plan = BuildFanControlPlan(initialState, profile);
        var operations = new List<FanControlOperationResult>(plan.Operations.Count);
        bool manualMayHaveBeenEntered = initialState.IsManual;

        foreach (FanControlOperation operation in plan.Operations)
        {
            if (operation.Kind is
                FanControlOperationKind.SetManualZone1 or
                FanControlOperationKind.SetManualZone2)
            {
                manualMayHaveBeenEntered = true;
            }

            try
            {
                RazerExchangeTrace exchange = ExecuteFanOperation(operation);
                operations.Add(new FanControlOperationResult(
                    operation,
                    true,
                    exchange,
                    null));
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                RazerExchangeTrace? exchange = LastExchange(exception);
                operations.Add(new FanControlOperationResult(
                    operation,
                    false,
                    exchange,
                    exception.Message));
                return FanApplyAttempt.Failed(
                    plan,
                    operations,
                    $"{operation.Description} failed: {exception.Message}",
                    manualMayHaveBeenEntered);
            }
        }

        PerformanceState postPerformanceState;
        try
        {
            postPerformanceState = GetPerformanceState();
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            return FanApplyAttempt.Failed(
                plan,
                operations,
                $"Post-apply state read failed: {exception.Message}",
                manualMayHaveBeenEntered);
        }

        var postState = new FanControlState(postPerformanceState);
        FanControlVerification modeVerification = VerifyFanMode(postState, profile);
        if (!modeVerification.Succeeded || !profile.IsFixed)
        {
            return new FanApplyAttempt(
                plan,
                operations,
                postState,
                [],
                modeVerification,
                manualMayHaveBeenEntered);
        }

        FanObservationResult observation = ObserveFixedFanTargets(
            postState,
            profile.Fan1Rpm!.Value,
            profile.Fan2Rpm!.Value);
        return new FanApplyAttempt(
            plan,
            operations,
            observation.FinalState,
            observation.Exchanges,
            observation.Verification,
            manualMayHaveBeenEntered);
    }

    private static FanControlPlan BuildFanControlPlan(
        FanControlState currentState,
        FanControlProfile profile)
    {
        var operations = new List<FanControlOperation>(capacity: 4);

        // The mode the machine is already in is the mode the plan writes. Taking fan ownership
        // is a statement about the fans, not about the user's performance preference, and this
        // used to move them to Balanced regardless of what they had chosen.
        //
        // Balanced is the fallback only when there is no single current mode to preserve: the
        // zones disagree, or the controller reported a mode byte this build does not model.
        // Writing back a mode we cannot name is not an option, and Balanced is the one mode
        // every path here has always been able to reach.
        RazerPerformanceMode mode =
            currentState.OwnershipPerformanceMode ?? RazerPerformanceMode.Balanced;

        if (!profile.IsFixed)
        {
            if (!currentState.IsAuto || currentState.OwnershipPerformanceMode is null)
            {
                operations.Add(new FanControlOperation(
                    FanControlOperationKind.SetAutoZone1,
                    performanceMode: mode));
                operations.Add(new FanControlOperation(
                    FanControlOperationKind.SetAutoZone2,
                    performanceMode: mode));
            }

            return new FanControlPlan(operations);
        }

        bool enteringManual = !currentState.IsManual ||
            currentState.OwnershipPerformanceMode != mode;
        if (enteringManual)
        {
            operations.Add(new FanControlOperation(
                FanControlOperationKind.SetManualZone1,
                performanceMode: mode));
            operations.Add(new FanControlOperation(
                FanControlOperationKind.SetManualZone2,
                performanceMode: mode));
        }

        FanRpm fan1Target = profile.Fan1Rpm!.Value;
        FanRpm fan2Target = profile.Fan2Rpm!.Value;
        if (enteringManual || !IsWithinTolerance(
                currentState.Fan1.FirmwareReportedRpm,
                fan1Target.Value))
        {
            operations.Add(new FanControlOperation(
                FanControlOperationKind.SetFan1Rpm,
                fan1Target));
        }

        if (enteringManual || !IsWithinTolerance(
                currentState.Fan2.FirmwareReportedRpm,
                fan2Target.Value))
        {
            operations.Add(new FanControlOperation(
                FanControlOperationKind.SetFan2Rpm,
                fan2Target));
        }

        return new FanControlPlan(operations);
    }

    private RazerExchangeTrace ExecuteFanOperation(FanControlOperation operation)
    {
        return operation.Kind switch
        {
            FanControlOperationKind.SetManualZone1 =>
                WritePerformanceAndFanMode(
                    RazerZone.Zone1,
                    operation.PerformanceMode!.Value,
                    RazerFanMode.Manual),
            FanControlOperationKind.SetManualZone2 =>
                WritePerformanceAndFanMode(
                    RazerZone.Zone2,
                    operation.PerformanceMode!.Value,
                    RazerFanMode.Manual),
            FanControlOperationKind.SetFan1Rpm =>
                WriteFanRpm(RazerZone.Zone1, operation.Rpm!.Value),
            FanControlOperationKind.SetFan2Rpm =>
                WriteFanRpm(RazerZone.Zone2, operation.Rpm!.Value),
            FanControlOperationKind.SetAutoZone1 =>
                WritePerformanceAndFanMode(
                    RazerZone.Zone1,
                    operation.PerformanceMode!.Value,
                    RazerFanMode.Auto),
            FanControlOperationKind.SetAutoZone2 =>
                WritePerformanceAndFanMode(
                    RazerZone.Zone2,
                    operation.PerformanceMode!.Value,
                    RazerFanMode.Auto),
            _ => throw new InvalidOperationException(
                $"Unsupported fan operation {operation.Kind}.")
        };
    }

    private RazerExchangeTrace WriteFanRpm(RazerZone zone, FanRpm rpm)
    {
        byte transactionId = _transactionIds.NextTransactionId();
        RazerPacket request = RazerCommands.CreateSetFanRpm(
            transactionId,
            zone,
            rpm);
        return ExchangeWriteAndValidateEcho(
            request,
            (byte)zone,
            "zone",
            minimumResponseDataSize: 3);
    }

    private FanObservationResult ObserveFixedFanTargets(
        FanControlState postState,
        FanRpm fan1Target,
        FanRpm fan2Target)
    {
        RazerFanReading fan1 = postState.Fan1;
        RazerFanReading fan2 = postState.Fan2;
        var exchanges = new List<RazerExchangeTrace>(
            MaximumObservationIntervals * 2);
        if (BothFansWithinTolerance(fan1, fan2, fan1Target, fan2Target))
        {
            return FanObservationResult.Success(postState, exchanges);
        }

        for (int interval = 1; interval <= MaximumObservationIntervals; interval++)
        {
            _fanObservationDelay.Wait(TimeSpan.FromMilliseconds(
                FanControlSafety.ObservationIntervalMilliseconds));
            try
            {
                fan1 = GetFanRpm(RazerZone.Zone1);
                exchanges.Add(fan1.Exchange);
                fan2 = GetFanRpm(RazerZone.Zone2);
                exchanges.Add(fan2.Exchange);
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                RazerExchangeTrace? exchange = LastExchange(exception);
                if (exchange is not null)
                {
                    exchanges.Add(exchange);
                }

                return FanObservationResult.Failure(
                    new FanControlState(postState.PerformanceState, fan1, fan2, exchanges),
                    exchanges,
                    $"Fan RPM observation failed: {exception.Message}");
            }

            if (BothFansWithinTolerance(fan1, fan2, fan1Target, fan2Target))
            {
                return FanObservationResult.Success(
                    new FanControlState(postState.PerformanceState, fan1, fan2, exchanges),
                    exchanges);
            }
        }

        var finalState = new FanControlState(
            postState.PerformanceState,
            fan1,
            fan2,
            exchanges);
        return FanObservationResult.Failure(
            finalState,
            exchanges,
            $"FAN RPM VERIFICATION TIMEOUT after " +
            $"{FanControlSafety.MaximumObservationMilliseconds} ms: " +
            $"Fan 1 expected {fan1Target.Value} +/- {FanControlSafety.RpmTolerance}, " +
            $"received {fan1.FirmwareReportedRpm}; Fan 2 expected " +
            $"{fan2Target.Value} +/- {FanControlSafety.RpmTolerance}, " +
            $"received {fan2.FirmwareReportedRpm}. No SET was repeated.");
    }

    /// <summary>Hands the fans back to firmware after a failed write.</summary>
    /// <param name="mode">
    /// The performance mode to write alongside Auto. Callers pass the mode they were operating
    /// in, so a failure in Silent does not silently move the machine to Balanced. Balanced is
    /// the default only for callers that have no mode to preserve.
    /// </param>
    private FanAutoRecoveryResult AttemptEmergencyAuto(RazerPerformanceMode? mode = null)
    {
        RazerPerformanceMode target = mode ?? RazerPerformanceMode.Balanced;
        var operations = new List<FanControlOperationResult>(capacity: 2);
        FanControlOperation[] recoveryPlan =
        [
            new(FanControlOperationKind.SetAutoZone1, performanceMode: target),
            new(FanControlOperationKind.SetAutoZone2, performanceMode: target)
        ];

        foreach (FanControlOperation operation in recoveryPlan)
        {
            try
            {
                RazerExchangeTrace exchange = ExecuteFanOperation(operation);
                operations.Add(new FanControlOperationResult(
                    operation,
                    true,
                    exchange,
                    null));
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                operations.Add(new FanControlOperationResult(
                    operation,
                    false,
                    LastExchange(exception),
                    exception.Message));
                return new FanAutoRecoveryResult(
                    false,
                    operations,
                    TryReadFanControlState(),
                    $"FAN AUTO RESTORATION FAILED: {operation.Description}: " +
                    exception.Message);
            }
        }

        FanControlState? finalState = TryReadFanControlState();
        bool succeeded = finalState?.IsBalancedAuto == true;
        return new FanAutoRecoveryResult(
            succeeded,
            operations,
            finalState,
            succeeded
                ? "Fan firmware control restored to Balanced + Auto."
                : "FAN AUTO RESTORATION FAILED: readback did not confirm " +
                  "Balanced + Auto.");
    }

    private FanAutoRecoveryResult AssessFailedAutoTransition(FanApplyAttempt attempt)
    {
        FanControlState? finalState = attempt.FinalState ?? TryReadFanControlState();
        bool succeeded = finalState?.IsBalancedAuto == true;
        return new FanAutoRecoveryResult(
            succeeded,
            attempt.Operations,
            finalState,
            succeeded
                ? "Fan firmware control restored to Balanced + Auto."
                : "FAN AUTO RESTORATION FAILED: the single Auto transition did " +
                  "not validate. No Auto SET was repeated.");
    }

    private ManualEntryAttempt ExecuteManualEntryForSelfTest()
    {
        var operationResults = new List<FanControlOperationResult>(capacity: 2);
        var exchanges = new List<RazerExchangeTrace>(capacity: 8);
        // Balanced explicitly: the self-test asserts against the Balanced + Manual baseline
        // and is not a test of mode preservation.
        FanControlOperation[] operations =
        [
            new(
                FanControlOperationKind.SetManualZone1,
                performanceMode: RazerPerformanceMode.Balanced),
            new(
                FanControlOperationKind.SetManualZone2,
                performanceMode: RazerPerformanceMode.Balanced)
        ];
        bool manualMayHaveBeenEntered = false;

        foreach (FanControlOperation operation in operations)
        {
            manualMayHaveBeenEntered = true;
            try
            {
                RazerExchangeTrace exchange = ExecuteFanOperation(operation);
                exchanges.Add(exchange);
                operationResults.Add(new FanControlOperationResult(
                    operation,
                    true,
                    exchange,
                    null));
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                RazerExchangeTrace? exchange = LastExchange(exception);
                if (exchange is not null)
                {
                    exchanges.Add(exchange);
                }

                operationResults.Add(new FanControlOperationResult(
                    operation,
                    false,
                    exchange,
                    exception.Message));
                return new ManualEntryAttempt(
                    new FanControlSelfTestStageResult(
                        "B - Enter Balanced + Manual",
                        false,
                        $"{operation.Description} failed: {exception.Message}",
                        null,
                        exchanges),
                    manualMayHaveBeenEntered);
            }
        }

        FanControlState state;
        try
        {
            state = GetFanControlState();
            exchanges.AddRange(state.InitialExchanges);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            return new ManualEntryAttempt(
                new FanControlSelfTestStageResult(
                    "B - Enter Balanced + Manual",
                    false,
                    $"Manual mode readback failed: {exception.Message}",
                    null,
                    exchanges),
                manualMayHaveBeenEntered);
        }

        bool succeeded = state.IsBalancedManual;
        return new ManualEntryAttempt(
            new FanControlSelfTestStageResult(
                "B - Enter Balanced + Manual",
                succeeded,
                succeeded
                    ? "Balanced + Manual verified in both zones."
                    : "Manual mode verification mismatch.",
                state,
                exchanges),
            manualMayHaveBeenEntered);
    }

    private FanControlSelfTestResult RecoverFailedFanSelfTest(
        FanControlState initialState,
        PerformanceProfile initialProfile,
        IReadOnlyList<FanControlSelfTestStageResult> stages,
        bool manualMayBeActive,
        FanAutoRecoveryResult? existingAutoRecovery)
    {
        if (!manualMayBeActive)
        {
            return new FanControlSelfTestResult(
                initialState,
                stages,
                false,
                "SELFTEST FAILED before Manual mode was entered; no recovery SET was required.");
        }

        FanAutoRecoveryResult autoRecovery =
            existingAutoRecovery ?? AttemptEmergencyAuto();
        if (!autoRecovery.Succeeded)
        {
            return new FanControlSelfTestResult(
                initialState,
                stages,
                false,
                "FAN AUTO RESTORATION FAILED",
                autoRecovery,
                finalState: autoRecovery.FinalState);
        }

        PerformanceApplyResult performanceRestoration = ApplyPerformanceProfile(
            initialProfile,
            initialProfile);
        FanControlState? finalState = performanceRestoration.FinalState is null
            ? performanceRestoration.Restoration?.FinalState is null
                ? null
                : new FanControlState(
                    performanceRestoration.Restoration.FinalState)
            : new FanControlState(performanceRestoration.FinalState);
        bool restored = finalState is not null &&
            FanNonTelemetryStateEquals(initialState, finalState);
        return new FanControlSelfTestResult(
            initialState,
            stages,
            false,
            restored
                ? "SELFTEST FAILED - INITIAL STATE RESTORED"
                : "PERFORMANCE RESTORATION FAILED",
            autoRecovery,
            performanceRestoration,
            finalState);
    }

    private static FanControlSelfTestStageResult CreateFanApplySelfTestStage(
        string stage,
        FanControlApplyResult apply)
    {
        var exchanges = new List<RazerExchangeTrace>();
        exchanges.AddRange(apply.InitialState.InitialExchanges);
        exchanges.AddRange(apply.Operations
            .Where(operation => operation.Exchange is not null)
            .Select(operation => operation.Exchange!));
        exchanges.AddRange(apply.FinalState?.InitialExchanges ?? []);
        exchanges.AddRange(apply.ObservationExchanges);
        return new FanControlSelfTestStageResult(
            stage,
            apply.Succeeded,
            apply.Verification.Message,
            apply.FinalState,
            exchanges,
            apply);
    }

    private static FanControlVerification VerifyFanMode(
        FanControlState state,
        FanControlProfile profile)
    {
        if (!state.ZonesAgree)
        {
            return new FanControlVerification(
                false,
                "Fan verification mismatch: performance or fan mode differs between zones.");
        }

        // Verification is about the fan mode. The performance mode is whatever the user had,
        // and is asserted to be unchanged by the zone-agreement check above plus the plan
        // writing the mode it read — not by demanding it be Balanced.
        bool modeMatches = profile.IsFixed ? state.IsManual : state.IsAuto;
        string observedMode = state.Zone1Mode.PerformanceMode.ToString();
        return modeMatches
            ? new FanControlVerification(
                true,
                profile.IsFixed
                    ? $"{observedMode} + Manual verified; RPM targets verified."
                    : $"{observedMode} + Auto verified; firmware fan control is active.")
            : new FanControlVerification(
                false,
                $"Fan verification mismatch: expected fan mode " +
                (profile.IsFixed ? "Manual" : "Auto") +
                $", observed {observedMode} + {state.Zone1Mode.FanMode}.");
    }

    private static void ValidateFanProfile(FanControlProfile profile)
    {
        if (profile.Kind == FanControlProfileKind.Auto)
        {
            return;
        }

        if (profile.Kind != FanControlProfileKind.Fixed ||
            profile.Fan1Rpm is not FanRpm fan1 || !fan1.IsValid ||
            profile.Fan2Rpm is not FanRpm fan2 || !fan2.IsValid)
        {
            throw new ArgumentException("Invalid fan-control profile.", nameof(profile));
        }
    }

    private static void ValidateFanControlState(FanControlState state)
    {
        if (!state.ZonesAgree)
        {
            throw new FanControlStateException(
                "Current performance or fan mode differs between zones. No SET command was sent.");
        }

        RazerPerformanceMode performance = state.Zone1Mode.PerformanceMode;
        RazerFanMode fanMode = state.Zone1Mode.FanMode;

        // Manual used to be accepted only alongside Balanced here, mirroring the packet
        // validator. That made the path back to firmware conditional on already being
        // somewhere this code recognised: a machine left in Silent + Manual could not be
        // handed back to Auto, because the refusal fired before the plan was even built. It
        // was observed exactly once, and it is the wrong direction to fail in — declining to
        // stop owning the fans is worse than declining to start.
        //
        // What actually has to hold is that the mode is one we can name, so the plan can write
        // it back. An unknown mode byte is still refused.
        if (!performance.IsKnown)
        {
            throw new FanControlStateException(
                $"Current performance mode {performance} is not one Fan Control models, so it " +
                "cannot be preserved. No SET command was sent.");
        }

        if (fanMode != RazerFanMode.Auto && fanMode != RazerFanMode.Manual)
        {
            throw new FanControlStateException(
                $"Current combination {performance} + {fanMode} is not safe for " +
                "Fan Control V1. No SET command was sent.");
        }
    }

    private static void ValidateFanSelfTestInitialState(FanControlState state)
    {
        bool exact = state.ZonesAgree &&
            state.Zone1Mode.PerformanceMode == RazerPerformanceMode.Custom &&
            state.Zone1Mode.FanMode == RazerFanMode.Auto &&
            state.CpuPerformanceLevel == RazerCpuPerformanceLevel.Medium &&
            state.GpuPerformanceLevel == RazerGpuPerformanceLevel.Low;
        if (!exact)
        {
            throw new FanControlSelfTestPreconditionException(
                "Fan selftest requires initial state Custom + Auto, CPU Medium, " +
                "GPU Low. No SET command was sent.",
                state);
        }
    }

    private FanControlState? TryReadFanControlState()
    {
        try
        {
            return GetFanControlState();
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            return null;
        }
    }

    private static bool BothFansWithinTolerance(
        RazerFanReading fan1,
        RazerFanReading fan2,
        FanRpm fan1Target,
        FanRpm fan2Target) =>
        IsWithinTolerance(fan1.FirmwareReportedRpm, fan1Target.Value) &&
        IsWithinTolerance(fan2.FirmwareReportedRpm, fan2Target.Value);

    private static bool IsWithinTolerance(int actual, int expected) =>
        Math.Abs(actual - expected) <= FanControlSafety.RpmTolerance;

    private static RazerExchangeTrace? LastExchange(Exception exception) =>
        exception is RazerProtocolException protocol && protocol.Exchanges.Count > 0
            ? protocol.Exchanges[^1]
            : null;

    private static bool FanNonTelemetryStateEquals(
        FanControlState expected,
        FanControlState actual) =>
        expected.Zone1Mode.PerformanceMode == actual.Zone1Mode.PerformanceMode &&
        expected.Zone2Mode.PerformanceMode == actual.Zone2Mode.PerformanceMode &&
        expected.Zone1Mode.FanMode == actual.Zone1Mode.FanMode &&
        expected.Zone2Mode.FanMode == actual.Zone2Mode.FanMode &&
        expected.CpuPerformanceLevel == actual.CpuPerformanceLevel &&
        expected.GpuPerformanceLevel == actual.GpuPerformanceLevel;

    private sealed record FanApplyAttempt(
        FanControlPlan Plan,
        IReadOnlyList<FanControlOperationResult> Operations,
        FanControlState? FinalState,
        IReadOnlyList<RazerExchangeTrace> ObservationExchanges,
        FanControlVerification Verification,
        bool ManualMayHaveBeenEntered)
    {
        internal bool Succeeded => Verification.Succeeded;

        internal static FanApplyAttempt Failed(
            FanControlPlan plan,
            IReadOnlyList<FanControlOperationResult> operations,
            string message,
            bool manualMayHaveBeenEntered) => new(
                plan,
                operations,
                null,
                [],
                new FanControlVerification(false, message),
                manualMayHaveBeenEntered);
    }

    private sealed record FanObservationResult(
        FanControlState FinalState,
        IReadOnlyList<RazerExchangeTrace> Exchanges,
        FanControlVerification Verification)
    {
        internal static FanObservationResult Success(
            FanControlState state,
            IReadOnlyList<RazerExchangeTrace> exchanges) => new(
                state,
                exchanges,
                new FanControlVerification(
                    true,
                    "Balanced + Manual and both fixed RPM targets verified."));

        internal static FanObservationResult Failure(
            FanControlState state,
            IReadOnlyList<RazerExchangeTrace> exchanges,
            string message) => new(
                state,
                exchanges,
                new FanControlVerification(false, message));
    }

    private sealed record ManualEntryAttempt(
        FanControlSelfTestStageResult Stage,
        bool ManualMayHaveBeenEntered);
}
