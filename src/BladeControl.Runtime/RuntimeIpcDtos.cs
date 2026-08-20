using BladeControl.Razer;
using BladeControl.Telemetry;
using BladeControl.Thermal;

namespace BladeControl.Runtime;

public sealed record RuntimeModeDto(
    string Zone1PerformanceMode,
    string Zone1FanMode,
    string Zone2PerformanceMode,
    string Zone2FanMode);

public sealed record PerformanceStateDto(
    RuntimeModeDto Mode,
    string CpuLevel,
    string GpuLevel,
    int Fan1Rpm,
    int Fan2Rpm);

public sealed record FanStateDto(
    RuntimeModeDto Mode,
    int Fan1Rpm,
    int Fan2Rpm);

public sealed record RuntimeRazerModeStateDto(
    string Zone1PerformanceMode,
    string Zone1FanMode,
    string Zone2PerformanceMode,
    string Zone2FanMode,
    bool ZonesAgree,
    bool IsBalancedManual,
    bool IsAuto,
    bool IsKnownAuto);

public sealed record ThermalMachineStateDto(
    string Zone1PerformanceMode,
    string Zone1FanMode,
    string Zone2PerformanceMode,
    string Zone2FanMode,
    string CpuLevel,
    string GpuLevel,
    int Fan1Rpm,
    int Fan2Rpm,
    bool ZonesAgree,
    bool IsAuto);

public sealed record TelemetryMetricDto<T>(
    T? Value,
    DateTimeOffset? Timestamp,
    string Provider,
    string Metric,
    string Authority,
    bool IsSupported,
    bool IsValid,
    bool HasValue,
    string? Diagnostic)
    where T : struct;

public sealed record ThermalTelemetrySampleDto(
    DateTimeOffset Timestamp,
    TelemetryMetricDto<double> CpuPackageTemperatureCelsius,
    TelemetryMetricDto<double> GpuTemperatureCelsius,
    TelemetryMetricDto<double> CpuCoreMaxTemperatureCelsius,
    TelemetryMetricDto<double> CpuPackagePowerWatts,
    TelemetryMetricDto<double> CpuTotalLoadPercent,
    TelemetryMetricDto<double> CpuClockMegahertz,
    TelemetryMetricDto<double> GpuPowerWatts,
    TelemetryMetricDto<double> GpuUtilizationPercent,
    TelemetryMetricDto<double> GpuMemoryUtilizationPercent,
    TelemetryMetricDto<double> GpuGraphicsClockMegahertz,
    TelemetryMetricDto<double> GpuMemoryClockMegahertz,
    TelemetryMetricDto<ulong> GpuVramUsedBytes,
    TelemetryMetricDto<ulong> GpuVramTotalBytes,
    IReadOnlyList<string> Warnings);

public sealed record RazerFirmwareStateDto(
    ushort VendorId,
    ushort ProductId,
    int Fan1Rpm,
    int Fan2Rpm,
    RuntimeModeDto Mode,
    string CpuLevel,
    string GpuLevel,
    IReadOnlyList<ProtocolExchangeDto> Exchanges);

public sealed record TelemetrySnapshotDto(
    ThermalTelemetrySampleDto Telemetry,
    IReadOnlyList<TelemetryMetricDto<double>> AcpiThermalZonesCelsius,
    RazerFirmwareStateDto? RazerFirmwareState,
    IReadOnlyList<string> Warnings);

public sealed record TelemetryHealthDto(string Kind, string Reason, bool IsHealthy);

public sealed record ThermalDecisionDto(
    long Sequence,
    DateTimeOffset Timestamp,
    TelemetryHealthDto Health,
    int? CpuCurveTargetRpm,
    int? GpuCurveTargetRpm,
    int? RequestedTargetRpm,
    int EffectiveTargetRpm,
    string? DemandSource,
    bool ShouldWrite,
    bool EmergencyAuto,
    string Reason);

public sealed record ProtocolExchangeDto(
    byte TransactionId,
    string Command,
    bool HasResponse,
    string? RequestReportHex = null,
    string? ResponseReportHex = null);

public sealed record RuntimeEventBatchDto(
    RuntimeStatusDto Status,
    IReadOnlyList<RuntimeEventDto> Events,
    long OldestAvailableSequence,
    long LatestAvailableSequence,
    bool GapDetected);

