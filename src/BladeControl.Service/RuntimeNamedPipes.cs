using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using BladeControl.Ipc;
using BladeControl.Runtime;

namespace BladeControl.Service;

public sealed class RuntimeNamedPipeServer
{
    /// <summary>
    /// Kept as an alias so existing callers and tests still compile; the endpoint identity
    /// itself now lives in BladeControl.Ipc where both ends of the channel can read it.
    /// </summary>
    public const string PipeName = RuntimeIpcEndpoint.PipeName;

    private readonly RuntimeIpcDispatcher _dispatcher;

    public RuntimeNamedPipeServer(RuntimeIpcDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // CurrentUserOnly is deliberately not used: under the SCM the server runs as
            // LocalSystem while clients are interactive users, so that option would both
            // lock out the UI and, on the client side, assert an identity match that no
            // longer holds. RuntimePipeSecurity applies an explicit DACL instead, at
            // creation time so the pipe is never briefly world-writable.
            await using var pipe = RuntimePipeSecurity.CreateServerStream(
                RuntimeIpcEndpoint.PipeName,
                maximumServerInstances: 1);
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            await ProcessConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        if (!IsLocalClient(pipe))
        {
            return;
        }

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

        RuntimeIpcResponse response;
        try
        {
            string json = await ReadBoundedMessageAsync(reader, cancellationToken)
                .ConfigureAwait(false);
            RuntimeIpcRequest request = RuntimeIpcDispatcher.ParseRequest(json);
            response = await _dispatcher.DispatchAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FormatException or
            DecoderFallbackException or IOException)
        {
            response = new RuntimeIpcResponse(
                RuntimeIpcDispatcher.ProtocolVersion,
                Guid.Empty,
                false,
                null,
                exception.Message);
        }

        string responseJson = RuntimeIpcDispatcher.SerializeResponse(response);
        if (Encoding.UTF8.GetByteCount(responseJson) >
            RuntimeIpcDispatcher.MaximumMessageBytes)
        {
            response = new RuntimeIpcResponse(
                RuntimeIpcDispatcher.ProtocolVersion,
                response.RequestId,
                false,
                null,
                "IPC response exceeded the 64-KiB limit.");
            responseJson = RuntimeIpcDispatcher.SerializeResponse(response);
        }

        await writer.WriteLineAsync(responseJson).ConfigureAwait(false);
    }

    private static bool IsLocalClient(NamedPipeServerStream pipe)
    {
        const int capacity = 256;
        var computerName = new StringBuilder(capacity);
        bool remoteNameReturned = NativePipeMethods.GetNamedPipeClientComputerNameW(
            pipe.SafePipeHandle,
            computerName,
            checked((uint)(capacity * sizeof(char))));
        return !remoteNameReturned &&
            Marshal.GetLastWin32Error() == NativePipeMethods.ErrorPipeLocal;
    }

    private static async Task<string> ReadBoundedMessageAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            int newline = Array.IndexOf(buffer, '\n', 0, read);
            int count = newline >= 0 ? newline : read;
            builder.Append(buffer, 0, count);
            if (Encoding.UTF8.GetByteCount(builder.ToString()) >
                RuntimeIpcDispatcher.MaximumMessageBytes)
            {
                throw new FormatException("IPC message exceeds the 64-KiB limit.");
            }

            if (newline >= 0)
            {
                break;
            }
        }

        if (builder.Length == 0)
        {
            throw new FormatException("IPC request is empty.");
        }

        return builder.ToString().TrimEnd('\r');
    }
}

public sealed class NamedPipeRuntimeIpcClient : IRuntimeIpcClient
{
    private readonly int _connectTimeoutMilliseconds;

    public NamedPipeRuntimeIpcClient(int connectTimeoutMilliseconds = 2000)
    {
        if (connectTimeoutMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeoutMilliseconds));
        }

        _connectTimeoutMilliseconds = connectTimeoutMilliseconds;
    }

    public async Task<RuntimeIpcResponse> SendAsync(
        RuntimeIpcOperation operation,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        using var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: RuntimeNamedPipeServer.PipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(_connectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true
        };
        JsonElement? element = payload is null
            ? null
            : JsonSerializer.SerializeToElement(payload);
        var request = new RuntimeIpcRequest(
            RuntimeIpcDispatcher.ProtocolVersion,
            Guid.NewGuid(),
            operation,
            element);
        string json = JsonSerializer.Serialize(request, new JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        });
        await writer.WriteLineAsync(json).ConfigureAwait(false);
        string? responseJson = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            throw new IOException("Runtime pipe returned no response.");
        }

        if (Encoding.UTF8.GetByteCount(responseJson) >
            RuntimeIpcDispatcher.MaximumMessageBytes)
        {
            throw new IOException("Runtime pipe response exceeds the 64-KiB limit.");
        }

        RuntimeIpcResponse response = JsonSerializer.Deserialize<RuntimeIpcResponse>(responseJson) ??
            throw new IOException("Runtime pipe returned an invalid response.");
        if (response.Version != RuntimeIpcDispatcher.ProtocolVersion ||
            response.RequestId != request.RequestId)
        {
            throw new IOException("Runtime pipe returned a mismatched response envelope.");
        }

        return response;
    }
}

public static class RuntimePipeClient
{
    public static Task<RuntimeIpcResponse> SendAsync(
        RuntimeIpcOperation operation,
        object? payload = null,
        int connectTimeoutMilliseconds = 2000,
        CancellationToken cancellationToken = default) =>
        new NamedPipeRuntimeIpcClient(connectTimeoutMilliseconds).SendAsync(
            operation,
            payload,
            cancellationToken);
}
