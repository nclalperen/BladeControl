namespace BladeControl.Razer;

public sealed partial class RazerClient
{
    internal FanControlApplyResult ApplyThermalFanTarget(FanRpm target)
    {
        if (!target.IsValid || target.Value < 3000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "Thermal Control V1 fan targets must be 3000..5000 RPM in 100-RPM increments.");
        }

        FanControlState initialState = GetFanControlState();
        if (!initialState.IsBalancedManual)
        {
            throw new FanControlStateException(
                "A dynamic thermal target requires verified Balanced + Manual state. No SET was sent.");
        }

        FanControlProfile profile = FanControlProfile.Fixed(target, target);
        FanControlOperation[] operations =
        [
            new(FanControlOperationKind.SetFan1Rpm, target),
            new(FanControlOperationKind.SetFan2Rpm, target)
        ];
        var plan = new FanControlPlan(operations);
        var results = new List<FanControlOperationResult>(operations.Length);
        foreach (FanControlOperation operation in operations)
        {
            try
            {
                RazerExchangeTrace exchange = ExecuteFanOperation(operation);
                results.Add(new FanControlOperationResult(operation, true, exchange, null));
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                results.Add(new FanControlOperationResult(
                    operation,
                    false,
                    LastExchange(exception),
                    exception.Message));
                FanAutoRecoveryResult recovery = AttemptEmergencyAuto();
                return new FanControlApplyResult(
                    initialState,
                    profile,
                    plan,
                    results,
                    null,
                    [],
                    new FanControlVerification(
                        false,
                        $"{operation.Description} failed: {exception.Message}"),
                    recovery.Succeeded
                        ? FanControlApplyOutcome.AutoRestored
                        : FanControlApplyOutcome.AutoRestorationFailed,
                    recovery);
            }
        }

        FanControlState? observedState;
        IReadOnlyList<RazerExchangeTrace> observationExchanges;
        FanControlVerification verification;
        try
        {
            observedState = GetFanControlState();
            observationExchanges = observedState.InitialExchanges;
            bool exact = observedState.IsBalancedManual &&
                observedState.Fan1.FirmwareReportedRpm == target.Value &&
                observedState.Fan2.FirmwareReportedRpm == target.Value;
            verification = new FanControlVerification(
                exact,
                exact
                    ? $"Firmware reported the exact {target.Value} RPM target for both fans."
                    : $"Exact thermal target validation failed: expected {target.Value}/{target.Value}, " +
                      $"received {observedState.Fan1.FirmwareReportedRpm}/" +
                      $"{observedState.Fan2.FirmwareReportedRpm}.");
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            observedState = null;
            observationExchanges = exception is RazerProtocolException protocol
                ? protocol.Exchanges
                : [];
            verification = new FanControlVerification(
                false,
                $"Thermal target readback failed: {exception.Message}");
        }

        if (verification.Succeeded)
        {
            return new FanControlApplyResult(
                initialState,
                profile,
                plan,
                results,
                observedState,
                observationExchanges,
                verification,
                FanControlApplyOutcome.Applied,
                autoRecovery: null);
        }

        FanAutoRecoveryResult autoRecovery = AttemptEmergencyAuto();
        return new FanControlApplyResult(
            initialState,
            profile,
            plan,
            results,
            observedState,
            observationExchanges,
            verification,
            autoRecovery.Succeeded
                ? FanControlApplyOutcome.AutoRestored
                : FanControlApplyOutcome.AutoRestorationFailed,
            autoRecovery);
    }
}