public sealed record StopThermalControlResultDto(
    bool SessionWasActive,
    bool Succeeded,
    string Message,
    RuntimeStatusDto FinalStatus);

public sealed record RuntimeEventDto(
    string Kind,
    long Sequence,
    DateTimeOffset Timestamp,
    string Message,
    ThermalTelemetrySampleDto? Telemetry = null,
    double? AcquisitionDurationMilliseconds = null,
    ThermalDecisionDto? ThermalDecision = null,
    int? TargetRpm = null,
    RuntimeRazerModeStateDto? WatchdogState = null,
    double? OverrunMilliseconds = null,
    bool? Succeeded = null,
    Guid? SessionId = null,
    ProtocolExchangeDto? Exchange = null);

public sealed record RuntimeStatusDto(
    string State,
    Guid? SessionId,
    DateTimeOffset? StartTimestamp,
    string? CurrentProfile,
    ThermalMachineStateDto? CapturedOriginalPerformanceState,
    int? CurrentEffectiveFanTargetRpm,
    ThermalTelemetrySampleDto? LatestAuthoritativeTelemetry,
    TelemetryHealthDto? TelemetryHealth,
    SchedulerMetrics Scheduler,
    string SchedulerHealth,
    string? RuntimeBuild,
    RuntimeRazerModeStateDto? LastRazerWatchdogState,
    DateTimeOffset? LastRazerWatchdogObservedAt,
    string? LastFailureReason,
    string? LastStartRejectionReason,
    string? EmergencyStatus,
    TimeSpan LastTelemetryAcquisitionDuration,
    DurationStatistics? TelemetryAcquisition,
    DurationStatistics? ActuatorDuration,
    long WatchdogCoalescedCount,
    long TotalEventCount,
    int RetainedThermalDecisionCount,
    int RetainedThermalTraceCount,
    IReadOnlyList<RuntimeEventDto> RecentEvents);

internal static class RuntimeIpcDtoMapper
{
    internal static RuntimeStatusDto ToSummaryDto(RuntimeStatus status) =>
        ToDto(status, includeEvents: false);

    internal static RuntimeEventBatchDto ToEventBatch(
        RuntimeStatus status,
        long afterSequence,
        int maximumEvents)
    {
        RuntimeEvent[] retained = status.RecentEvents.OrderBy(item => item.Sequence).ToArray();
        long oldest = retained.Length == 0 ? 0 : retained[0].Sequence;
        long latest = retained.Length == 0 ? 0 : retained[^1].Sequence;
        bool gap = afterSequence > 0 && oldest > afterSequence + 1;
        RuntimeEventDto[] events = retained
            .Where(item => item.Sequence > afterSequence)
            .Take(maximumEvents)
            .Select(item => ToDto(item, includeProtocolHex: true))
            .ToArray();
        return new RuntimeEventBatchDto(
            ToDto(status, includeEvents: false),
            events,
            oldest,
            latest,
            gap);
    }

    private static RuntimeStatusDto ToDto(RuntimeStatus status, bool includeEvents) => new(
        // Named arguments throughout, deliberately. This record has grown past twenty
        // parameters and now contains runs of same-typed neighbours - three adjacent nullable
        // strings (failure, rejection, emergency), two adjacent statistics, two longs, two
        // ints. A transposition inside any of those runs compiles cleanly, passes every test,
        // and surfaces as a diagnostic that quietly reports the wrong thing: a refusal shown as
        // a fault, actuator timings labelled as acquisition. Naming them makes that class of
        // mistake impossible to make silently.
        State: status.State.ToString(),
        SessionId: status.SessionId,
        StartTimestamp: status.StartTimestamp,
        CurrentProfile: status.CurrentProfile,
        CapturedOriginalPerformanceState: status.CapturedOriginalPerformanceState is null
            ? null
            : ToDto(status.CapturedOriginalPerformanceState),
        CurrentEffectiveFanTargetRpm: status.CurrentEffectiveFanTargetRpm,
        LatestAuthoritativeTelemetry: status.LatestAuthoritativeTelemetry is null
            ? null
            : ToDto(status.LatestAuthoritativeTelemetry),
        TelemetryHealth: status.TelemetryHealth is null ? null : ToDto(status.TelemetryHealth),
        Scheduler: status.Scheduler,
        SchedulerHealth: status.SchedulerHealth,
        RuntimeBuild: status.RuntimeBuild,
        LastRazerWatchdogState: status.LastRazerWatchdogState is null
            ? null
            : ToDto(status.LastRazerWatchdogState),
        LastRazerWatchdogObservedAt: status.LastRazerWatchdogObservedAt,
        LastFailureReason: status.LastFailureReason,
        LastStartRejectionReason: status.LastStartRejectionReason,
        EmergencyStatus: status.EmergencyStatus,
        LastTelemetryAcquisitionDuration: status.LastTelemetryAcquisitionDuration,
        TelemetryAcquisition: status.TelemetryAcquisition,
        ActuatorDuration: status.ActuatorDuration,
        WatchdogCoalescedCount: status.WatchdogCoalescedCount,
        TotalEventCount: status.TotalEventCount,
        RetainedThermalDecisionCount: status.RetainedThermalDecisionCount,
        RetainedThermalTraceCount: status.RetainedThermalTraceCount,
        RecentEvents: includeEvents
            ? status.RecentEvents.Select(item => ToDto(item, includeProtocolHex: false)).ToArray()
            : []);

