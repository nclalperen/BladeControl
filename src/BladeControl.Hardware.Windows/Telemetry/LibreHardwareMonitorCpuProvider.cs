using BladeControl.Telemetry;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.PawnIo;

namespace BladeControl.Hardware.Windows.Telemetry;

internal sealed record LibreHardwareMonitorConfiguration(
    bool Cpu,
    bool Gpu,
    bool Motherboard,
    bool Controller,
    bool Storage,
    bool Network,
    bool Memory,
    bool Battery,
    bool Psu,
    bool PowerMonitor)
{
    internal static LibreHardwareMonitorConfiguration CpuOnly { get; } = new(
        Cpu: true,
        Gpu: false,
        Motherboard: false,
        Controller: false,
        Storage: false,
        Network: false,
        Memory: false,
        Battery: false,
        Psu: false,
        PowerMonitor: false);
}

internal sealed record CpuSensorReading(
    string HardwareType,
    string SensorType,
    string Name,
    float? Value);

internal interface ILibreHardwareMonitorBackend : IDisposable
{
    bool PawnIoInstalled { get; }

    string LibraryVersion { get; }

    LibreHardwareMonitorConfiguration Configuration { get; }

    void Open();

    IReadOnlyList<CpuSensorReading> ReadSensors();
}

internal sealed class NativeLibreHardwareMonitorBackend : ILibreHardwareMonitorBackend
{
    private Computer? _computer;

    public bool PawnIoInstalled => PawnIo.IsInstalled;

    public string LibraryVersion =>
        typeof(Computer).Assembly.GetName().Version?.ToString() ?? "unknown";

    public LibreHardwareMonitorConfiguration Configuration =>
        LibreHardwareMonitorConfiguration.CpuOnly;

    public void Open()
    {
        if (!PawnIoInstalled)
        {
            return;
        }

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = false,
            IsMotherboardEnabled = false,
            IsControllerEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
            IsMemoryEnabled = false,
            IsBatteryEnabled = false,
            IsPsuEnabled = false,
            IsPowerMonitorEnabled = false
        };
        _computer.Open();
    }

    public IReadOnlyList<CpuSensorReading> ReadSensors()
    {
        if (_computer is null)
        {
            return [];
        }

        var result = new List<CpuSensorReading>();
        foreach (IHardware hardware in _computer.Hardware.Where(
                     hardware => hardware.HardwareType == HardwareType.Cpu))
        {
            ReadHardware(hardware, result);
        }

        return result;
    }

    public void Dispose()
    {
        _computer?.Close();
        _computer = null;
    }

    private static void ReadHardware(
        IHardware hardware,
        ICollection<CpuSensorReading> result)
    {
        hardware.Update();
        foreach (ISensor sensor in hardware.Sensors)
        {
            result.Add(new CpuSensorReading(
                hardware.HardwareType.ToString(),
                sensor.SensorType.ToString(),
                sensor.Name,
                sensor.Value));
        }

        foreach (IHardware subHardware in hardware.SubHardware)
        {
            ReadHardware(subHardware, result);
        }
    }
}

internal sealed class LibreHardwareMonitorCpuProvider : IDisposable
{
    private readonly ILibreHardwareMonitorBackend _backend;
    private bool _disposed;

    private LibreHardwareMonitorCpuProvider(ILibreHardwareMonitorBackend backend)
    {
        _backend = backend;
    }

    internal bool PawnIoInstalled => _backend.PawnIoInstalled;

    internal string LibraryVersion => _backend.LibraryVersion;

    internal LibreHardwareMonitorConfiguration Configuration => _backend.Configuration;

    internal static LibreHardwareMonitorCpuProvider Open() =>
        Open(new NativeLibreHardwareMonitorBackend());

    internal static LibreHardwareMonitorCpuProvider Open(
        ILibreHardwareMonitorBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (backend.Configuration != LibreHardwareMonitorConfiguration.CpuOnly)
        {
            backend.Dispose();
            throw new InvalidOperationException(
                "LibreHardwareMonitor must be configured for CPU monitoring only.");
        }

        try
        {
            if (backend.PawnIoInstalled)
            {
                backend.Open();
            }

            return new LibreHardwareMonitorCpuProvider(backend);
        }
        catch
        {
            backend.Dispose();
            throw;
        }
    }

    internal CpuTelemetryReading Read(DateTimeOffset timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_backend.PawnIoInstalled)
        {
            return CpuTelemetryReading.PawnIoUnavailable();
        }

        IReadOnlyList<CpuSensorReading> sensors;
        try
        {
            sensors = _backend.ReadSensors();
        }
        catch (Exception exception)
        {
            return CpuTelemetryReading.ProviderFailure(timestamp, exception.Message);
        }

