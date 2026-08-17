using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BladeControl.Runtime;

namespace BladeControl.UI.Ipc;

/// <summary>
/// Production IPC client. Speaks the existing typed Runtime Core protocol over the local
/// named pipe and does nothing else: it never touches hardware, never starts a host, and
/// never falls back to synthetic data.
/// </summary>
/// <remarks>
/// The runtime pipe server is created with <c>maxNumberOfServerInstances: 1</c> and serves
/// exactly one request per connection, so every request here is funnelled through a single
/// gate. Concurrent requests would otherwise race for the single server instance and time
/// out against each other rather than against a genuinely absent runtime.
/// </remarks>
public sealed class NamedPipeRuntimeUiClient : IRuntimeUiClient, IDisposable
{
    /// <summary>
    /// Mirrors <c>BladeControl.Service.RuntimeNamedPipeServer.PipeName</c>. The constant is
    /// duplicated because BladeControl.Service transitively references
    /// LibreHardwareMonitorLib and the hardware provider assembly, which must never be
    /// loaded into the GUI process. Tracked in docs/gui-backend-needs.md (item 1).
    /// </summary>
    public const string PipeName = "BladeControl.Runtime.v1";

    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions ResponseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _connectTimeoutMilliseconds;
    private bool _disposed;

    public NamedPipeRuntimeUiClient(int connectTimeoutMilliseconds = 1500)
    {
        if (connectTimeoutMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeoutMilliseconds));
        }

        _connectTimeoutMilliseconds = connectTimeoutMilliseconds;
    }

    public bool IsLiveRuntimeChannel => true;

    public Task<RuntimeStatusDto> GetStatusAsync(CancellationToken cancellationToken) =>
        SendAsync<RuntimeStatusDto>(
            RuntimeIpcOperation.GetRuntimeStatus,
            payload: null,
            cancellationToken);

    public Task<ThermalTelemetrySampleDto> GetTelemetrySampleAsync(
        CancellationToken cancellationToken) =>
        SendAsync<ThermalTelemetrySampleDto>(
            RuntimeIpcOperation.GetTelemetrySample,
            payload: null,
            cancellationToken);

    public Task<TelemetrySnapshotDto> GetTelemetrySnapshotAsync(
        CancellationToken cancellationToken) =>
        SendAsync<TelemetrySnapshotDto>(
            RuntimeIpcOperation.GetTelemetrySnapshot,
            payload: null,
            cancellationToken);

    public Task<PerformanceStateDto> GetPerformanceStateAsync(
        CancellationToken cancellationToken) =>
        SendAsync<PerformanceStateDto>(
            RuntimeIpcOperation.GetPerformanceState,
            payload: null,
            cancellationToken);

    public Task<FanStateDto> GetFanStateAsync(CancellationToken cancellationToken) =>
        SendAsync<FanStateDto>(RuntimeIpcOperation.GetFanState, payload: null, cancellationToken);

    public Task<RuntimeDoctorReportDto> GetDoctorAsync(CancellationToken cancellationToken) =>
        SendAsync<RuntimeDoctorReportDto>(
            RuntimeIpcOperation.GetRuntimeDoctor,
            payload: null,
            cancellationToken);

    public Task<RuntimeEventBatchDto> GetEventsAsync(
        long afterSequence,
        int maximumEvents,
        CancellationToken cancellationToken) =>
        SendAsync<RuntimeEventBatchDto>(
            RuntimeIpcOperation.GetRuntimeEvents,
            new GetRuntimeEventsRequest(afterSequence, maximumEvents),
            cancellationToken);

    public async Task<IReadOnlyList<string>> ListBuiltInCurvesAsync(
        CancellationToken cancellationToken) =>
        await SendAsync<string[]>(
            RuntimeIpcOperation.ListBuiltInCurves,
            payload: null,
            cancellationToken).ConfigureAwait(false);

    public Task<StoredThermalCurveDocument> GetThermalCurveAsync(
        string name,
        CancellationToken cancellationToken) =>
        SendAsync<StoredThermalCurveDocument>(
            RuntimeIpcOperation.GetThermalCurve,
            new GetThermalCurveRequest(name),
            cancellationToken);

    public Task<RuntimeCommandResultDto> ApplyPerformanceProfileAsync(
        ApplyPerformanceProfileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<RuntimeCommandResultDto>(
            RuntimeIpcOperation.ApplyPerformanceProfile,
            request,
            cancellationToken);
    }

    public Task<RuntimeCommandResultDto> ApplyFanProfileAsync(
        ApplyFanProfileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<RuntimeCommandResultDto>(
            RuntimeIpcOperation.ApplyFanProfile,
            request,
            cancellationToken);
    }

    public Task<RuntimeStatusDto> StartThermalControlAsync(
        string curve,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(curve);
        return SendAsync<RuntimeStatusDto>(
            RuntimeIpcOperation.StartThermalControl,
            new StartThermalControlRequest(curve),
            cancellationToken);
    }

    public Task<StopThermalControlResultDto> StopThermalControlAsync(
        CancellationToken cancellationToken) =>
        SendAsync<StopThermalControlResultDto>(
            RuntimeIpcOperation.StopThermalControl,
            payload: null,
            cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private async Task<T> SendAsync<T>(
        RuntimeIpcOperation operation,
        object? payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RuntimeIpcResponse response = await ExchangeAsync(
                operation,
                payload,
                cancellationToken).ConfigureAwait(false);
            if (!response.Succeeded)
            {
                throw new RuntimeUiException(
                    RuntimeUiFailureKind.Rejected,
                    response.Error ??
                    $"Runtime Core rejected {operation} without a reason.");
            }

            return ReadData<T>(response, operation);
        }
        finally
        {
            if (!_disposed)
            {
                _gate.Release();
            }
        }
    }

    private async Task<RuntimeIpcResponse> ExchangeAsync(
        RuntimeIpcOperation operation,
        object? payload,
        CancellationToken cancellationToken)
    {
        var request = new RuntimeIpcRequest(
            RuntimeIpcDispatcher.ProtocolVersion,
            Guid.NewGuid(),
            operation,
            payload is null ? null : JsonSerializer.SerializeToElement(payload, RequestOptions));
        string requestJson = JsonSerializer.Serialize(request, RequestOptions);
        if (Encoding.UTF8.GetByteCount(requestJson) > RuntimeIpcDispatcher.MaximumMessageBytes)
        {
            throw new RuntimeUiException(
                RuntimeUiFailureKind.Protocol,
                $"The {operation} request exceeds the 64-KiB IPC limit.");
        }

        string? responseJson;
        try
        {
            using var pipe = new NamedPipeClientStream(
                serverName: ".",
                pipeName: PipeName,
                direction: PipeDirection.InOut,
                options: PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(_connectTimeoutMilliseconds, cancellationToken)
                .ConfigureAwait(false);
            using var reader = new StreamReader(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);
            using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 4096,
                leaveOpen: true)
            {
                AutoFlush = true
            };
            await writer.WriteLineAsync(requestJson.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            responseJson = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or
            UnauthorizedAccessException or ObjectDisposedException or
            OperationCanceledException)
        {
            throw new RuntimeUiException(
                RuntimeUiFailureKind.Transport,
                $"Runtime Core is not reachable on pipe '{PipeName}': {exception.Message}",
                exception);
        }

        if (string.IsNullOrWhiteSpace(responseJson))
        {
            throw new RuntimeUiException(
                RuntimeUiFailureKind.Transport,
                "Runtime Core closed the pipe without answering.");
        }

        if (Encoding.UTF8.GetByteCount(responseJson) > RuntimeIpcDispatcher.MaximumMessageBytes)
        {
            throw new RuntimeUiException(
                RuntimeUiFailureKind.Protocol,
                "Runtime Core response exceeds the 64-KiB IPC limit.");
        }

        RuntimeIpcResponse response;
        try
        {
            response = JsonSerializer.Deserialize<RuntimeIpcResponse>(
                responseJson,
                ResponseOptions) ??
                throw new RuntimeUiException(
                    RuntimeUiFailureKind.Protocol,
                    "Runtime Core returned an empty response envelope.");
        }
        catch (JsonException exception)
        {
            throw new RuntimeUiException(
                RuntimeUiFailureKind.Protocol,
                $"Runtime Core returned an unreadable response: {exception.Message}",
                exception);
        }

        if (response.Version != RuntimeIpcDispatcher.ProtocolVersion ||
            response.RequestId != request.RequestId)
        {
            throw new RuntimeUiException(
                RuntimeUiFailureKind.Protocol,
                "Runtime Core returned a mismatched response envelope.");
        }

        return response;
    }

    private static T ReadData<T>(RuntimeIpcResponse response, RuntimeIpcOperation operation)
    {
        if (response.Data is T typed)
        {
            return typed;
        }

        if (response.Data is JsonElement element)
        {
            try
            {
                return element.Deserialize<T>(ResponseOptions) ??
                    throw new RuntimeUiException(
                        RuntimeUiFailureKind.Protocol,
                        $"Runtime Core returned no {operation} payload.");
            }
            catch (JsonException exception)
            {
                throw new RuntimeUiException(
                    RuntimeUiFailureKind.Protocol,
                    $"Runtime Core returned an unreadable {operation} payload: " +
                    exception.Message,
                    exception);
            }
        }

        throw new RuntimeUiException(
            RuntimeUiFailureKind.Protocol,
            $"Runtime Core returned no {operation} payload.");
    }
}