    internal static PerformanceStateDto ToDto(PerformanceState state) => new(
        ToDto(state.Zone1Mode, state.Zone2Mode),
        state.CpuPerformanceLevel.ToString(),
        state.GpuPerformanceLevel.ToString(),
        state.Fan1.FirmwareReportedRpm,
        state.Fan2.FirmwareReportedRpm);

    internal static FanStateDto ToDto(FanControlState state) => new(
        ToDto(state.Zone1Mode, state.Zone2Mode),
        state.Fan1.FirmwareReportedRpm,
        state.Fan2.FirmwareReportedRpm);

    internal static TelemetrySnapshotDto ToDto(TelemetrySnapshot snapshot)
    {
        var sample = new ThermalTelemetrySample(
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
            Warnings = snapshot.Warnings
        };
        return new TelemetrySnapshotDto(
            ToDto(sample),
            snapshot.AcpiThermalZonesCelsius.Select(ToDto).ToArray(),
            snapshot.RazerFirmwareState is null
                ? null
                : ToDto(snapshot.RazerFirmwareState),
            snapshot.Warnings);
    }

    internal static RuntimeModeDto ToDto(
        RazerModeReading zone1,
        RazerModeReading zone2) => new(
        zone1.PerformanceMode.ToString(),
        zone1.FanMode.ToString(),
        zone2.PerformanceMode.ToString(),
        zone2.FanMode.ToString());

    private static RuntimeRazerModeStateDto ToDto(RuntimeRazerModeState state) => new(
        state.Zone1PerformanceMode.ToString(),
        state.Zone1FanMode.ToString(),
        state.Zone2PerformanceMode.ToString(),
        state.Zone2FanMode.ToString(),
        state.ZonesAgree,
        state.IsBalancedManual,
        state.IsAuto,
        state.IsKnownAuto);

    private static ThermalMachineStateDto ToDto(ThermalMachineState state) => new(
        state.Zone1PerformanceMode.ToString(),
        state.Zone1FanMode.ToString(),
        state.Zone2PerformanceMode.ToString(),
        state.Zone2FanMode.ToString(),
        state.CpuLevel.ToString(),
        state.GpuLevel.ToString(),
        state.FirmwareReportedFan1Rpm,
        state.FirmwareReportedFan2Rpm,
        state.ZonesAgree,
        state.IsAuto);

    internal static ThermalTelemetrySampleDto ToDto(ThermalTelemetrySample sample) => new(
        sample.Timestamp,
        ToDto(sample.CpuPackageTemperatureCelsius),
        ToDto(sample.GpuTemperatureCelsius),
        ToDto(sample.CpuCoreMaxTemperatureCelsius),
        ToDto(sample.CpuPackagePowerWatts),
        ToDto(sample.CpuTotalLoadPercent),
        ToDto(sample.CpuClockMegahertz),
        ToDto(sample.GpuPowerWatts),
        ToDto(sample.GpuUtilizationPercent),
        ToDto(sample.GpuMemoryUtilizationPercent),
        ToDto(sample.GpuGraphicsClockMegahertz),
        ToDto(sample.GpuMemoryClockMegahertz),
        ToDto(sample.GpuVramUsedBytes),
        ToDto(sample.GpuVramTotalBytes),
        sample.Warnings);

