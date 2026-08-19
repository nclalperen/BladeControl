using BladeControl.Hardware.Windows.Telemetry.Nvml;
using BladeControl.Telemetry;

namespace BladeControl.Hardware.Windows.Telemetry;

/// <summary>One field of an nvmlDeviceGetFieldValues response, exactly as the driver returned it.</summary>
/// <param name="FieldId">The NVML field identifier that was requested.</param>
/// <param name="Result">Per-field <c>nvmlReturn</c>, which is distinct from the call's result.</param>
/// <param name="ValueType">The union member the driver declared it populated.</param>
/// <param name="RawValue">The raw eight-byte union payload, before any interpretation.</param>
/// <param name="Celsius">Decoded temperature, or null when the payload was not readable.</param>
public sealed record GpuThermalFieldReport(
    uint FieldId,
    string Result,
    string ValueType,
    long RawValue,
    double? Celsius);

/// <summary>One sensor entry from nvmlDeviceGetThermalSettings, exactly as returned.</summary>
/// <param name="Controller">nvmlThermalController_t member.</param>
/// <param name="Target">nvmlThermalTarget_t member; GPU is the one of interest.</param>
/// <param name="CurrentTemperatureCelsius">The sensor's present reading.</param>
/// <param name="DefaultMinimumCelsius">defaultMinTemp, semantics not assumed.</param>
/// <param name="DefaultMaximumCelsius">defaultMaxTemp, semantics not assumed.</param>
public sealed record GpuThermalSensorReport(
    string Controller,
    string Target,
    int CurrentTemperatureCelsius,
    int DefaultMinimumCelsius,
    int DefaultMaximumCelsius);

/// <summary>One nvmlDeviceGetThermalSettings query and everything it returned.</summary>
public sealed record GpuThermalSettingsReport(
    uint RequestedSensorIndex,
    string Result,
    uint ReturnedSensorCount,
    IReadOnlyList<GpuThermalSensorReport> Sensors);

/// <summary>One absolute threshold from the legacy NVML API, with the driver's own status.</summary>
/// <param name="Threshold">Which nvmlTemperatureThresholds_t member was requested.</param>
/// <param name="Result">The driver's status for this specific query.</param>
/// <param name="Celsius">Absolute temperature, or null when the driver refused it.</param>
public sealed record GpuAbsoluteThresholdReport(
    string Threshold,
    string Result,
    double? Celsius);

/// <summary>
/// Everything the NVIDIA driver reports about a GPU's thermal limits in one read-only pass.
/// </summary>
public sealed record GpuThermalLimitReport
{
    public required bool NvmlAvailable { get; init; }

    public required string Diagnostic { get; init; }

    public string DeviceName { get; init; } = "unavailable";

    public string PciBusId { get; init; } = "unavailable";

    public bool SelectionAmbiguous { get; init; }

    /// <summary>Which entry point produced the current temperature.</summary>
    public string TemperatureSource { get; init; } = "unavailable";

    public string TemperatureResult { get; init; } = "unavailable";

    public double? CurrentTemperatureCelsius { get; init; }

    /// <summary>Result of the nvmlDeviceGetFieldValues call itself, not of any single field.</summary>
    public string FieldCallResult { get; init; } = "unavailable";

    public GpuThermalFieldReport? Shutdown { get; init; }

    public GpuThermalFieldReport? Slowdown { get; init; }

    public GpuThermalFieldReport? GpuMax { get; init; }

    public string MarginResult { get; init; } = "unavailable";

    /// <summary>The live margin, null when the driver refused it. Never defaulted.</summary>
    public int? MarginCelsius { get; init; }

    /// <summary>
    /// Absolute thresholds from the legacy nvmlDeviceGetTemperatureThreshold API, used to
    /// corroborate the relative T.Limit derivation at qualification.
    /// </summary>
    public GpuAbsoluteThresholdReport? LegacyShutdown { get; init; }

    public GpuAbsoluteThresholdReport? LegacySlowdown { get; init; }

    public GpuAbsoluteThresholdReport? LegacyGpuMax { get; init; }

    /// <summary>
    /// Every member of nvmlTemperatureThresholds_t, for diagnostics. Nothing in the control
    /// path reads this.
    /// </summary>
    public IReadOnlyList<GpuAbsoluteThresholdReport> AllLegacyThresholds { get; init; } = [];

