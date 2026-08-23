using BladeControl.Razer.Protocol;

namespace BladeControl.Razer;

public sealed partial class RazerClient
{
    public PerformanceState GetPerformanceState() =>
        new(ReadCompleteStatus(requireZoneAgreement: false));

    public PerformanceApplyResult ApplyPerformanceProfile(
        PerformanceProfile profile)
    {
        return ApplyPerformanceProfile(profile, restorationOverride: null);
    }

    public PerformanceSelfTestResult RunPerformanceSelfTest()
    {
        PerformanceState initialState = GetPerformanceState();
        ValidateSelfTestInitialState(initialState);
        PerformanceProfile restorationTarget = PerformanceProfile.Custom(
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        (string Stage, PerformanceProfile Profile)[] sequence =
        [
            ("B - Custom Low/Low", PerformanceProfile.Custom(
                RazerCpuPerformanceLevel.Low,
                RazerGpuPerformanceLevel.Low)),
            ("C - Balanced", PerformanceProfile.Balanced),
            ("D - Silent", PerformanceProfile.Silent),
            ("E - Restore Custom Medium/Low", restorationTarget)
        ];
        var stages = new List<PerformanceSelfTestStageResult>(sequence.Length);
        PerformanceState lastVerifiedState = initialState;
        bool stateChangedFromInitial = false;

        foreach ((string stage, PerformanceProfile target) in sequence)
        {
            PerformanceApplyResult apply;
            try
            {
                apply = ApplyPerformanceProfile(target, restorationTarget);
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                PerformanceRestorationResult? restoration = stateChangedFromInitial
                    ? AttemptRestoration(restorationTarget)
                    : null;
                apply = new PerformanceApplyResult(
                    lastVerifiedState,
                    target,
                    new PerformanceApplyPlan([]),
                    [],
                    null,
                    new PerformanceVerificationResult(false, exception.Message),
                    restoration is null
                        ? PerformanceApplyOutcome.Failed
                        : restoration.Succeeded
                            ? PerformanceApplyOutcome.Restored
                            : PerformanceApplyOutcome.RestorationFailed,
                    restoration);
            }

            stages.Add(new PerformanceSelfTestStageResult(stage, target, apply));
            if (!apply.Succeeded)
            {
                return new PerformanceSelfTestResult(
                    initialState,
                    stages,
                    false,
                    $"Selftest failed at stage {stage}. " +
                    (apply.Restoration?.Message ??
                     "No restoration was possible because no state-changing write completed."));
            }

            stateChangedFromInitial |= !apply.Plan.IsNoOp;
            lastVerifiedState = apply.FinalState!;
        }

        PerformanceState finalState = stages[^1].ApplyResult.FinalState!;
        bool exactRestore = NonTelemetryStateEquals(initialState, finalState);
        return new PerformanceSelfTestResult(
            initialState,
            stages,
            exactRestore,
            exactRestore
                ? "PASS - all stages succeeded and the initial non-telemetry state was restored."
                : "RESTORATION FAILED: final non-telemetry state differs from the captured initial state.");
    }

    private PerformanceApplyResult ApplyPerformanceProfile(
        PerformanceProfile profile,
        PerformanceProfile? restorationOverride)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProfilePolicy(profile);

        PerformanceState initialState = GetPerformanceState();
        ValidatePerformanceState(initialState);
        ApplyAttempt attempt = ExecutePerformanceApply(initialState, profile);

        if (attempt.Succeeded)
        {
            return CreateSuccessfulApplyResult(
                initialState,
                profile,
                attempt);
        }

        PerformanceRestorationResult? restoration = null;
        PerformanceApplyOutcome outcome = PerformanceApplyOutcome.Failed;
        PerformanceProfile? restoreProfile = restorationOverride;
        bool mayRestore = restoreProfile is not null ||
            TryCreateRestorationProfile(initialState, out restoreProfile);
        if (attempt.AnyWriteAttempted && mayRestore)
        {
            restoration = AttemptRestoration(restoreProfile!);
            outcome = restoration.Succeeded
                ? PerformanceApplyOutcome.Restored
                : PerformanceApplyOutcome.RestorationFailed;
        }

        return new PerformanceApplyResult(
            initialState,
            profile,
            attempt.Plan,
            attempt.Operations,
            attempt.FinalState,
            attempt.Verification,
            outcome,
            restoration);
    }

