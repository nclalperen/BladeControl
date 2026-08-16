using BladeControl.Razer;

namespace BladeControl.Telemetry;

public enum TelemetryAuthority
{
    Authoritative,
    Optional,
    Diagnostic
}

public sealed record TelemetrySource(
    string Provider,
    string Metric,
    TelemetryAuthority Authority);

public enum TelemetryFreshness
{
    Fresh,
    Stale,
    Unavailable
}

public sealed class TelemetryMetric<T> where T : struct
{
    private TelemetryMetric(
        T? value,
        DateTimeOffset? timestamp,
        TelemetrySource source,
        bool isSupported,
        bool isValid,
        string? diagnostic)
    {
        Value = value;
        Timestamp = timestamp;
        Source = source;
        IsSupported = isSupported;
        IsValid = isValid;
        Diagnostic = diagnostic;
    }

    public T? Value { get; }

    public DateTimeOffset? Timestamp { get; }

    public TelemetrySource Source { get; }

    public bool IsSupported { get; }

    public bool IsValid { get; }

    public bool HasValue => Value.HasValue;

    public string? Diagnostic { get; }

    public TimeSpan? Age(DateTimeOffset now) => Timestamp is null
        ? null
        : now >= Timestamp.Value
            ? now - Timestamp.Value
            : TimeSpan.Zero;

    public TelemetryFreshness Freshness(DateTimeOffset now, TimeSpan maximumAge)
    {
        if (!IsSupported || !IsValid || !HasValue || Timestamp is null)
        {
            return TelemetryFreshness.Unavailable;
        }

        return Age(now) <= maximumAge
            ? TelemetryFreshness.Fresh
            : TelemetryFreshness.Stale;
    }

    public static TelemetryMetric<T> Available(
        T value,
        DateTimeOffset timestamp,
        TelemetrySource source) =>
        new(value, timestamp, source, true, true, null);

    public static TelemetryMetric<T> Unsupported(
        TelemetrySource source,
        string? diagnostic = null) =>
        new(null, null, source, false, false, diagnostic ?? "Not supported.");

    public static TelemetryMetric<T> Missing(
        DateTimeOffset timestamp,
        TelemetrySource source,
        string diagnostic) =>
        new(null, timestamp, source, true, false, diagnostic);

    public static TelemetryMetric<T> Invalid(
        T? value,
        DateTimeOffset timestamp,
        TelemetrySource source,
        string diagnostic) =>
        new(value, timestamp, source, true, false, diagnostic);
}

public static class TelemetrySources
{
    public static readonly TelemetrySource CpuPackageTemperature = new(
        "LibreHardwareMonitor / PawnIO",
        "CPU Package temperature",
        TelemetryAuthority.Authoritative);

    public static readonly TelemetrySource CpuOptional = new(
        "LibreHardwareMonitor / PawnIO",
        "CPU optional telemetry",
        TelemetryAuthority.Optional);

    public static readonly TelemetrySource GpuTemperature = new(
        "NVIDIA NVML",
        "GPU core temperature",
        TelemetryAuthority.Authoritative);

    public static readonly TelemetrySource GpuOptional = new(
        "NVIDIA NVML",
        "GPU optional telemetry",
        TelemetryAuthority.Optional);

    public static readonly TelemetrySource RazerFirmware = new(
        "Razer HID",
        "Firmware-reported state",
        TelemetryAuthority.Diagnostic);

    public static readonly TelemetrySource AcpiZone = new(
        "Windows ACPI thermal zone",
        "Thermal-zone temperature",
        TelemetryAuthority.Diagnostic);
}

public sealed class ThermalTelemetrySample
{
    public ThermalTelemetrySample(
        DateTimeOffset timestamp,
        TelemetryMetric<double> cpuPackageTemperatureCelsius,
        TelemetryMetric<double> gpuTemperatureCelsius)
    {
        Timestamp = timestamp;
        CpuPackageTemperatureCelsius = cpuPackageTemperatureCelsius;
        GpuTemperatureCelsius = gpuTemperatureCelsius;
    }

    public DateTimeOffset Timestamp { get; }

    public TelemetryMetric<double> CpuPackageTemperatureCelsius { get; }

    public TelemetryMetric<double> GpuTemperatureCelsius { get; }

    public TelemetryMetric<double> CpuPackagePowerWatts { get; init; } =
        TelemetryMetric<double>.Unsupported(TelemetrySources.CpuOptional);

    public TelemetryMetric<double> CpuCoreMaxTemperatureCelsius { get; init; } =
        TelemetryMetric<double>.Unsupported(TelemetrySources.CpuOptional);