    /// <summary>
    /// nvmlDeviceGetThermalSettings across every sensor index the header allows. Diagnostic;
    /// nothing in the control path reads it.
    /// </summary>
    public IReadOnlyList<GpuThermalSettingsReport> ThermalSettings { get; init; } = [];

    public GpuThermalLimits? DerivedLimits { get; init; }

    public string DerivedDiagnostic { get; init; } = "unavailable";
}

/// <summary>
/// Read-only NVML thermal-limit diagnostic.
/// </summary>
/// <remarks>
/// <para>Deliberately narrow: it initialises NVML and nothing else. No Razer HID, no
/// LibreHardwareMonitor, no PawnIO, no runtime, and no write of any kind. It exists so the
/// meaning of the driver's T.Limit numbers can be established against real hardware rather
/// than inferred from documentation, and so a later regression in that meaning is visible.</para>
/// <para>The T.Limit specifications are static and should match
/// <c>nvidia-smi -q -d TEMPERATURE</c> exactly; the live margin moves with load, so the two
/// have to be sampled in the same time window to be compared.</para>
/// </remarks>
public static class GpuThermalLimitDiagnostic
{
    public static GpuThermalLimitReport Read(string? preferredPciBusId = null)
    {
        if (!NvmlTelemetryProvider.TryOpen(
                preferredPciBusId,
                out NvmlTelemetryProvider? provider,
                out bool ambiguous,
                out _,
                out string diagnostic))
        {
            return new GpuThermalLimitReport
            {
                NvmlAvailable = false,
                Diagnostic = diagnostic
            };
        }

        using NvmlTelemetryProvider gpu = provider!;
        NvmlThermalLimitProbe probe = gpu.ProbeThermalLimits();
        bool derived = gpu.TryDiscoverThermalLimits(
            out GpuThermalLimits? limits,
            out string derivedDiagnostic);

        return new GpuThermalLimitReport
        {
            NvmlAvailable = true,
            Diagnostic = diagnostic,
            DeviceName = gpu.SelectedGpu.Name,
            PciBusId = gpu.SelectedGpu.PciBusId,
            SelectionAmbiguous = ambiguous,
            TemperatureSource = probe.TemperatureSource,
            TemperatureResult = probe.TemperatureResult.ToString(),
            CurrentTemperatureCelsius = probe.CurrentTemperatureCelsius,
            FieldCallResult = probe.FieldCallResult.ToString(),
            Shutdown = Field(probe.Shutdown),
            Slowdown = Field(probe.Slowdown),
            GpuMax = Field(probe.GpuMax),
            MarginResult = probe.MarginResult.ToString(),
            MarginCelsius = probe.MarginCelsius,
            LegacyShutdown = Threshold(probe.LegacyShutdown),
            LegacySlowdown = Threshold(probe.LegacySlowdown),
            LegacyGpuMax = Threshold(probe.LegacyGpuMax),
            AllLegacyThresholds = gpu.ProbeAllTemperatureThresholds().Select(Threshold).ToArray(),
            ThermalSettings = gpu.ProbeThermalSettings().Select(Settings).ToArray(),
            DerivedLimits = derived ? limits : null,
            DerivedDiagnostic = derivedDiagnostic
        };
    }

    private static GpuThermalSettingsReport Settings(NvmlThermalSettingsReading reading) => new(
        reading.RequestedIndex,
        reading.Result.ToString(),
        reading.Count,
        reading.Sensors
            .Select(sensor => new GpuThermalSensorReport(
                sensor.Controller.ToString(),
                sensor.Target.ToString(),
                sensor.CurrentTemp,
                sensor.DefaultMinTemp,
                sensor.DefaultMaxTemp))
            .ToArray());

    private static GpuAbsoluteThresholdReport Threshold(NvmlThresholdReading reading) => new(
        reading.Threshold.ToString(),
        reading.Result.ToString(),
        reading.Celsius);

    private static GpuThermalFieldReport Field(NvmlFieldReading reading) => new(
        reading.FieldId,
        reading.Result.ToString(),
        reading.ValueType.ToString(),
        reading.RawValue,
        reading.Celsius);
}
