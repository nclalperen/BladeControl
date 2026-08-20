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

    /// <summary>Both zones agree and hold Manual, in a performance mode we can name.</summary>
    /// <remarks>
    /// This is what an orphaned session looks like: fans held in Manual with nothing driving
    /// them. It is not specific to Balanced, because a session runs in whatever mode the user
    /// chose and leaves it there — a crash in Silent strands the fans exactly as thoroughly.
    /// </remarks>
    public bool IsOwnedManual => ZonesAgree &&
        Zone1FanMode == RazerFanMode.Manual &&
        Zone1PerformanceMode.IsKnown;

    public bool IsBalancedManual => IsOwnedManual &&
        Zone1PerformanceMode == RazerPerformanceMode.Balanced;

    public bool IsAuto => ZonesAgree && Zone1FanMode == RazerFanMode.Auto;

    public bool IsKnownAuto => IsAuto && Zone1PerformanceMode.IsKnown;

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

    public ThermalFanModeObservation ReadFanModeObservation() =>
        _thermal.ReadFanModeObservation();

    public ThermalControlOperationResult EnterManualBaseline(FanRpm baseline) =>
        _thermal.EnterManualBaseline(baseline);

    public ThermalControlOperationResult SetBothFans(FanRpm target) =>
        _thermal.SetBothFans(target);

    public ThermalControlOperationResult ReturnToFirmwareAuto() =>
        _thermal.ReturnToFirmwareAuto();

    public ThermalControlOperationResult RestorePerformance(ThermalMachineState originalState) =>
        _thermal.RestorePerformance(originalState);

    public PerformanceState GetPerformanceState() => _client.GetPerformanceState();

    public PerformanceApplyResult ApplyPerformanceProfile(PerformanceProfile profile) =>
        _client.ApplyPerformanceProfile(profile);

    public FanControlState GetFanState() => _client.GetFanControlState();

    public FanControlApplyResult ApplyFanProfile(FanControlProfile profile) =>
        _client.ApplyFanControlProfile(profile);
}
