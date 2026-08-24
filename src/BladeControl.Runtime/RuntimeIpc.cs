using System.Text.Json;
using System.Text.Json.Serialization;
using BladeControl.Razer;
using BladeControl.Thermal;

namespace BladeControl.Runtime;

public enum RuntimeIpcOperation
{
    GetRuntimeStatus,
    GetRuntimeDoctor,
    GetTelemetrySnapshot,
    GetPerformanceState,
    ApplyPerformanceProfile,
    GetFanState,
    ApplyFanProfile,
    StartThermalControl,
    StopThermalControl,
    GetRuntimeEvents,
    GetThermalCurve,
    ListBuiltInCurves,
    GetTelemetrySample
}

public sealed record RuntimeIpcRequest(
    int Version,
    Guid RequestId,
    RuntimeIpcOperation Operation,
    JsonElement? Payload);

public sealed record RuntimeIpcResponse(
    int Version,
    Guid RequestId,
    bool Succeeded,
    object? Data,
    string? Error);

public sealed record ApplyPerformanceProfileRequest(
    string Mode,
    string? CpuLevel,
    string? GpuLevel);

public sealed record ApplyFanProfileRequest(
    string Mode,
    int? Fan1Rpm,
    int? Fan2Rpm);

public sealed record StartThermalControlRequest(string Curve);

public sealed record GetThermalCurveRequest(string Name);

public sealed record GetRuntimeEventsRequest(
    long AfterSequence,
    int MaximumEvents = RuntimeIpcDispatcher.MaximumEventBatchSize);

public interface IRuntimeIpcClient
{
    Task<RuntimeIpcResponse> SendAsync(
        RuntimeIpcOperation operation,
        object? payload = null,
        CancellationToken cancellationToken = default);
}

public sealed class RuntimeIpcDispatcher : IAsyncDisposable
{
    public const int ProtocolVersion = 1;
    public const int MaximumMessageBytes = 64 * 1024;
    public const int MaximumEventBatchSize = 16;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _sync = new();
    private readonly BladeRuntime _runtime;
    private readonly Func<object?> _doctorReport;
    private CancellationTokenSource? _thermalCancellation;
    private Task? _thermalTask;

    public RuntimeIpcDispatcher(BladeRuntime runtime, Func<object?>? doctorReport = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _doctorReport = doctorReport ?? (() => null);
    }

    public static RuntimeIpcRequest ParseRequest(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumMessageBytes)
        {
            throw new FormatException("IPC message exceeds the 64-KiB limit.");
        }