    private static void ValidateSelfTestInitialState(PerformanceState state)
    {
        bool exact = state.ZonesAgree &&
            state.Zone1Mode.PerformanceMode == RazerPerformanceMode.Custom &&
            state.Zone1Mode.FanMode == RazerFanMode.Auto &&
            state.CpuPerformanceLevel == RazerCpuPerformanceLevel.Medium &&
            state.GpuPerformanceLevel == RazerGpuPerformanceLevel.Low;
        if (!exact)
        {
            throw new PerformanceSelfTestPreconditionException(
                "Performance selftest requires initial state Custom + Auto, " +
                "CPU Medium, GPU Low. No SET command was sent.",
                state);
        }
    }

    private static bool NonTelemetryStateEquals(
        PerformanceState expected,
        PerformanceState actual) =>
        expected.Zone1Mode.PerformanceMode == actual.Zone1Mode.PerformanceMode &&
        expected.Zone2Mode.PerformanceMode == actual.Zone2Mode.PerformanceMode &&
        expected.Zone1Mode.FanMode == actual.Zone1Mode.FanMode &&
        expected.Zone2Mode.FanMode == actual.Zone2Mode.FanMode &&
        expected.CpuPerformanceLevel == actual.CpuPerformanceLevel &&
        expected.GpuPerformanceLevel == actual.GpuPerformanceLevel;

    private ApplyAttempt ExecutePerformanceApply(
        PerformanceState currentState,
        PerformanceProfile profile)
    {
        PerformanceApplyPlan plan = BuildPerformanceApplyPlan(currentState, profile);
        var results = new List<PerformanceOperationResult>(plan.Operations.Count);
        bool anyWriteAttempted = false;

        foreach (PerformanceApplyOperation operation in plan.Operations)
        {
            anyWriteAttempted = true;
            try
            {
                RazerExchangeTrace exchange = ExecutePerformanceOperation(operation);
                results.Add(new PerformanceOperationResult(
                    operation,
                    true,
                    exchange,
                    null));
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                RazerExchangeTrace? exchange = exception is RazerProtocolException protocol &&
                    protocol.Exchanges.Count > 0
                        ? protocol.Exchanges[^1]
                        : null;
                results.Add(new PerformanceOperationResult(
                    operation,
                    false,
                    exchange,
                    exception.Message));
                return new ApplyAttempt(
                    plan,
                    results,
                    null,
                    new PerformanceVerificationResult(
                        false,
                        $"{operation.Description} failed: {exception.Message}"),
                    anyWriteAttempted);
            }
        }

        PerformanceState finalState;
        try
        {
            finalState = GetPerformanceState();
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            return new ApplyAttempt(
                plan,
                results,
                null,
                new PerformanceVerificationResult(
                    false,
                    $"Post-apply state read failed: {exception.Message}"),
                anyWriteAttempted);
        }

        PerformanceVerificationResult verification =
            VerifyPerformanceProfile(finalState, profile, currentState.Zone1Mode.FanMode);
        return new ApplyAttempt(
            plan,
            results,
            finalState,
            verification,
            anyWriteAttempted);
    }

    private static PerformanceApplyResult CreateSuccessfulApplyResult(
        PerformanceState initialState,
        PerformanceProfile profile,
        ApplyAttempt attempt)
    {
        PerformanceApplyOutcome outcome = attempt.Plan.IsNoOp
            ? PerformanceApplyOutcome.NoChangesRequired
            : PerformanceApplyOutcome.Applied;
        return new PerformanceApplyResult(
            initialState,
            profile,
            attempt.Plan,
            attempt.Operations,
            attempt.FinalState,
            attempt.Verification,
            outcome,
            restoration: null);
    }

