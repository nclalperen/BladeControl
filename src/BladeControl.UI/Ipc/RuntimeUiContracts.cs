using BladeControl.Runtime;

namespace BladeControl.UI.Ipc;

/// <summary>
/// Distinguishes "the pipe is not reachable" from "Runtime Core answered and said no".
/// Only <see cref="Transport"/> may move the UI to the offline state; a rejection is a
/// backend error that must be surfaced verbatim without dropping the connection.
/// </summary>
public enum RuntimeUiFailureKind
{
    Transport,
    Rejected,
    Protocol
}

public sealed class RuntimeUiException : Exception
{
    public RuntimeUiException(RuntimeUiFailureKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public RuntimeUiFailureKind Kind { get; }

    public bool IsDisconnect => Kind == RuntimeUiFailureKind.Transport;
}

/// <summary>
/// Result envelope returned by the runtime for the two state-changing profile operations.
/// </summary>
public sealed record RuntimeCommandResultDto(
    bool Succeeded,
    string? Outcome,
    string? Message);

public sealed record RuntimeGpuIdentityDto(
    string? Name,
    string? Uuid,
    string? PciBusId);

public sealed record RuntimeTelemetryCapabilitiesDto(
    bool RazerHidAvailable,
    bool NvmlAvailable,
    RuntimeGpuIdentityDto? SelectedGpu,
    bool GpuTemperatureSupported,
    bool GpuPowerSupported,
    string? LibreHardwareMonitorVersion,
    bool PawnIoAvailable,
    bool CpuPackageTemperatureAvailable,
    bool CpuPackagePowerAvailable,
    bool AcpiZonesAvailable,
    bool GpuSelectionAmbiguous,
    IReadOnlyList<RuntimeGpuIdentityDto>? EnumeratedGpus,
    IReadOnlyList<string>? Diagnostics);

public sealed record RuntimePawnIoProvenanceDto(
    bool Installed,
    string? Version,
    string? DriverPath,
    string? ServiceState,
    string? FileVersion,
    string? AuthenticodeStatus,
    string? WindowsTrustedSignerSubject,
    string? EmbeddedSignerSubject,
    string? TimestampSignerSubject,
    string? SignatureSource,
    string? Sha256,
    bool IsSafeForThermalOwnership,
    IReadOnlyList<string>? Diagnostics);

/// <summary>
/// Mirror of the anonymous doctor payload produced by the runtime host. The shape lives in
/// BladeControl.Service, which the UI must not reference, so every member is optional and a
/// missing field degrades to "unknown" instead of failing the whole request.
/// </summary>
public sealed record RuntimeDoctorReportDto(
    RuntimeTelemetryCapabilitiesDto? Capabilities,
    RuntimePawnIoProvenanceDto? PawnIoProvenance,
    bool CpuProviderProvenanceSafe,
    bool CpuPackageTemperatureHealthy,
    bool GpuTemperatureHealthy,
    bool GpuSelectionDeterministic,
    bool RazerHidAvailable,
    bool GpuThermalLimitsKnown,
    string? GpuThermalLimitDiagnostic,
    bool ThermalOwnershipReady,
    IReadOnlyList<string>? Reasons,
    DateTimeOffset? QualificationTimestamp);

/// <summary>
/// The only channel the GUI has to the machine. Every implementation is an IPC client:
/// no implementation may open HID, PawnIO, NVML, or construct a BladeRuntime.
/// </summary>
public interface IRuntimeUiClient
{
    /// <summary>
    /// True for the production named-pipe client. The shell refuses to run a build whose
    /// client is not live, so a development fake can never be mistaken for real hardware.
    /// </summary>
    bool IsLiveRuntimeChannel { get; }

    Task<RuntimeStatusDto> GetStatusAsync(CancellationToken cancellationToken);

    Task<ThermalTelemetrySampleDto> GetTelemetrySampleAsync(
        CancellationToken cancellationToken);

    Task<TelemetrySnapshotDto> GetTelemetrySnapshotAsync(CancellationToken cancellationToken);

    Task<PerformanceStateDto> GetPerformanceStateAsync(CancellationToken cancellationToken);

    Task<FanStateDto> GetFanStateAsync(CancellationToken cancellationToken);

    Task<RuntimeDoctorReportDto> GetDoctorAsync(CancellationToken cancellationToken);

    Task<RuntimeEventBatchDto> GetEventsAsync(
        long afterSequence,
        int maximumEvents,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListBuiltInCurvesAsync(CancellationToken cancellationToken);

    Task<StoredThermalCurveDocument> GetThermalCurveAsync(
        string name,
        CancellationToken cancellationToken);

    Task<RuntimeCommandResultDto> ApplyPerformanceProfileAsync(
        ApplyPerformanceProfileRequest request,
        CancellationToken cancellationToken);

    Task<RuntimeCommandResultDto> ApplyFanProfileAsync(
        ApplyFanProfileRequest request,
        CancellationToken cancellationToken);

    Task<RuntimeStatusDto> StartThermalControlAsync(
        string curve,
        CancellationToken cancellationToken);

    Task<StopThermalControlResultDto> StopThermalControlAsync(CancellationToken cancellationToken);
}