    private static TelemetryMetricDto<T> ToDto<T>(TelemetryMetric<T> metric)
        where T : struct => new(
        metric.Value,
        metric.Timestamp,
        metric.Source.Provider,
        metric.Source.Metric,
        metric.Source.Authority.ToString(),
        metric.IsSupported,
        metric.IsValid,
        metric.HasValue,
        metric.Diagnostic);

    private static RazerFirmwareStateDto ToDto(RazerStatusSnapshot state) => new(
        state.Device.VendorId,
        state.Device.ProductId,
        state.Fan1.FirmwareReportedRpm,
        state.Fan2.FirmwareReportedRpm,
        ToDto(state.Zone1Mode, state.Zone2Mode),
        state.CpuPerformanceLevel.ToString(),
        state.GpuPerformanceLevel.ToString(),
        state.Exchanges.Select(exchange => ToDto(exchange, includeHex: false)).ToArray());

    private static TelemetryHealthDto ToDto(TelemetryHealth health) => new(
        health.Kind.ToString(),
        health.Reason,
        health.IsHealthy);

    private static ThermalDecisionDto ToDto(ThermalDecision decision) => new(
        decision.Sequence,
        decision.Timestamp,
        ToDto(decision.Health),
        decision.CpuCurveTarget?.Value,
        decision.GpuCurveTarget?.Value,
        decision.RequestedTarget?.Value,
        decision.EffectiveTarget.Value,
        decision.DemandSource?.ToString(),
        decision.ShouldWrite,
        decision.EmergencyAuto,
        decision.Reason);

    private static ProtocolExchangeDto ToDto(
        RazerExchangeTrace exchange,
        bool includeHex) => new(
        exchange.TransactionId,
        $"0x{exchange.CombinedCommand:X4}",
        exchange.HasResponse,
        includeHex ? Convert.ToHexString(exchange.RequestReport.Span) : null,
        includeHex && exchange.HasResponse
            ? Convert.ToHexString(exchange.ResponseReport.Span)
            : null);

    private static RuntimeEventDto ToDto(
        RuntimeEvent item,
        bool includeProtocolHex) => item switch
        {
            TelemetrySampleEvent telemetry => new(
                telemetry.Kind.ToString(), telemetry.Sequence, telemetry.Timestamp,
                telemetry.Message, ToDto(telemetry.Sample),
                telemetry.AcquisitionDuration.TotalMilliseconds),
            ThermalDecisionEvent decision => new(
                decision.Kind.ToString(), decision.Sequence, decision.Timestamp,
                decision.Message, ThermalDecision: ToDto(decision.Decision)),
            FanTargetChangedEvent target => new(
                target.Kind.ToString(), target.Sequence, target.Timestamp,
                target.Message, TargetRpm: target.TargetRpm),
            RazerWatchdogCheckEvent watchdog => new(
                watchdog.Kind.ToString(), watchdog.Sequence, watchdog.Timestamp,
                watchdog.Message, WatchdogState: ToDto(watchdog.State)),
            SchedulerOverrunEvent overrun => new(
                overrun.Kind.ToString(), overrun.Sequence, overrun.Timestamp,
                overrun.Message, OverrunMilliseconds: overrun.Overrun.TotalMilliseconds),
            EmergencyHandoffEvent handoff => new(
                handoff.Kind.ToString(), handoff.Sequence, handoff.Timestamp,
                handoff.Message, Succeeded: handoff.Succeeded),
            SessionStartedEvent started => new(
                started.Kind.ToString(), started.Sequence, started.Timestamp,
                started.Message, SessionId: started.SessionId),
            SessionStoppedEvent stopped => new(
                stopped.Kind.ToString(), stopped.Sequence, stopped.Timestamp,
                stopped.Message, SessionId: stopped.SessionId),
            RecoveryResultEvent recovery => new(
                recovery.Kind.ToString(), recovery.Sequence, recovery.Timestamp,
                recovery.Message, Succeeded: recovery.Succeeded),
            ProtocolExchangeEvent exchange => new(
                exchange.Kind.ToString(), exchange.Sequence, exchange.Timestamp,
                exchange.Message, Exchange: ToDto(exchange.Exchange, includeProtocolHex)),
            _ => new(item.Kind.ToString(), item.Sequence, item.Timestamp, item.Message)
        };
}
