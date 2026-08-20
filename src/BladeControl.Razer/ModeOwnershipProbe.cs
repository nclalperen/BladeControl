namespace BladeControl.Razer;

/// <summary>One step of the mode-ownership probe.</summary>
public sealed record ModeProbeStep(
    string Description,
    RazerPerformanceMode Zone1PerformanceMode,
    RazerFanMode Zone1FanMode,
    RazerPerformanceMode Zone2PerformanceMode,
    RazerFanMode Zone2FanMode,
    int FirmwareReportedFan1Rpm,
    int FirmwareReportedFan2Rpm)
{
    public bool ZonesAgree =>
        Zone1PerformanceMode == Zone2PerformanceMode && Zone1FanMode == Zone2FanMode;

    public override string ToString() =>
        $"{Description,-38} {Zone1PerformanceMode} + {Zone1FanMode}" +
        $"{(ZonesAgree ? string.Empty : $" / {Zone2PerformanceMode} + {Zone2FanMode}")}" +
        $"   fans {FirmwareReportedFan1Rpm}/{FirmwareReportedFan2Rpm} RPM";
}

public sealed partial class RazerClient
{
    /// <summary>
    /// Writes an arbitrary performance + fan mode pair to both zones and reads the result back.
    /// </summary>
    /// <remarks>
    /// <para>Thermal Control V1 only ever writes <c>Balanced + Manual</c>. The wire command
    /// <c>0x0D02</c> has always taken both values as parameters, so pairs such as
    /// <c>Silent + Manual</c> are expressible; they had simply never been written, which means
    /// nothing was known about whether the controller honours a manual fan target outside
    /// Balanced or lets the mode's own curve override it.</para>
    /// <para>This exists to answer that empirically. It uses the same already-validated write
    /// family and the same echo validation as every other write here — only the argument pair
    /// is new — and it reads the state back rather than assuming the write took. It performs no
    /// recovery of its own: the caller owns sequencing and restoration.</para>
    /// </remarks>
    public ModeProbeStep WriteModePairAndReadBack(
        string description,
        RazerPerformanceMode mode,
        RazerFanMode fanMode)
    {
        WritePerformanceAndFanMode(RazerZone.Zone1, mode, fanMode);
        WritePerformanceAndFanMode(RazerZone.Zone2, mode, fanMode);
        return ReadModeAndFans(description);
    }

    /// <summary>Writes a fan target to both fans without touching the mode pair.</summary>
    /// <remarks>
    /// Deliberately separate from <see cref="ApplyThermalFanTarget"/>, which requires
    /// Balanced + Manual ownership and would refuse in exactly the states under test.
    /// </remarks>
    public ModeProbeStep WriteFanTargetAndReadBack(string description, FanRpm rpm)
    {
        WriteFanRpm(RazerZone.Zone1, rpm);
        WriteFanRpm(RazerZone.Zone2, rpm);
        return ReadModeAndFans(description);
    }

    /// <summary>Reads the mode pair and the firmware-reported fan targets.</summary>
    /// <remarks>
    /// The fan figures come from <c>0x0D81</c> and are the firmware's reported fan state, not
    /// a tachometer reading. A target that holds across the mode change tells us the pair was
    /// accepted; it does not tell us the blades are spinning at that rate.
    /// </remarks>
    public ModeProbeStep ReadModeAndFans(string description)
    {
        RazerModeReading zone1 = GetPerformanceAndFanMode(RazerZone.Zone1);
        RazerModeReading zone2 = GetPerformanceAndFanMode(RazerZone.Zone2);
        FanControlState fans = GetFanControlState();
        return new ModeProbeStep(
            description,
            zone1.PerformanceMode,
            zone1.FanMode,
            zone2.PerformanceMode,
            zone2.FanMode,
            fans.Fan1.FirmwareReportedRpm,
            fans.Fan2.FirmwareReportedRpm);
    }
}