    public TelemetryMetric<double> CpuTotalLoadPercent { get; init; } =
        TelemetryMetric<double>.Unsupported(TelemetrySources.CpuOptional);

    public TelemetryMetric<double> CpuClockMegahertz { get; init; } =
        TelemetryMetric<double>.Unsupported(TelemetrySources.CpuOptional);

    public TelemetryMetric<double> GpuPowerWatts { get; init; } =
        TelemetryMetric<double>.Unsupported(TelemetrySources.GpuOptional);

    public TelemetryMetric<double> GpuUtilizationPercent { get; init; } =
        TelemetryMetric<double>.Unsupported(TelemetrySources.GpuOptional);

    public TelemetryMetric<double> GpuMemoryUtilizationPercent { get; init; } =
        TelemetryMetric<double>.Unsupported(TelemetrySources.GpuOptional);

    public TelemetryMetric<double> GpuGraphicsClockMegahertz { get; init; } =
        TelemetryMetric<double>.Unsupported(TelemetrySources.GpuOptional);

    public TelemetryMetric<double> GpuMemoryClockMegahertz { get; init; } =
        TelemetryMetric<double>.Unsupported(TelemetrySources.GpuOptional);

    public TelemetryMetric<ulong> GpuVramUsedBytes { get; init; } =
        TelemetryMetric<ulong>.Unsupported(TelemetrySources.GpuOptional);

    public TelemetryMetric<ulong> GpuVramTotalBytes { get; init; } =
        TelemetryMetric<ulong>.Unsupported(TelemetrySources.GpuOptional);

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public TelemetrySnapshot ToDiagnosticSnapshot() => new(
        Timestamp,
        CpuPackageTemperatureCelsius,
        GpuTemperatureCelsius)
    {
        CpuCoreMaxTemperatureCelsius = CpuCoreMaxTemperatureCelsius,
        CpuPackagePowerWatts = CpuPackagePowerWatts,
        CpuTotalLoadPercent = CpuTotalLoadPercent,
        CpuClockMegahertz = CpuClockMegahertz,
        GpuPowerWatts = GpuPowerWatts,
        GpuUtilizationPercent = GpuUtilizationPercent,
        GpuMemoryUtilizationPercent = GpuMemoryUtilizationPercent,
        GpuGraphicsClockMegahertz = GpuGraphicsClockMegahertz,
        GpuMemoryClockMegahertz = GpuMemoryClockMegahertz,
        GpuVramUsedBytes = GpuVramUsedBytes,
        GpuVramTotalBytes = GpuVramTotalBytes,
        Warnings = Warnings
    };
}

public sealed class TelemetrySnapshot
{
    public TelemetrySnapshot(
        DateTimeOffset timestamp,
        TelemetryMetric<double> cpuPackageTemperatureCelsius,
        TelemetryMetric<double> gpuTemperatureCelsius)
    {
        Timestamp = timestamp;
        CpuPackageTemperatureCelsius = cpuPackageTemperatureCelsius;
        GpuTemperatureCelsius = gpuTemperatureCelsius;
    }

    public DateTimeOffset Timestamp { get; }

    public TelemetryMetric<double> CpuPackageTemperatureCelsius { get; }

    public TelemetryMetric<double> GpuTemperatureCelsius { get; }

    public TelemetryMetric<double> CpuCoreMaxTemperatureCelsius { get; init; } =
        TelemetryMetric<double>.Unsupported(TelemetrySources.CpuOptional);

    public TelemetryMetric<double> CpuPackagePowerWatts { get; init; } =
        TelemetryMetric<double>.Unsupported(TelemetrySources.CpuOptional);

    public TelemetryMetric<double> CpuTotalLoadPercent { get; init; } =
        TelemetryMetric<double>.Unsupported(TelemetrySources.CpuOptional);

    public TelemetryMetric<double> CpuClockMegahertz { get; init; } =
        TelemetryMetric<double>.Unsupported(TelemetrySources.CpuOptional);

    public TelemetryMetric<double> GpuPowerWatts { get; init; } =
        TelemetryMetric<double>.Unsupported(TelemetrySources.GpuOptional);

    public TelemetryMetric<double> GpuUtilizationPercent { get; init; } =
        TelemetryMetric<double>.Unsupported(TelemetrySources.GpuOptional);

    public TelemetryMetric<double> GpuMemoryUtilizationPercent { get; init; } =
        TelemetryMetric<double>.Unsupported(TelemetrySources.GpuOptional);

    public TelemetryMetric<double> GpuGraphicsClockMegahertz { get; init; } =
        TelemetryMetric<double>.Unsupported(TelemetrySources.GpuOptional);