        TelemetryMetric<double> packageTemperature = SelectRequiredPackageTemperature(
            sensors,
            timestamp);
        return new CpuTelemetryReading(
            packageTemperature,
            SelectOptional(sensors, timestamp, "Temperature", "Core Max"),
            SelectOptional(sensors, timestamp, "Power", "CPU Package"),
            SelectOptional(sensors, timestamp, "Load", "CPU Total"),
            SelectOptionalClock(sensors, timestamp));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _backend.Dispose();
        _disposed = true;
    }

    private static TelemetryMetric<double> SelectRequiredPackageTemperature(
        IEnumerable<CpuSensorReading> sensors,
        DateTimeOffset timestamp)
    {
        CpuSensorReading[] matches = sensors.Where(sensor =>
            sensor.HardwareType.Equals("Cpu", StringComparison.Ordinal) &&
            sensor.SensorType.Equals("Temperature", StringComparison.Ordinal) &&
            sensor.Name.Equals("CPU Package", StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            return TelemetryMetric<double>.Missing(
                timestamp,
                TelemetrySources.CpuPackageTemperature,
                matches.Length == 0
                    ? "No unique HardwareType.Cpu / SensorType.Temperature / CPU Package sensor was found."
                    : $"{matches.Length} CPU Package temperature candidates were found.");
        }

        if (!matches[0].Value.HasValue)
        {
            return TelemetryMetric<double>.Missing(
                timestamp,
                TelemetrySources.CpuPackageTemperature,
                "The unique CPU Package sensor returned no value.");
        }

        return TelemetryMetric<double>.Available(
            matches[0].Value.GetValueOrDefault(),
            timestamp,
            TelemetrySources.CpuPackageTemperature);
    }

    private static TelemetryMetric<double> SelectOptional(
        IEnumerable<CpuSensorReading> sensors,
        DateTimeOffset timestamp,
        string sensorType,
        string name)
    {
        CpuSensorReading[] matches = sensors.Where(sensor =>
            sensor.HardwareType.Equals("Cpu", StringComparison.Ordinal) &&
            sensor.SensorType.Equals(sensorType, StringComparison.Ordinal) &&
            sensor.Name.Equals(name, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || !matches[0].Value.HasValue)
        {
            return TelemetryMetric<double>.Unsupported(
                TelemetrySources.CpuOptional,
                matches.Length > 1
                    ? $"Multiple {name} candidates were found."
                    : $"{name} is unavailable.");
        }

        return TelemetryMetric<double>.Available(
            matches[0].Value.GetValueOrDefault(),
            timestamp,
            TelemetrySources.CpuOptional);
    }

    private static TelemetryMetric<double> SelectOptionalClock(
        IEnumerable<CpuSensorReading> sensors,
        DateTimeOffset timestamp)
    {
        string[] acceptedNames = ["CPU Core Average", "CPU Package"];
        CpuSensorReading[] matches = sensors.Where(sensor =>
            sensor.HardwareType.Equals("Cpu", StringComparison.Ordinal) &&
            sensor.SensorType.Equals("Clock", StringComparison.Ordinal) &&
            acceptedNames.Contains(sensor.Name, StringComparer.Ordinal)).ToArray();
        return matches.Length == 1 && matches[0].Value.HasValue
            ? TelemetryMetric<double>.Available(
                matches[0].Value.GetValueOrDefault(),
                timestamp,
                TelemetrySources.CpuOptional)
            : TelemetryMetric<double>.Unsupported(
                TelemetrySources.CpuOptional,
                "CPU package/effective clock is unavailable or ambiguous.");
    }
}

internal sealed record CpuTelemetryReading(
    TelemetryMetric<double> PackageTemperatureCelsius,
    TelemetryMetric<double> CoreMaxTemperatureCelsius,
    TelemetryMetric<double> PackagePowerWatts,
    TelemetryMetric<double> TotalLoadPercent,
    TelemetryMetric<double> ClockMegahertz)
{
    internal static CpuTelemetryReading PawnIoUnavailable() => new(
        TelemetryMetric<double>.Unsupported(
            TelemetrySources.CpuPackageTemperature,
            "PawnIO is not installed; CPU Package temperature is unavailable."),
        TelemetryMetric<double>.Unsupported(TelemetrySources.CpuOptional),
        TelemetryMetric<double>.Unsupported(TelemetrySources.CpuOptional),
        TelemetryMetric<double>.Unsupported(TelemetrySources.CpuOptional),
        TelemetryMetric<double>.Unsupported(TelemetrySources.CpuOptional));

    internal static CpuTelemetryReading ProviderFailure(
        DateTimeOffset timestamp,
        string diagnostic) => new(
        TelemetryMetric<double>.Invalid(
            null,
            timestamp,
            TelemetrySources.CpuPackageTemperature,
            $"LibreHardwareMonitor failed: {diagnostic}"),
        TelemetryMetric<double>.Invalid(null, timestamp, TelemetrySources.CpuOptional, diagnostic),
        TelemetryMetric<double>.Invalid(null, timestamp, TelemetrySources.CpuOptional, diagnostic),
        TelemetryMetric<double>.Invalid(null, timestamp, TelemetrySources.CpuOptional, diagnostic),
        TelemetryMetric<double>.Invalid(null, timestamp, TelemetrySources.CpuOptional, diagnostic));
}
