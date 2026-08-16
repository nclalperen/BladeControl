using BladeControl.Runtime;

namespace BladeControl.UI.Ipc;

/// <summary>
/// Builders for the runtime DTOs. Used by the development fake and by tests so neither has
/// to spell out eighteen positional arguments. Nothing here talks to hardware.
/// </summary>
public static class RuntimeUiSampleData
{
    public static TelemetryMetricDto<double> Metric(
        double? value,
        DateTimeOffset? timestamp = null,
        string provider = "fake",
        string metric = "metric",
        string authority = "Authoritative",
        bool isSupported = true,
        string? diagnostic = null) => new(
        value,
        value.HasValue ? timestamp ?? DateTimeOffset.UtcNow : null,
        provider,
        metric,
        authority,
        isSupported,
        value.HasValue,
        value.HasValue,
        diagnostic);

    public static TelemetryMetricDto<double> Unsupported(string metric, string reason) => new(
        null,
        null,
        "none",
        metric,
        "Unavailable",
        false,
        false,
        false,
        reason);

    public static TelemetryMetricDto<ulong> Bytes(ulong? value) => new(
        value,
        value.HasValue ? DateTimeOffset.UtcNow : null,
        "nvml",
        "vram",
        "Authoritative",
        value.HasValue,
        value.HasValue,
        value.HasValue,
        null);

    public static ThermalTelemetrySampleDto Telemetry(
        double cpuTemperature = 61.5,
        double gpuTemperature = 52.0,
        double cpuPower = 34.2,
        double cpuLoad = 12.5,
        double gpuPower = 21.7,
        double gpuUtilization = 4.0,
        DateTimeOffset? timestamp = null,
        IReadOnlyList<string>? warnings = null)
    {
        DateTimeOffset now = timestamp ?? DateTimeOffset.UtcNow;
        return new ThermalTelemetrySampleDto(
            now,
            Metric(cpuTemperature, now, "librehardwaremonitor", "cpu-package-temperature"),
            Metric(gpuTemperature, now, "nvml", "gpu-temperature"),
            Metric(cpuTemperature + 6.0, now, "librehardwaremonitor", "cpu-core-max"),
            Metric(cpuPower, now, "librehardwaremonitor", "cpu-package-power"),
            Metric(cpuLoad, now, "librehardwaremonitor", "cpu-total-load"),
            Metric(3_450, now, "librehardwaremonitor", "cpu-clock"),
            Metric(gpuPower, now, "nvml", "gpu-power"),
            Metric(gpuUtilization, now, "nvml", "gpu-utilization"),
            Metric(2.0, now, "nvml", "gpu-memory-utilization"),
            Metric(1_710, now, "nvml", "gpu-graphics-clock"),
            Metric(8_001, now, "nvml", "gpu-memory-clock"),
            Bytes(1_932_735_283),
            Bytes(17_179_869_184),
            warnings ?? []);
    }

    public static SchedulerMetrics Scheduler(
        long completedCycles = 0,
        long overrunCount = 0,
        double actualStartToStartMilliseconds = 0,
        double cycleDurationMilliseconds = 0,
        double maximumOverrunMilliseconds = 0) => new(
        TimeSpan.FromMilliseconds(500),
        completedCycles,
        TimeSpan.FromMilliseconds(actualStartToStartMilliseconds),
        TimeSpan.FromMilliseconds(cycleDurationMilliseconds),
        TimeSpan.Zero,
        overrunCount,
        TimeSpan.FromMilliseconds(maximumOverrunMilliseconds),
        0);

    public static RuntimeRazerModeStateDto Watchdog(bool isAuto = true) => new(
        isAuto ? "Balanced" : "Balanced",
        isAuto ? "Auto" : "Manual",
        isAuto ? "Balanced" : "Balanced",
        isAuto ? "Auto" : "Manual",
        true,
        !isAuto,
        isAuto,
        isAuto);

    public static ThermalMachineStateDto MachineState() => new(
        "Balanced",
        "Auto",
        "Balanced",
        "Auto",
        "Medium",
        "Low",
        0,
        0,
        true,
        true);

    public static RuntimeStatusDto Status(
        string state = "Stopped",
        Guid? sessionId = null,
        string? currentProfile = null,
        int? effectiveFanTargetRpm = null,
        ThermalTelemetrySampleDto? telemetry = null,
        TelemetryHealthDto? health = null,
        SchedulerMetrics? scheduler = null,
        string schedulerHealth = "Healthy",
        RuntimeRazerModeStateDto? watchdog = null,
        string? lastFailureReason = null,
        string? emergencyStatus = null,
        long totalEventCount = 0,
        IReadOnlyList<RuntimeEventDto>? recentEvents = null) => new(
        state,
        sessionId,
        sessionId.HasValue ? DateTimeOffset.UtcNow.AddMinutes(-3) : null,
        currentProfile,
        sessionId.HasValue ? MachineState() : null,
        effectiveFanTargetRpm,
        telemetry,
        health,
        scheduler ?? Scheduler(),
        schedulerHealth,
        watchdog,
        lastFailureReason,
        emergencyStatus,
        TimeSpan.FromMilliseconds(38),
        totalEventCount,
        0,
        0,
        recentEvents ?? []);

    public static RuntimeEventDto Event(
        string kind,
        long sequence,
        string message,
        DateTimeOffset? timestamp = null) => new(
        kind,
        sequence,
        timestamp ?? DateTimeOffset.UtcNow,
        message);

    public static RuntimeDoctorReportDto Doctor(
        bool thermalOwnershipReady = true,
        IReadOnlyList<string>? reasons = null) => new(
        new RuntimeTelemetryCapabilitiesDto(
            true,
            true,
            new RuntimeGpuIdentityDto(
                "NVIDIA GeForce RTX 4090 Laptop GPU",
                "GPU-00000000-0000-0000-0000-000000000000",
                "00000000:01:00.0"),
            true,
            true,
            "0.9.6.0",
            true,
            true,
            true,
            true,
            false,
            [],
            []),
        new RuntimePawnIoProvenanceDto(
            true,
            "1.2.0",
            @"C:\Windows\System32\drivers\PawnIO.sys",
            "Running",
            "1.2.0.0",
            "Valid",
            "Microsoft Windows Hardware Compatibility Publisher",
            "Namazso",
            "DigiCert Timestamp",
            "WindowsTrustedSigner",
            "0000000000000000000000000000000000000000000000000000000000000000",
            true,
            []),
        true,
        true,
        true,
        true,
        true,
        thermalOwnershipReady,
        reasons ?? [],
        DateTimeOffset.UtcNow);

    public static StoredThermalCurveDocument DefaultCurve() => new(
        1,
        "default",
        [
            new StoredThermalCurvePoint(50, 3000),
            new StoredThermalCurvePoint(60, 3300),
            new StoredThermalCurvePoint(70, 3800),
            new StoredThermalCurvePoint(80, 4400),
            new StoredThermalCurvePoint(88, 5000)
        ],
        [
            new StoredThermalCurvePoint(45, 3000),
            new StoredThermalCurvePoint(55, 3300),
            new StoredThermalCurvePoint(65, 3800),
            new StoredThermalCurvePoint(72, 4400),
            new StoredThermalCurvePoint(78, 5000)
        ]);
}