    private PerformanceRestorationResult AttemptRestoration(
        PerformanceProfile restoreProfile)
    {
        PerformanceState currentState;
        try
        {
            currentState = GetPerformanceState();
            ValidateRestorationState(currentState);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            return new PerformanceRestorationResult(
                false,
                null,
                [],
                null,
                $"RESTORATION FAILED: state could not be read: {exception.Message}");
        }

        ApplyAttempt restoration = ExecutePerformanceApply(currentState, restoreProfile);
        PerformanceState? finalState = restoration.FinalState;
        if (!restoration.Succeeded && finalState is null)
        {
            try
            {
                finalState = GetPerformanceState();
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                return new PerformanceRestorationResult(
                    false,
                    restoration.Plan,
                    restoration.Operations,
                    null,
                    $"RESTORATION FAILED: {restoration.Verification.Message}; " +
                    $"final state read also failed: {exception.Message}");
            }
        }

        return new PerformanceRestorationResult(
            restoration.Succeeded,
            restoration.Plan,
            restoration.Operations,
            finalState,
            restoration.Succeeded
                ? "RESTORED"
                : $"RESTORATION FAILED: {restoration.Verification.Message}");
    }

    private static PerformanceApplyPlan BuildPerformanceApplyPlan(
        PerformanceState currentState,
        PerformanceProfile profile)
    {
        var operations = new List<PerformanceApplyOperation>(capacity: 4);

        // The fan mode currently in force, carried through unchanged. Performance and cooling
        // are independent: which power ceiling the machine runs at says nothing about who
        // drives the fans, and a user who set a fixed fan or started Dynamic did not ask for
        // that to end because they changed performance mode.
        RazerFanMode fanMode = currentState.Zone1Mode.FanMode;

        bool modeMatches =
            currentState.Zone1Mode.PerformanceMode == profile.PerformanceMode &&
            currentState.Zone2Mode.PerformanceMode == profile.PerformanceMode;

        if (!modeMatches)
        {
            operations.Add(new PerformanceApplyOperation(
                PerformanceApplyOperationKind.SetModeZone1,
                performanceMode: profile.PerformanceMode,
                fanMode: fanMode));
            operations.Add(new PerformanceApplyOperation(
                PerformanceApplyOperationKind.SetModeZone2,
                performanceMode: profile.PerformanceMode,
                fanMode: fanMode));
        }

        if (profile.IsCustom)
        {
            RazerCpuPerformanceLevel cpuLevel = profile.CpuLevel!.Value;
            RazerGpuPerformanceLevel gpuLevel = profile.GpuLevel!.Value;
            if (currentState.CpuPerformanceLevel != cpuLevel)
            {
                operations.Add(new PerformanceApplyOperation(
                    PerformanceApplyOperationKind.SetCpuLevel,
                    cpuLevel: cpuLevel));
            }

            if (currentState.GpuPerformanceLevel != gpuLevel)
            {
                operations.Add(new PerformanceApplyOperation(
                    PerformanceApplyOperationKind.SetGpuLevel,
                    gpuLevel: gpuLevel));
            }
        }

        return new PerformanceApplyPlan(operations);
    }

    private RazerExchangeTrace ExecutePerformanceOperation(
        PerformanceApplyOperation operation)
    {
        return operation.Kind switch
        {
            PerformanceApplyOperationKind.SetModeZone1 =>
                WritePerformanceAndFanMode(
                    RazerZone.Zone1,
                    operation.PerformanceMode!.Value,
                    operation.FanMode!.Value),
            PerformanceApplyOperationKind.SetModeZone2 =>
                WritePerformanceAndFanMode(
                    RazerZone.Zone2,
                    operation.PerformanceMode!.Value,
                    operation.FanMode!.Value),
            PerformanceApplyOperationKind.SetCpuLevel =>
                WriteCpuPerformanceLevel(operation.CpuLevel!.Value),
            PerformanceApplyOperationKind.SetGpuLevel =>
                WriteGpuPerformanceLevel(operation.GpuLevel!.Value),
            _ => throw new InvalidOperationException(
                $"Unsupported performance operation {operation.Kind}.")
        };
    }

