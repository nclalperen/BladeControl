using System.Diagnostics;
using BladeControl.Hardware.Windows.Telemetry.Nvml;
using BladeControl.Razer;
using BladeControl.Telemetry;

namespace BladeControl.Hardware.Windows.Telemetry;

public sealed class WindowsTelemetrySession : ITelemetryProvider, IControlTelemetryProvider
{
    private const string PinnedLibreHardwareMonitorVersion = "0.9.6";

    private readonly RazerClient? _razerClient;
    private readonly LibreHardwareMonitorCpuProvider? _cpu;
    private readonly NvmlTelemetryProvider? _gpu;
    private readonly List<string> _diagnostics;
    private readonly bool _gpuAmbiguous;
    private readonly IReadOnlyList<TelemetryGpuIdentity> _enumeratedGpus;

    /// <summary>
    /// Discovered once when the session opens. These are per-device constants, so re-reading
    /// them on the 500 ms telemetry path would cost NVML calls for values that never change.
    /// </summary>
    private readonly GpuThermalLimits? _gpuThermalLimits;

    /// <summary>
    /// What that discovery concluded, kept whether it succeeded or failed, so the reason
    /// travels with the capabilities instead of only reaching a verbose diagnostic list.
    /// </summary>
    private readonly string _gpuThermalLimitDiagnostic;
    private bool _disposed;

    private WindowsTelemetrySession(
        RazerClient? razerClient,
        LibreHardwareMonitorCpuProvider? cpu,
        NvmlTelemetryProvider? gpu,
        bool gpuAmbiguous,
        IReadOnlyList<TelemetryGpuIdentity> enumeratedGpus,
        PawnIoProvenance pawnIoProvenance,
        IReadOnlyList<string> diagnostics)
    {
        _razerClient = razerClient;
        _cpu = cpu;
        _gpu = gpu;
        _gpuAmbiguous = gpuAmbiguous;
        _enumeratedGpus = enumeratedGpus.ToArray();
        PawnIoProvenance = pawnIoProvenance;
        _diagnostics = diagnostics.ToList();

        if (_gpu is null)
        {
            _gpuThermalLimitDiagnostic =
                "NVML did not initialize, so GPU thermal limits could not be read.";
            _diagnostics.Add(_gpuThermalLimitDiagnostic);
        }
        else if (_gpu.TryDiscoverThermalLimits(
            out GpuThermalLimits? limits,
            out string diagnostic))
        {
            _gpuThermalLimits = limits;
            _gpuThermalLimitDiagnostic = diagnostic;
            _diagnostics.Add($"GPU thermal limits: {diagnostic}");
        }
        else
        {
            // Recorded, not fatal here: closed-loop thermal control refuses to qualify
            // without limits, while every other feature is unaffected. The reason is kept on
            // the capabilities so the refusal can explain itself wherever it surfaces.
            _gpuThermalLimitDiagnostic = diagnostic;
            _diagnostics.Add($"GPU thermal limits unavailable: {diagnostic}");
        }

        Capabilities = CreateCapabilities(null, null);
    }

    public string Name => "Windows telemetry providers";

    public TelemetryCapabilities Capabilities { get; private set; }

    public PawnIoProvenance PawnIoProvenance { get; }

    /// <summary>How long the most recent CPU provider read took.</summary>
    /// <remarks>
    /// Two plain fields rather than a statistics object: this is the 500 ms path, and the
    /// windowing that turns these into percentiles belongs to the caller that already keeps
    /// one.
    /// </remarks>
    public TimeSpan LastCpuAcquisitionDuration { get; private set; }

    /// <summary>How long the most recent GPU provider read took.</summary>
    public TimeSpan LastGpuAcquisitionDuration { get; private set; }

    public static WindowsTelemetrySession Open(
        RazerClient? razerClient = null,
        string? preferredGpuPciBusId = null)
    {
        var diagnostics = new List<string>();
        PawnIoProvenance pawnIoProvenance = PawnIoProvenanceReader.Read();
        diagnostics.AddRange(pawnIoProvenance.Diagnostics);
        LibreHardwareMonitorCpuProvider? cpu = null;
        try
        {
            if (pawnIoProvenance.IsSafeForThermalOwnership)
            {
                cpu = LibreHardwareMonitorCpuProvider.Open();
            }
            else
            {
                diagnostics.Add(
                    "PawnIO provenance is not safe; authoritative CPU telemetry was not opened.");
            }

            if (cpu is not null && !cpu.PawnIoInstalled)
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
            pawnIoProvenance,
            diagnostics);
    }

    public TelemetrySnapshot GetSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThermalTelemetrySample control = GetControlSample();
        RazerStatusSnapshot? firmware = null;
        var warnings = new List<string>(control.Warnings);
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

        TelemetrySnapshot snapshot = control.ToDiagnosticSnapshot();
        return new TelemetrySnapshot(
            snapshot.Timestamp,
            snapshot.CpuPackageTemperatureCelsius,
            snapshot.GpuTemperatureCelsius)
        {
            CpuCoreMaxTemperatureCelsius = snapshot.CpuCoreMaxTemperatureCelsius,
            CpuPackagePowerWatts = snapshot.CpuPackagePowerWatts,
            CpuTotalLoadPercent = snapshot.CpuTotalLoadPercent,
            CpuClockMegahertz = snapshot.CpuClockMegahertz,
            GpuPowerWatts = snapshot.GpuPowerWatts,
            GpuUtilizationPercent = snapshot.GpuUtilizationPercent,
            GpuMemoryUtilizationPercent = snapshot.GpuMemoryUtilizationPercent,
            GpuGraphicsClockMegahertz = snapshot.GpuGraphicsClockMegahertz,
            GpuMemoryClockMegahertz = snapshot.GpuMemoryClockMegahertz,
            GpuVramUsedBytes = snapshot.GpuVramUsedBytes,
            GpuVramTotalBytes = snapshot.GpuVramTotalBytes,
            AcpiThermalZonesCelsius = snapshot.AcpiThermalZonesCelsius,
            RazerFirmwareState = firmware,
            Warnings = warnings
        };
    }

    public ThermalTelemetrySample GetControlSample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        // Measured separately because they are separate problems. An aggregate figure of
        // ~390 ms says the cycle is tight; it does not say which provider to look at, and the
        // two are not remotely comparable in cost.
        long cpuStarted = Stopwatch.GetTimestamp();
        CpuTelemetryReading cpu = _cpu?.Read(timestamp) ??
            CpuTelemetryReading.ProviderFailure(
                timestamp,
                "The CPU telemetry provider did not initialize.");
        LastCpuAcquisitionDuration = Stopwatch.GetElapsedTime(cpuStarted);

        long gpuStarted = Stopwatch.GetTimestamp();
        NvmlGpuReading gpu = _gpu?.Read(timestamp) ?? UnavailableGpuReading(timestamp);
        LastGpuAcquisitionDuration = Stopwatch.GetElapsedTime(gpuStarted);
        Capabilities = CreateCapabilities(cpu, gpu);
        return new ThermalTelemetrySample(
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
            Warnings = []
        };
    }

    public ThermalOwnershipQualification QualifyThermalOwnership()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThermalTelemetrySample sample = GetControlSample();
        return ThermalOwnershipQualifier.Evaluate(
            DateTimeOffset.UtcNow,
            PawnIoProvenance.IsSafeForThermalOwnership,
            Capabilities,
            sample);
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
            GpuThermalLimits = _gpuThermalLimits,
            GpuThermalLimitDiagnostic = _gpuThermalLimitDiagnostic,
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
