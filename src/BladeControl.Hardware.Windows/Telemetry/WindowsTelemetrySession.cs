using BladeControl.Hardware.Windows.Telemetry.Nvml;
using BladeControl.Razer;
using BladeControl.Telemetry;

namespace BladeControl.Hardware.Windows.Telemetry;

public sealed class WindowsTelemetrySession : ITelemetryProvider
{
    private const string PinnedLibreHardwareMonitorVersion = "0.9.6";

    private readonly RazerClient? _razerClient;
    private readonly LibreHardwareMonitorCpuProvider? _cpu;
    private readonly NvmlTelemetryProvider? _gpu;
    private readonly List<string> _diagnostics;
    private readonly bool _gpuAmbiguous;
    private readonly IReadOnlyList<TelemetryGpuIdentity> _enumeratedGpus;
    private bool _disposed;

    private WindowsTelemetrySession(
        RazerClient? razerClient,
        LibreHardwareMonitorCpuProvider? cpu,
        NvmlTelemetryProvider? gpu,
        bool gpuAmbiguous,
        IReadOnlyList<TelemetryGpuIdentity> enumeratedGpus,
        IReadOnlyList<string> diagnostics)
    {
        _razerClient = razerClient;
        _cpu = cpu;
        _gpu = gpu;
        _gpuAmbiguous = gpuAmbiguous;
        _enumeratedGpus = enumeratedGpus.ToArray();
        _diagnostics = diagnostics.ToList();
        Capabilities = CreateCapabilities(null, null);
    }

    public string Name => "Windows telemetry providers";

    public TelemetryCapabilities Capabilities { get; private set; }

    public static WindowsTelemetrySession Open(
        RazerClient? razerClient = null,
        string? preferredGpuPciBusId = null)
    {
        var diagnostics = new List<string>();
        LibreHardwareMonitorCpuProvider? cpu = null;
        try
        {
            cpu = LibreHardwareMonitorCpuProvider.Open();
            if (!cpu.PawnIoInstalled)
            {
                diagnostics.Add(
                    "PawnIO is not installed. Static Razer controls remain available, " +
                    "but thermal closed-loop control is disabled.");
            }
        }
        catch (Exception exception)
        {
            diagnostics.Add($"LibreHardwareMonitor CPU provider failed to open: {exception.Message}");
        }

        _ = NvmlTelemetryProvider.TryOpen(
            preferredGpuPciBusId,
            out NvmlTelemetryProvider? gpu,
            out bool ambiguous,
            out IReadOnlyList<TelemetryGpuIdentity> gpus,
            out string nvmlDiagnostic);
        diagnostics.Add(nvmlDiagnostic);
        return new WindowsTelemetrySession(
            razerClient,
            cpu,
            gpu,
            ambiguous,
            gpus,
            diagnostics);
    }

    public TelemetrySnapshot GetSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        CpuTelemetryReading cpu = _cpu?.Read(timestamp) ??
            CpuTelemetryReading.ProviderFailure(
                timestamp,
                "The CPU telemetry provider did not initialize.");
        NvmlGpuReading gpu = _gpu?.Read(timestamp) ?? UnavailableGpuReading(timestamp);
        RazerStatusSnapshot? firmware = null;
        var warnings = new List<string>();
        if (_razerClient is not null)
        {
            try
            {
                firmware = _razerClient.GetStatus();
            }
            catch (Exception exception)
            {
                warnings.Add($"Razer firmware state read failed: {exception.Message}");
            }
        }

        Capabilities = CreateCapabilities(cpu, gpu);
        return new TelemetrySnapshot(
            timestamp,
            cpu.PackageTemperatureCelsius,
            gpu.TemperatureCelsius)
        {
            CpuCoreMaxTemperatureCelsius = cpu.CoreMaxTemperatureCelsius,
            CpuPackagePowerWatts = cpu.PackagePowerWatts,
            CpuTotalLoadPercent = cpu.TotalLoadPercent,
            CpuClockMegahertz = cpu.ClockMegahertz,
            GpuPowerWatts = gpu.PowerWatts,
            GpuUtilizationPercent = gpu.GpuUtilizationPercent,
            GpuMemoryUtilizationPercent = gpu.MemoryUtilizationPercent,
            GpuGraphicsClockMegahertz = gpu.GraphicsClockMegahertz,
            GpuMemoryClockMegahertz = gpu.MemoryClockMegahertz,
            GpuVramUsedBytes = gpu.VramUsedBytes,
            GpuVramTotalBytes = gpu.VramTotalBytes,
            AcpiThermalZonesCelsius = [],
            RazerFirmwareState = firmware,
            Warnings = warnings
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gpu?.Dispose();
        _cpu?.Dispose();
        _disposed = true;
    }

    private TelemetryCapabilities CreateCapabilities(
        CpuTelemetryReading? cpu,
        NvmlGpuReading? gpu) => new()
        {
            RazerHidAvailable = _razerClient is not null,
            NvmlAvailable = _gpu is not null,
            SelectedGpu = _gpu?.SelectedGpu,
            GpuTemperatureSupported = gpu?.TemperatureCelsius.IsSupported == true,
            GpuPowerSupported = gpu?.PowerWatts.IsSupported == true,
            LibreHardwareMonitorVersion = _cpu?.LibraryVersion ??
            $"{PinnedLibreHardwareMonitorVersion} (provider unavailable)",
            PawnIoAvailable = _cpu?.PawnIoInstalled == true,
            CpuPackageTemperatureAvailable =
            cpu?.PackageTemperatureCelsius is { IsValid: true, HasValue: true },
            CpuPackagePowerAvailable =
            cpu?.PackagePowerWatts is { IsValid: true, HasValue: true },
            AcpiZonesAvailable = false,
            GpuSelectionAmbiguous = _gpuAmbiguous,
            EnumeratedGpus = _enumeratedGpus,
            Diagnostics = _diagnostics
        };

    private static NvmlGpuReading UnavailableGpuReading(DateTimeOffset timestamp)
    {
        TelemetryMetric<double> temperature = TelemetryMetric<double>.Invalid(
            null,
            timestamp,
            TelemetrySources.GpuTemperature,
            "NVML did not initialize or GPU selection was ambiguous.");
        TelemetryMetric<double> optional = TelemetryMetric<double>.Unsupported(
            TelemetrySources.GpuOptional,
            "NVML unavailable.");
        TelemetryMetric<ulong> memory = TelemetryMetric<ulong>.Unsupported(
            TelemetrySources.GpuOptional,
            "NVML unavailable.");
        return new NvmlGpuReading(
            temperature,
            optional,
            optional,
            optional,
            optional,
            optional,
            memory,
            memory);
    }
}