    public TelemetryMetric<double> GpuMemoryClockMegahertz { get; init; } =
        TelemetryMetric<double>.Unsupported(TelemetrySources.GpuOptional);

    public TelemetryMetric<ulong> GpuVramUsedBytes { get; init; } =
        TelemetryMetric<ulong>.Unsupported(TelemetrySources.GpuOptional);

    public TelemetryMetric<ulong> GpuVramTotalBytes { get; init; } =
        TelemetryMetric<ulong>.Unsupported(TelemetrySources.GpuOptional);

    public IReadOnlyList<TelemetryMetric<double>> AcpiThermalZonesCelsius { get; init; } = [];

    public RazerStatusSnapshot? RazerFirmwareState { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record TelemetryGpuIdentity(
    string Name,
    string Uuid,
    string PciBusId);

public sealed class TelemetryCapabilities
{
    public bool RazerHidAvailable { get; init; }

    public bool NvmlAvailable { get; init; }

    public TelemetryGpuIdentity? SelectedGpu { get; init; }

    public bool GpuTemperatureSupported { get; init; }

    public bool GpuPowerSupported { get; init; }

    public string LibreHardwareMonitorVersion { get; init; } = "unavailable";

    public bool PawnIoAvailable { get; init; }

    public bool CpuPackageTemperatureAvailable { get; init; }

    public bool CpuPackagePowerAvailable { get; init; }

    public bool AcpiZonesAvailable { get; init; }

    public bool GpuSelectionAmbiguous { get; init; }

    public IReadOnlyList<TelemetryGpuIdentity> EnumeratedGpus { get; init; } = [];

    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

public sealed record ThermalOwnershipQualification(
    DateTimeOffset Timestamp,
    bool CpuProviderProvenanceSafe,
    bool CpuPackageTemperatureHealthy,
    bool GpuTemperatureHealthy,
    bool GpuSelectionDeterministic,
    bool RazerHidAvailable,
    bool ThermalOwnershipReady,
    TelemetryCapabilities Capabilities,
    IReadOnlyList<string> Reasons);

public static class ThermalOwnershipQualifier
{
    public static ThermalOwnershipQualification Evaluate(
        DateTimeOffset now,
        bool cpuProviderProvenanceSafe,
        TelemetryCapabilities capabilities,
        ThermalTelemetrySample sample)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(sample);

        TelemetryHealth cpuHealth = TelemetryHealthEvaluator.EvaluateRequiredCpuTemperature(
            sample.CpuPackageTemperatureCelsius,
            now);
        TelemetryHealth gpuHealth = TelemetryHealthEvaluator.EvaluateRequiredGpuTemperature(
            sample.GpuTemperatureCelsius,
            now);
        bool cpuHealthy = cpuHealth.IsHealthy &&
            sample.CpuPackageTemperatureCelsius.Source ==
                TelemetrySources.CpuPackageTemperature;
        bool gpuHealthy = gpuHealth.IsHealthy &&
            sample.GpuTemperatureCelsius.Source == TelemetrySources.GpuTemperature;
        bool deterministicGpu = capabilities.NvmlAvailable &&
            capabilities.SelectedGpu is not null &&
            !capabilities.GpuSelectionAmbiguous;
        bool razerAvailable = capabilities.RazerHidAvailable;
        var reasons = new List<string>();
        if (!cpuProviderProvenanceSafe)
        {
            reasons.Add("PawnIO/CPU provider provenance policy did not pass.");
        }

        if (!cpuHealthy)
        {
            reasons.Add(cpuHealth.IsHealthy
                ? "CPU Package temperature did not come from the authoritative provider."
                : cpuHealth.Reason);
        }

        if (!gpuHealthy)
        {
            reasons.Add(gpuHealth.IsHealthy
                ? "GPU temperature did not come from the authoritative NVML provider."
                : gpuHealth.Reason);
        }

        if (!deterministicGpu)
        {
            reasons.Add("NVML GPU selection is unavailable or ambiguous.");
        }

        if (!razerAvailable)
        {
            reasons.Add("The selected Razer HID management interface is unavailable.");
        }

        bool ready = cpuProviderProvenanceSafe && cpuHealthy && gpuHealthy &&
            deterministicGpu && razerAvailable;
        if (ready)
        {
            reasons.Add("Authoritative CPU/GPU telemetry and Razer HID are ready.");
        }

        return new ThermalOwnershipQualification(
            sample.Timestamp,
            cpuProviderProvenanceSafe,
            cpuHealthy,
            gpuHealthy,
            deterministicGpu,
            razerAvailable,
            ready,
            capabilities,
            reasons);
    }
}