        try
        {
            RuntimeIpcRequest request = JsonSerializer.Deserialize<RuntimeIpcRequest>(json, Options) ??
                throw new FormatException("IPC request is empty.");
            if (request.Version != ProtocolVersion || request.RequestId == Guid.Empty)
            {
                throw new FormatException("IPC version or request ID is invalid.");
            }

            return request;
        }
        catch (JsonException exception)
        {
            throw new FormatException($"Malformed IPC request: {exception.Message}", exception);
        }
    }

    public static string SerializeResponse(RuntimeIpcResponse response) =>
        JsonSerializer.Serialize(response, Options);

    public async ValueTask<RuntimeIpcResponse> DispatchAsync(
        RuntimeIpcRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            object? data = request.Operation switch
            {
                RuntimeIpcOperation.GetRuntimeStatus =>
                    RuntimeIpcDtoMapper.ToSummaryDto(_runtime.GetStatus()),
                RuntimeIpcOperation.GetRuntimeDoctor => _doctorReport(),
                RuntimeIpcOperation.GetTelemetrySnapshot =>
                    RuntimeIpcDtoMapper.ToDto(_runtime.GetDiagnosticSnapshot()),
                RuntimeIpcOperation.GetTelemetrySample =>
                    RuntimeIpcDtoMapper.ToDto(_runtime.GetTelemetrySample()),
                RuntimeIpcOperation.GetPerformanceState =>
                    RuntimeIpcDtoMapper.ToDto(_runtime.GetPerformanceState()),
                RuntimeIpcOperation.ApplyPerformanceProfile => ApplyPerformance(request),
                RuntimeIpcOperation.GetFanState =>
                    RuntimeIpcDtoMapper.ToDto(_runtime.GetFanState()),
                RuntimeIpcOperation.ApplyFanProfile => ApplyFan(request),
                RuntimeIpcOperation.StartThermalControl => StartThermal(request, cancellationToken),
                RuntimeIpcOperation.StopThermalControl => await StopThermalAsync().ConfigureAwait(false),
                RuntimeIpcOperation.GetRuntimeEvents => GetRuntimeEvents(request),
                RuntimeIpcOperation.GetThermalCurve => GetThermalCurve(request),
                RuntimeIpcOperation.ListBuiltInCurves => new[] { "default" },
                _ => throw new NotSupportedException(
                    $"IPC operation '{request.Operation}' is not supported.")
            };
            return new RuntimeIpcResponse(
                ProtocolVersion,
                request.RequestId,
                true,
                data,
                null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            return new RuntimeIpcResponse(
                ProtocolVersion,
                request.RequestId,
                false,
                null,
                exception.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopThermalAsync().ConfigureAwait(false);
        _thermalCancellation?.Dispose();
    }

    private object ApplyPerformance(RuntimeIpcRequest request)
    {
        ApplyPerformanceProfileRequest payload = ReadPayload<ApplyPerformanceProfileRequest>(request);
        PerformanceProfile profile = payload.Mode switch
        {
            "Balanced" => PerformanceProfile.Balanced,
            "Silent" => PerformanceProfile.Silent,
            "Custom" => PerformanceProfile.Custom(
                ParseCpuLevel(payload.CpuLevel),
                ParseGpuLevel(payload.GpuLevel)),
            _ => throw new FormatException("Unsupported performance profile mode.")
        };
        PerformanceApplyResult result = _runtime.ApplyPerformanceProfile(profile);
        return new
        {
            result.Succeeded,
            Outcome = result.Outcome.ToString(),
            result.Verification.Message
        };
    }

    private object ApplyFan(RuntimeIpcRequest request)
    {
        ApplyFanProfileRequest payload = ReadPayload<ApplyFanProfileRequest>(request);
        FanControlProfile profile = payload.Mode switch
        {
            "Auto" when payload.Fan1Rpm is null && payload.Fan2Rpm is null =>
                FanControlProfile.Auto,
            "Fixed" when payload.Fan1Rpm.HasValue && payload.Fan2Rpm.HasValue =>
                FanControlProfile.Fixed(
                    new FanRpm(payload.Fan1Rpm.Value),
                    new FanRpm(payload.Fan2Rpm.Value)),
            _ => throw new FormatException("Fan profile payload is invalid.")
        };
        FanControlApplyResult result = _runtime.ApplyFanProfile(profile);
        return new
        {
            result.Succeeded,
            Outcome = result.Outcome.ToString(),
            result.Verification.Message
        };
    }

    private object StartThermal(RuntimeIpcRequest request, CancellationToken hostCancellation)
    {
        StartThermalControlRequest payload = ReadPayload<StartThermalControlRequest>(request);
        if (!payload.Curve.Equals("default", StringComparison.Ordinal))
        {
            throw new FormatException("Only the immutable built-in 'default' curve is available.");
        }

        lock (_sync)
        {
            if (_thermalTask is { IsCompleted: false })
            {
                throw new RuntimeOwnershipException("Thermal control is already running.");
            }

            _runtime.StartThermalControl();

            // A previous session's source can still be here when a stop failed partway through,
            // and overwriting it would strand its registration on the host token. That token
            // lives as long as the service does, so a linked source that is never disposed is
            // never collected either — small, permanent, and accumulating across restarts of a
            // session in a process designed to run for months.
            _thermalCancellation?.Dispose();
            _thermalCancellation = CancellationTokenSource.CreateLinkedTokenSource(hostCancellation);
            _thermalTask = Task.Run(
                async () => await _runtime.RunScheduledAsync(_thermalCancellation.Token)
                    .ConfigureAwait(false),
                CancellationToken.None);
        }

        return RuntimeIpcDtoMapper.ToSummaryDto(_runtime.GetStatus());
    }

    private async ValueTask<object?> StopThermalAsync()
    {
        CancellationTokenSource? cancellation;
        Task? task;
        lock (_sync)
        {
            cancellation = _thermalCancellation;
            task = _thermalTask;
            cancellation?.Cancel();
        }

        ThermalSessionResult? result;
        try
        {
            if (task is not null)
            {
                await task.ConfigureAwait(false);
            }

            result = await _runtime.StopThermalControlAsync().ConfigureAwait(false);
        }
        finally
        {
            // In a finally because the session is over either way. Leaving the source behind on
            // a failed stop is what allowed it to be overwritten and stranded later, and
            // leaving the completed task behind makes a subsequent stop await a task belonging
            // to a session that has already ended.
            lock (_sync)
            {
                if (ReferenceEquals(_thermalTask, task))
                {
                    _thermalTask = null;
                }

                if (ReferenceEquals(_thermalCancellation, cancellation))
                {
                    _thermalCancellation?.Dispose();
                    _thermalCancellation = null;
                }
            }
        }

        RuntimeStatus status = _runtime.GetStatus();
        return result is null
            ? new StopThermalControlResultDto(
                false,
                status.State == RuntimeState.Stopped,
                "No thermal-control session was active.",
                RuntimeIpcDtoMapper.ToSummaryDto(status))
            : new StopThermalControlResultDto(
                true,
                result.Succeeded,
                result.Message,
                RuntimeIpcDtoMapper.ToSummaryDto(status));
    }

    private object GetRuntimeEvents(RuntimeIpcRequest request)
    {
        GetRuntimeEventsRequest payload = ReadPayload<GetRuntimeEventsRequest>(request);
        if (payload.AfterSequence < 0 ||
            payload.MaximumEvents is < 1 or > MaximumEventBatchSize)
        {
            throw new FormatException(
                $"Runtime event cursor must be non-negative and MaximumEvents must be " +
                $"1..{MaximumEventBatchSize}.");
        }

        return RuntimeIpcDtoMapper.ToEventBatch(
            _runtime.GetStatus(),
            payload.AfterSequence,
            payload.MaximumEvents);
    }

    private static object GetThermalCurve(RuntimeIpcRequest request)
    {
        GetThermalCurveRequest payload = ReadPayload<GetThermalCurveRequest>(request);
        if (!payload.Name.Equals("default", StringComparison.Ordinal))
        {
            throw new FormatException("Unknown thermal curve.");
        }

        return RuntimeConfigurationStore.BuiltInDefault;
    }

    private static T ReadPayload<T>(RuntimeIpcRequest request)
    {
        if (request.Payload is null || request.Payload.Value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"{request.Operation} requires a typed object payload.");
        }

        try
        {
            return request.Payload.Value.Deserialize<T>(Options) ??
                throw new FormatException("IPC payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new FormatException($"Malformed IPC payload: {exception.Message}", exception);
        }
    }

    private static RazerCpuPerformanceLevel ParseCpuLevel(string? value) => value switch
    {
        "Low" => RazerCpuPerformanceLevel.Low,
        "Medium" => RazerCpuPerformanceLevel.Medium,
        _ => throw new FormatException("CPU level must be Low or Medium.")
    };

    private static RazerGpuPerformanceLevel ParseGpuLevel(string? value) => value switch
    {
        "Low" => RazerGpuPerformanceLevel.Low,
        _ => throw new FormatException("GPU level must be Low.")
    };

}
