using BladeControl.Runtime;
using BladeControl.UI.Ipc;

namespace BladeControl.UI.Tests;

internal sealed class ScriptedRuntimeUiClient : IRuntimeUiClient
{
    private readonly Queue<RuntimeEventBatchDto> _eventBatches = new();

    public ScriptedRuntimeUiClient()
    {
        Telemetry = RuntimeUiSampleData.Telemetry();
        Status = RuntimeUiSampleData.Status(
            telemetry: Telemetry,
            health: new TelemetryHealthDto("Healthy", "Telemetry is live.", true),
            watchdog: RuntimeUiSampleData.Watchdog());
        Doctor = RuntimeUiSampleData.Doctor();
        Performance = new PerformanceStateDto(
            new RuntimeModeDto("Balanced", "Auto", "Balanced", "Auto"),
            "Medium",
            "Low",
            0,
            0);
        Fan = new FanStateDto(
            new RuntimeModeDto("Balanced", "Auto", "Balanced", "Auto"),
            0,
            0);
    }

    public bool IsLiveRuntimeChannel => false;

    public RuntimeStatusDto Status { get; set; }

    public ThermalTelemetrySampleDto Telemetry { get; set; }

    public RuntimeDoctorReportDto Doctor { get; set; }

    public PerformanceStateDto Performance { get; set; }

    public FanStateDto Fan { get; set; }

    public List<long> RequestedEventCursors { get; } = [];

    public int FastTelemetryRequestCount { get; private set; }

    public int DiagnosticSnapshotRequestCount { get; private set; }

    public void EnqueueEventBatch(RuntimeEventBatchDto batch) => _eventBatches.Enqueue(batch);

    public Task<RuntimeStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Status);
    }

    public Task<TelemetrySnapshotDto> GetTelemetrySnapshotAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DiagnosticSnapshotRequestCount++;
        return Task.FromResult(new TelemetrySnapshotDto(Telemetry, [], null, []));
    }

    public Task<ThermalTelemetrySampleDto> GetTelemetrySampleAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FastTelemetryRequestCount++;
        return Task.FromResult(Telemetry);
    }

    public Task<PerformanceStateDto> GetPerformanceStateAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Performance);
    }

    public Task<FanStateDto> GetFanStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Fan);
    }

    public Task<RuntimeDoctorReportDto> GetDoctorAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Doctor);
    }

    public Task<RuntimeEventBatchDto> GetEventsAsync(
        long afterSequence,
        int maximumEvents,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestedEventCursors.Add(afterSequence);
        RuntimeEventBatchDto batch = _eventBatches.Count == 0
            ? new RuntimeEventBatchDto(Status, [], 0, 0, false)
            : _eventBatches.Dequeue();
        return Task.FromResult(batch);
    }

    public Task<IReadOnlyList<string>> ListBuiltInCurvesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>(["default"]);
    }

    public Task<StoredThermalCurveDocument> GetThermalCurveAsync(
        string name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RuntimeUiSampleData.DefaultCurve());
    }

    public Task<RuntimeCommandResultDto> ApplyPerformanceProfileAsync(
        ApplyPerformanceProfileRequest request,
        CancellationToken cancellationToken) => ForbiddenCommand<RuntimeCommandResultDto>();

    public Task<RuntimeCommandResultDto> ApplyFanProfileAsync(
        ApplyFanProfileRequest request,
        CancellationToken cancellationToken) => ForbiddenCommand<RuntimeCommandResultDto>();

    public Task<RuntimeStatusDto> StartThermalControlAsync(
        string curve,
        CancellationToken cancellationToken) => ForbiddenCommand<RuntimeStatusDto>();

    public Task<StopThermalControlResultDto> StopThermalControlAsync(
        CancellationToken cancellationToken) => ForbiddenCommand<StopThermalControlResultDto>();

    private static Task<T> ForbiddenCommand<T>() => Task.FromException<T>(
        new InvalidOperationException("This test double permits read-only operations only."));
}

internal static class UiTestWait
{
    public static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the asynchronous UI operation.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
