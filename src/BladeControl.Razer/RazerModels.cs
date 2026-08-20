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

public readonly struct RazerPerformanceMode : IEquatable<RazerPerformanceMode>
{
    private readonly byte _value;

    internal RazerPerformanceMode(byte value)
    {
        _value = value;
    }

    public static RazerPerformanceMode Balanced => new(0x00);

    public static RazerPerformanceMode Custom => new(0x04);

    public static RazerPerformanceMode Silent => new(0x05);

    internal byte Value => _value;

    /// <summary>True when this is a performance mode this build models.</summary>
    /// <remarks>
    /// The controller can report a byte outside the modelled set. Callers that preserve the
    /// user's performance mode need to distinguish "the machine is in Silent" from "the machine
    /// reported something this build does not recognise", because the second is not a mode that
    /// can be written back.
    /// </remarks>
    public bool IsKnown => _value is 0x00 or 0x04 or 0x05;

    public override string ToString()
    {
        return _value switch
        {
            0x00 => "Balanced",
            0x04 => "Custom",
            0x05 => "Silent",
            _ => $"Unknown(0x{_value:X2})"
        };
    }

    public bool Equals(RazerPerformanceMode other) => _value == other._value;

    public override bool Equals(object? obj) =>
        obj is RazerPerformanceMode other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode();

    public static bool operator ==(RazerPerformanceMode left, RazerPerformanceMode right) =>
        left.Equals(right);

    public static bool operator !=(RazerPerformanceMode left, RazerPerformanceMode right) =>
        !left.Equals(right);
}

public readonly struct RazerFanMode : IEquatable<RazerFanMode>
{
    private readonly byte _value;

    internal RazerFanMode(byte value)
    {
        _value = value;
    }

    public static RazerFanMode Auto => new(0x00);

    public static RazerFanMode Manual => new(0x01);

    internal byte Value => _value;

    public override string ToString()
    {
        return _value switch
        {
            0x00 => "Auto",
            0x01 => "Manual",
            _ => $"Unknown(0x{_value:X2})"
        };
    }

    public bool Equals(RazerFanMode other) => _value == other._value;

    public override bool Equals(object? obj) =>
        obj is RazerFanMode other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode();

    public static bool operator ==(RazerFanMode left, RazerFanMode right) =>
        left.Equals(right);

    public static bool operator !=(RazerFanMode left, RazerFanMode right) =>
        !left.Equals(right);
}

public readonly struct RazerCpuPerformanceLevel : IEquatable<RazerCpuPerformanceLevel>
{
    private readonly byte _value;

    internal RazerCpuPerformanceLevel(byte value)
    {
        _value = value;
    }

    internal byte Value => _value;

    public static RazerCpuPerformanceLevel Low => new(0x00);

    public static RazerCpuPerformanceLevel Medium => new(0x01);

    public static RazerCpuPerformanceLevel High => new(0x02);

    public static RazerCpuPerformanceLevel Boost => new(0x03);

    public static RazerCpuPerformanceLevel Overclock => new(0x04);

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

    public bool Equals(RazerCpuPerformanceLevel other) => _value == other._value;

    public override bool Equals(object? obj) =>
        obj is RazerCpuPerformanceLevel other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode();

    public static bool operator ==(
        RazerCpuPerformanceLevel left,
        RazerCpuPerformanceLevel right) => left.Equals(right);

    public static bool operator !=(
        RazerCpuPerformanceLevel left,
        RazerCpuPerformanceLevel right) => !left.Equals(right);
}

public readonly struct RazerGpuPerformanceLevel : IEquatable<RazerGpuPerformanceLevel>
{
    private readonly byte _value;

    internal RazerGpuPerformanceLevel(byte value)
    {
        _value = value;
    }

    internal byte Value => _value;

    public static RazerGpuPerformanceLevel Low => new(0x00);

    public static RazerGpuPerformanceLevel Medium => new(0x01);

    public static RazerGpuPerformanceLevel High => new(0x02);

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

    public bool Equals(RazerGpuPerformanceLevel other) => _value == other._value;

    public override bool Equals(object? obj) =>
        obj is RazerGpuPerformanceLevel other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode();

    public static bool operator ==(
        RazerGpuPerformanceLevel left,
        RazerGpuPerformanceLevel right) => left.Equals(right);

    public static bool operator !=(
        RazerGpuPerformanceLevel left,
        RazerGpuPerformanceLevel right) => !left.Equals(right);
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

    public ReadOnlyMemory<byte> ResponsePacket => _responseReport.Length == 0
        ? ReadOnlyMemory<byte>.Empty
        : _responseReport.AsMemory(1);

    public bool HasResponse => _responseReport.Length != 0;
}

public sealed record RazerFanReading(
    RazerZone Zone,
    int FirmwareReportedRpm,
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
