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
    GetThermalCurve,
    ListBuiltInCurves
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

public sealed class RuntimeIpcDispatcher : IAsyncDisposable
{
    public const int ProtocolVersion = 1;
    public const int MaximumMessageBytes = 64 * 1024;

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
                    RuntimeIpcDtoMapper.ToDto(_runtime.GetStatus()),
                RuntimeIpcOperation.GetRuntimeDoctor => _doctorReport(),
                RuntimeIpcOperation.GetTelemetrySnapshot =>
                    RuntimeIpcDtoMapper.ToDto(_runtime.GetDiagnosticSnapshot()),
                RuntimeIpcOperation.GetPerformanceState =>
                    RuntimeIpcDtoMapper.ToDto(_runtime.GetPerformanceState()),
                RuntimeIpcOperation.ApplyPerformanceProfile => ApplyPerformance(request),
                RuntimeIpcOperation.GetFanState =>
                    RuntimeIpcDtoMapper.ToDto(_runtime.GetFanState()),
                RuntimeIpcOperation.ApplyFanProfile => ApplyFan(request),
                RuntimeIpcOperation.StartThermalControl => StartThermal(request, cancellationToken),
                RuntimeIpcOperation.StopThermalControl => await StopThermalAsync().ConfigureAwait(false),
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
            _thermalCancellation = CancellationTokenSource.CreateLinkedTokenSource(hostCancellation);
            _thermalTask = Task.Run(
                async () => await _runtime.RunScheduledAsync(_thermalCancellation.Token)
                    .ConfigureAwait(false),
                CancellationToken.None);
        }

        return RuntimeIpcDtoMapper.ToDto(_runtime.GetStatus());
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

        if (task is not null)
        {
            await task.ConfigureAwait(false);
        }

        ThermalSessionResult? result = await _runtime.StopThermalControlAsync().ConfigureAwait(false);
        lock (_sync)
        {
            _thermalTask = null;
            _thermalCancellation?.Dispose();
            _thermalCancellation = null;
        }

        return result is null
            ? null
            : new { result.Succeeded, result.Message, State = result.State.ToString() };
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
