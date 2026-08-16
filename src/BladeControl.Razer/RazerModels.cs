namespace BladeControl.Razer;

public enum RazerZone : byte
{
    Zone1 = 0x01,
    Zone2 = 0x02
}

internal enum RazerPerformanceCluster : byte
{
    Cpu = 0x01,
    Gpu = 0x02
}

public readonly record struct RazerPerformanceMode(byte RawValue)
{
    public override string ToString()
    {
        return RawValue switch
        {
            0x00 => "Balanced",
            0x04 => "Custom",
            0x05 => "Silent",
            _ => $"Unknown(0x{RawValue:X2})"
        };
    }
}

public readonly record struct RazerFanMode(byte RawValue)
{
    public override string ToString()
    {
        return RawValue switch
        {
            0x00 => "Auto",
            0x01 => "Manual",
            _ => $"Unknown(0x{RawValue:X2})"
        };
    }
}

public readonly struct RazerCpuPerformanceLevel
{
    private readonly byte _value;

    internal RazerCpuPerformanceLevel(byte value)
    {
        _value = value;
    }

    public override string ToString()
    {
        return _value switch
        {
            0x00 => "Low",
            0x01 => "Medium",
            0x02 => "High",
            0x03 => "Boost",
            0x04 => "Overclock",
            _ => $"Unknown(0x{_value:X2})"
        };
    }
}

public readonly struct RazerGpuPerformanceLevel
{
    private readonly byte _value;

    internal RazerGpuPerformanceLevel(byte value)
    {
        _value = value;
    }

    public override string ToString()
    {
        return _value switch
        {
            0x00 => "Low",
            0x01 => "Medium",
            0x02 => "High",
            _ => $"Unknown(0x{_value:X2})"
        };
    }
}

public sealed record RazerDeviceInfo(
    string DevicePath,
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    ushort FeatureReportByteLength);

public sealed class RazerExchangeTrace
{
    private readonly byte[] _requestReport;
    private readonly byte[] _responseReport;

    internal RazerExchangeTrace(
        byte transactionId,
        byte commandClass,
        byte commandId,
        ReadOnlySpan<byte> requestReport,
        ReadOnlySpan<byte> responseReport)
    {
        TransactionId = transactionId;
        CommandClass = commandClass;
        CommandId = commandId;
        _requestReport = requestReport.ToArray();
        _responseReport = responseReport.ToArray();
    }

    public byte TransactionId { get; }

    public byte CommandClass { get; }

    public byte CommandId { get; }

    public ushort CombinedCommand => (ushort)((CommandClass << 8) | CommandId);

    public ReadOnlyMemory<byte> RequestReport => _requestReport;

    public ReadOnlyMemory<byte> ResponseReport => _responseReport;

    public ReadOnlyMemory<byte> RequestPacket => _requestReport.AsMemory(1);

    public ReadOnlyMemory<byte> ResponsePacket => _responseReport.AsMemory(1);
}

public sealed record RazerFanReading(
    RazerZone Zone,
    int RevolutionsPerMinute,
    RazerExchangeTrace Exchange);

public sealed record RazerModeReading(
    RazerZone Zone,
    RazerPerformanceMode PerformanceMode,
    RazerFanMode FanMode,
    RazerExchangeTrace Exchange);

public sealed class RazerStatusSnapshot
{
    internal RazerStatusSnapshot(
        RazerDeviceInfo device,
        RazerFanReading fan1,
        RazerFanReading fan2,
        RazerModeReading zone1Mode,
        RazerModeReading zone2Mode,
        RazerCpuPerformanceLevel cpuPerformanceLevel,
        RazerGpuPerformanceLevel gpuPerformanceLevel,
        RazerExchangeTrace cpuPerformanceExchange,
        RazerExchangeTrace gpuPerformanceExchange)
    {
        Device = device;
        Fan1 = fan1;
        Fan2 = fan2;
        Zone1Mode = zone1Mode;
        Zone2Mode = zone2Mode;
        CpuPerformanceLevel = cpuPerformanceLevel;
        GpuPerformanceLevel = gpuPerformanceLevel;
        Exchanges =
        [
            fan1.Exchange,
            fan2.Exchange,
            zone1Mode.Exchange,
            zone2Mode.Exchange,
            cpuPerformanceExchange,
            gpuPerformanceExchange
        ];
    }

    public RazerDeviceInfo Device { get; }

    public RazerFanReading Fan1 { get; }

    public RazerFanReading Fan2 { get; }

    public RazerModeReading Zone1Mode { get; }

    public RazerModeReading Zone2Mode { get; }

    public RazerPerformanceMode PerformanceMode => Zone1Mode.PerformanceMode;

    public RazerFanMode FanMode => Zone1Mode.FanMode;

    public RazerCpuPerformanceLevel CpuPerformanceLevel { get; }

    public RazerGpuPerformanceLevel GpuPerformanceLevel { get; }

    public IReadOnlyList<RazerExchangeTrace> Exchanges { get; }
}