    private RazerExchangeTrace WritePerformanceAndFanMode(
        RazerZone zone,
        RazerPerformanceMode mode,
        RazerFanMode fanMode)
    {
        byte transactionId = _transactionIds.NextTransactionId();
        RazerPacket request = RazerCommands.CreateSetPerformanceAndFanMode(
            transactionId,
            zone,
            mode,
            fanMode);
        return ExchangeWriteAndValidateEcho(
            request,
            (byte)zone,
            "zone",
            minimumResponseDataSize: 4);
    }

    private RazerExchangeTrace WriteCpuPerformanceLevel(
        RazerCpuPerformanceLevel level)
    {
        byte transactionId = _transactionIds.NextTransactionId();
        RazerPacket request = RazerCommands.CreateSetCpuPerformanceLevel(
            transactionId,
            level);
        return ExchangeWriteAndValidateEcho(
            request,
            (byte)RazerPerformanceCluster.Cpu,
            "cluster",
            minimumResponseDataSize: 3);
    }

    private RazerExchangeTrace WriteGpuPerformanceLevel(
        RazerGpuPerformanceLevel level)
    {
        byte transactionId = _transactionIds.NextTransactionId();
        RazerPacket request = RazerCommands.CreateSetGpuPerformanceLevel(
            transactionId,
            level);
        return ExchangeWriteAndValidateEcho(
            request,
            (byte)RazerPerformanceCluster.Gpu,
            "cluster",
            minimumResponseDataSize: 3);
    }

    private RazerExchangeTrace ExchangeWriteAndValidateEcho(
        RazerPacket request,
        byte expectedSelector,
        string selectorName,
        byte minimumResponseDataSize)
    {
        (RazerPacket response, RazerExchangeTrace exchange) = ExchangeAndValidate(
            request,
            expectedSelector,
            selectorName,
            minimumResponseDataSize);

        ReadOnlySpan<byte> expectedArguments =
            request.Arguments[..request.DataSize];
        ReadOnlySpan<byte> actualArguments =
            response.Arguments[..request.DataSize];
        if (!actualArguments.SequenceEqual(expectedArguments))
        {
            throw ValidationFailure(
                request,
                exchange,
                "response argument echo",
                FormatHex(expectedArguments),
                FormatHex(actualArguments));
        }

        return exchange;
    }

    /// <param name="expectedFanMode">
    /// The fan mode that was in force before the write and must still be after it. Verifying
    /// against Auto would have passed a write that quietly took the fans from the user.
    /// </param>
    private static PerformanceVerificationResult VerifyPerformanceProfile(
        PerformanceState state,
        PerformanceProfile profile,
        RazerFanMode expectedFanMode)
    {
        if (!state.ZonesAgree)
        {
            return new PerformanceVerificationResult(
                false,
                "Verification mismatch: performance or fan mode differs between zones.");
        }

        if (state.Zone1Mode.PerformanceMode != profile.PerformanceMode ||
            state.Zone1Mode.FanMode != expectedFanMode)
        {
            return new PerformanceVerificationResult(
                false,
                $"Verification mismatch: expected {profile.PerformanceMode} + " +
                $"{expectedFanMode}; received {state.Zone1Mode.PerformanceMode} + " +
                $"{state.Zone1Mode.FanMode}.");
        }

        if (profile.IsCustom &&
            (state.CpuPerformanceLevel != profile.CpuLevel!.Value ||
             state.GpuPerformanceLevel != profile.GpuLevel!.Value))
        {
            return new PerformanceVerificationResult(
                false,
                $"Verification mismatch: expected CPU {profile.CpuLevel} / GPU {profile.GpuLevel}; " +
                $"received CPU {state.CpuPerformanceLevel} / GPU {state.GpuPerformanceLevel}.");
        }

        return new PerformanceVerificationResult(true, "Target state verified.");
    }

