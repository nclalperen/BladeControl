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

    /// <summary>
    /// Thermal limits discovered from the GPU itself. Null when the device could not report
    /// them, which disqualifies it for thermal ownership rather than falling back to a guess.
    /// Discovered once at qualification: these are per-device constants, and nothing on the
    /// 500 ms telemetry path re-reads them.
    /// </summary>
    public GpuThermalLimits? GpuThermalLimits { get; init; }

    /// <summary>
    /// What the production discovery attempt actually concluded, in either direction.
    /// </summary>
    /// <remarks>
    /// "Unavailable" on its own is not a diagnosis. This carries the concrete outcome — which
    /// field failed, which predicate rejected, which signature did not match — so that a
    /// machine reporting no limits explains itself without needing a second, separate probe
    /// run to guess at what production did.
    /// </remarks>
    public string GpuThermalLimitDiagnostic { get; init; } = "GPU thermal limit discovery was not attempted.";

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

/// <summary>
/// The single authoritative answer to "may this machine take thermal ownership right now".
/// </summary>
/// <remarks>
/// <para>Runtime start, the CLI doctor and the GUI all consume <i>this</i> result. None of them
/// recomputes an approximation of it.</para>
/// <para>They used to. The CLI printed a heading of "Thermal-control qualification" over the
/// output of <see cref="TelemetryHealthEvaluator.Evaluate"/>, which only checks that the CPU
/// and GPU temperatures are present, plausible and fresh. It knows nothing about GPU thermal
/// limits, PawnIO provenance, Razer HID availability or GPU selection ambiguity — so it
/// answered "Healthy" on a machine that this qualifier was refusing, and the two statements sat
/// four lines apart in the same report.</para>
/// </remarks>
/// <param name="GpuThermalLimitsKnown">
/// Whether the GPU reported thermal limits that qualified. Required: without them there is no
/// GPU safety ladder and no threshold worth guessing.
/// </param>
/// <param name="GpuThermalLimitDiagnostic">
/// What discovery concluded, carried from the production path so a refusal is self-explaining.
/// </param>
public sealed record ThermalOwnershipQualification(
    DateTimeOffset Timestamp,
    bool CpuProviderProvenanceSafe,
    bool CpuPackageTemperatureHealthy,
    bool GpuTemperatureHealthy,
    bool GpuSelectionDeterministic,
    bool RazerHidAvailable,
    bool GpuThermalLimitsKnown,
    string GpuThermalLimitDiagnostic,
    bool ThermalOwnershipReady,
    TelemetryCapabilities Capabilities,
    IReadOnlyList<string> Reasons)
{
    /// <summary>The discovered limits, or null. Sourced from the same evaluated capabilities.</summary>
    public GpuThermalLimits? GpuThermalLimits => Capabilities.GpuThermalLimits;

    /// <summary>One line suitable for any surface that shows a verdict.</summary>
    public string Summary => ThermalOwnershipReady
        ? "QUALIFIED"
        : $"NOT QUALIFIED: {string.Join(" ", Reasons)}";
}

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

        // Without limits reported by the GPU itself there is no safe GPU thermal ladder, and
        // no threshold worth guessing: the previous hard-coded 80 C turned out to be the
        // reference part's hardware shutdown temperature. A device that cannot describe its
        // own limits does not qualify for closed-loop control.
        bool gpuLimitsKnown = capabilities.GpuThermalLimits is not null;
        if (!gpuLimitsKnown)
        {
            // Carry the concrete discovery outcome into the refusal. A bare "limits
            // unavailable" forces whoever reads it to go and reproduce the discovery by hand.
            reasons.Add(
                "GPU thermal limits could not be established, so no GPU safety thresholds " +
                $"exist: {capabilities.GpuThermalLimitDiagnostic}");
        }

        bool ready = cpuProviderProvenanceSafe && cpuHealthy && gpuHealthy &&
            deterministicGpu && razerAvailable && gpuLimitsKnown;
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
            gpuLimitsKnown,
            capabilities.GpuThermalLimitDiagnostic,
            ready,
            capabilities,
            reasons);
    }
}
