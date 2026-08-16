using BladeControl.Razer;
using BladeControl.Thermal;

namespace BladeControl.Runtime;

public sealed record RuntimeRazerModeState(
    RazerPerformanceMode Zone1PerformanceMode,
    RazerFanMode Zone1FanMode,
    RazerPerformanceMode Zone2PerformanceMode,
    RazerFanMode Zone2FanMode,
    IReadOnlyList<RazerExchangeTrace> Exchanges)
{
    public bool ZonesAgree =>
        Zone1PerformanceMode == Zone2PerformanceMode &&
        Zone1FanMode == Zone2FanMode;

    public bool IsBalancedManual => ZonesAgree &&
        Zone1PerformanceMode == RazerPerformanceMode.Balanced &&
        Zone1FanMode == RazerFanMode.Manual;

    public bool IsAuto => ZonesAgree && Zone1FanMode == RazerFanMode.Auto;

    public bool IsKnownAuto => IsAuto &&
        (Zone1PerformanceMode == RazerPerformanceMode.Balanced ||
         Zone1PerformanceMode == RazerPerformanceMode.Custom ||
         Zone1PerformanceMode == RazerPerformanceMode.Silent);

    public override string ToString() =>
        $"Zone 1 {Zone1PerformanceMode} + {Zone1FanMode}; " +
        $"Zone 2 {Zone2PerformanceMode} + {Zone2FanMode}";
}

public interface IRuntimeHardwareController : IThermalControlDevice
{
    event Action<RazerExchangeTrace>? ExchangeCompleted;

    RuntimeRazerModeState ReadModeState();

    PerformanceState GetPerformanceState();

    PerformanceApplyResult ApplyPerformanceProfile(PerformanceProfile profile);

    FanControlState GetFanState();

    FanControlApplyResult ApplyFanProfile(FanControlProfile profile);
}

public sealed class RazerRuntimeHardwareController : IRuntimeHardwareController
{
    private readonly RazerClient _client;
    private readonly RazerThermalControlDevice _thermal;

    public RazerRuntimeHardwareController(RazerClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _thermal = new RazerThermalControlDevice(client);
    }

    public event Action<RazerExchangeTrace>? ExchangeCompleted
    {
        add => _client.ExchangeCompleted += value;
        remove => _client.ExchangeCompleted -= value;
    }

    public RuntimeRazerModeState ReadModeState()
    {
        RazerModeReading zone1 = _client.GetPerformanceAndFanMode(RazerZone.Zone1);
        RazerModeReading zone2 = _client.GetPerformanceAndFanMode(RazerZone.Zone2);
        return new RuntimeRazerModeState(
            zone1.PerformanceMode,
            zone1.FanMode,
            zone2.PerformanceMode,
            zone2.FanMode,
            [zone1.Exchange, zone2.Exchange]);
    }

    public ThermalMachineState CaptureState() => _thermal.CaptureState();

    public ThermalControlOperationResult EnterManualBaseline(FanRpm baseline) =>
        _thermal.EnterManualBaseline(baseline);

    public ThermalControlOperationResult SetBothFans(FanRpm target) =>
        _thermal.SetBothFans(target);

    public ThermalControlOperationResult ReturnToBalancedAuto() =>
        _thermal.ReturnToBalancedAuto();

    public ThermalControlOperationResult RestorePerformance(ThermalMachineState originalState) =>
        _thermal.RestorePerformance(originalState);

    public PerformanceState GetPerformanceState() => _client.GetPerformanceState();

    public PerformanceApplyResult ApplyPerformanceProfile(PerformanceProfile profile) =>
        _client.ApplyPerformanceProfile(profile);

    public FanControlState GetFanState() => _client.GetFanControlState();

    public FanControlApplyResult ApplyFanProfile(FanControlProfile profile) =>
        _client.ApplyFanControlProfile(profile);
}