    private static void ValidateProfilePolicy(PerformanceProfile profile)
    {
        if (profile.PerformanceMode != RazerPerformanceMode.Balanced &&
            profile.PerformanceMode != RazerPerformanceMode.Silent &&
            profile.PerformanceMode != RazerPerformanceMode.Custom)
        {
            throw new PerformanceCapabilityException(
                $"Performance mode '{profile.PerformanceMode}' is not supported.");
        }

        if (!profile.IsCustom)
        {
            return;
        }

        RazerCpuPerformanceLevel cpu = profile.CpuLevel!.Value;
        RazerGpuPerformanceLevel gpu = profile.GpuLevel!.Value;
        if (cpu != RazerCpuPerformanceLevel.Low &&
            cpu != RazerCpuPerformanceLevel.Medium)
        {
            throw new PerformanceCapabilityException(
                $"CPU level '{cpu}' is known by the protocol but is not yet " +
                "hardware-validated on this device.");
        }

        if (gpu != RazerGpuPerformanceLevel.Low)
        {
            throw new PerformanceCapabilityException(
                $"GPU level '{gpu}' is known by the protocol but is not yet " +
                "hardware-validated on this device.");
        }
    }

    private static void ValidatePerformanceState(PerformanceState state)
    {
        if (!state.ZonesAgree)
        {
            throw new PerformanceStateException(
                "Current performance or fan mode differs between zones. No SET command was sent.");
        }

        // Fan mode is deliberately not checked. Performance Control refused to act at all
        // while the fans were Manual, which meant a running Dynamic session or a fixed fan
        // target made the performance controls unusable — and the only way to change mode was
        // to give up cooling first. The fan mode in force is preserved through the write
        // instead.

        RazerPerformanceMode mode = state.Zone1Mode.PerformanceMode;
        if (mode != RazerPerformanceMode.Balanced &&
            mode != RazerPerformanceMode.Custom &&
            mode != RazerPerformanceMode.Silent)
        {
            throw new PerformanceStateException(
                $"Current performance mode {mode} is unknown. No SET command was sent.");
        }
    }

    private static void ValidateRestorationState(PerformanceState state)
    {
        // Fan mode is not required to be Auto. Restoration puts back the performance state, and
        // whoever owns the fans keeps owning them; demanding Auto here meant a machine under a
        // fixed target or a Dynamic session could not be restored at all.
        RazerPerformanceMode[] modes =
            [state.Zone1Mode.PerformanceMode, state.Zone2Mode.PerformanceMode];
        if (modes.Any(mode =>
                mode != RazerPerformanceMode.Balanced &&
                mode != RazerPerformanceMode.Custom &&
                mode != RazerPerformanceMode.Silent))
        {
            throw new PerformanceStateException(
                "Restoration stopped because a returned performance mode is unknown.");
        }
    }

    private static bool TryCreateRestorationProfile(
        PerformanceState state,
        out PerformanceProfile? profile)
    {
        profile = null;
        if (!state.ZonesAgree)
        {
            return false;
        }

        // Fan mode deliberately does not disqualify a restoration. This returned false whenever
        // the fans were Manual, which meant a performance apply that failed part-way during a
        // Dynamic session left the machine in whatever half-applied state it had reached and
        // attempted no recovery — the one situation where recovery matters most.
        //
        // The level check is the one that still applies, and it now matches what this build will
        // send: anything except Overclock, which cannot be written back and so cannot be
        // restored to.
        bool cpuPermitted =
            state.CpuPerformanceLevel != RazerCpuPerformanceLevel.Overclock;
        if (!cpuPermitted)
        {
            return false;
        }

        if (state.Zone1Mode.PerformanceMode == RazerPerformanceMode.Balanced)
        {
            profile = PerformanceProfile.Balanced;
            return true;
        }

        if (state.Zone1Mode.PerformanceMode == RazerPerformanceMode.Silent)
        {
            profile = PerformanceProfile.Silent;
            return true;
        }

        if (state.Zone1Mode.PerformanceMode == RazerPerformanceMode.Custom)
        {
            profile = PerformanceProfile.Custom(
                state.CpuPerformanceLevel,
                state.GpuPerformanceLevel);
            return true;
        }

        return false;
    }

    private static bool IsOperationalFailure(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    private sealed record ApplyAttempt(
        PerformanceApplyPlan Plan,
        IReadOnlyList<PerformanceOperationResult> Operations,
        PerformanceState? FinalState,
        PerformanceVerificationResult Verification,
        bool AnyWriteAttempted)
    {
        internal bool Succeeded => Verification.Succeeded;
    }
}
