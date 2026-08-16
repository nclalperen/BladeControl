using BladeControl.Razer;

namespace BladeControl.Thermal;

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
}

public sealed record ThermalControlOperationResult(
    bool Succeeded,
    bool AnyWriteAttempted,
    bool AutoRecoveryAttempted,
    bool AutoActive,
    string Message,
    ThermalMachineState? FinalState,
    IReadOnlyList<RazerExchangeTrace> Exchanges);

public interface IThermalControlDevice
{
    ThermalMachineState CaptureState();

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

    public ThermalControlOperationResult SetBothFans(FanRpm target) =>
        ConvertFanApply(_client.ApplyThermalFanTarget(target));

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

    internal static PerformanceProfile CreateRestorationProfile(ThermalMachineState state)
    {
        if (!state.IsAuto)
        {
            throw new InvalidOperationException(
                "The captured state is not a consistent Auto-mode state.");
        }

        if (state.Zone1PerformanceMode == RazerPerformanceMode.Balanced)
        {
            return PerformanceProfile.Balanced;
        }

        if (state.Zone1PerformanceMode == RazerPerformanceMode.Silent)
        {
            return PerformanceProfile.Silent;
        }

        if (state.Zone1PerformanceMode == RazerPerformanceMode.Custom &&
            (state.CpuLevel == RazerCpuPerformanceLevel.Low ||
             state.CpuLevel == RazerCpuPerformanceLevel.Medium) &&
            state.GpuLevel == RazerGpuPerformanceLevel.Low)
        {
            return PerformanceProfile.Custom(state.CpuLevel, state.GpuLevel);
        }

        throw new InvalidOperationException(
            "The captured performance state is outside the hardware-validated restoration policy.");
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
