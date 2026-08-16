using System.Text.Json;

namespace BladeControl.Runtime;

public enum RuntimeThermalClientOutcome
{
    Stopped,
    CancelledAndStopped,
    StartRejected,
    RuntimeFaulted,
    RuntimeUnavailable,
    CommunicationFailed
}

public sealed record RuntimeThermalClientResult(
    RuntimeThermalClientOutcome Outcome,
    bool Succeeded,
    bool StartRequested,
    bool StopRequested,
    string Message,
    RuntimeStatusDto? FinalStatus);

public sealed class RuntimeThermalClient
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IRuntimeIpcClient _ipc;
    private readonly TimeSpan _pollInterval;

    public RuntimeThermalClient(
        IRuntimeIpcClient ipc,
        TimeSpan? pollInterval = null)
    {
        _ipc = ipc ?? throw new ArgumentNullException(nameof(ipc));
        _pollInterval = pollInterval ?? DefaultPollInterval;
        if (_pollInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }
    }

    public async Task<RuntimeThermalClientResult> RunAsync(
        string curve,
        Action<RuntimeStatusDto>? started = null,
        Action<RuntimeEventDto>? eventReceived = null,
        Action<RuntimeEventBatchDto>? batchReceived = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(curve);
        RuntimeIpcResponse start;
        try
        {
            start = await _ipc.SendAsync(
                RuntimeIpcOperation.StartThermalControl,
                new StartThermalControlRequest(curve),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCommunicationFailure(exception))
        {
            return new RuntimeThermalClientResult(
                RuntimeThermalClientOutcome.RuntimeUnavailable,
                false,
                true,
                false,
                "Runtime Core is not running. Start it with: " +
                "BladeControl.Cli service console",
                null);
        }

        if (!start.Succeeded)
        {
            return new RuntimeThermalClientResult(
                RuntimeThermalClientOutcome.StartRejected,
                false,
                true,
                false,
                start.Error ?? "Runtime Core rejected thermal control without a reason.",
                null);
        }

        RuntimeStatusDto status;
        try
        {
            status = ReadData<RuntimeStatusDto>(start);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return new RuntimeThermalClientResult(
                RuntimeThermalClientOutcome.CommunicationFailed,
                false,
                true,
                false,
                $"Runtime Core returned an invalid thermal-start response: {exception.Message}",
                null);
        }

        if (!status.State.Equals(nameof(RuntimeState.Running), StringComparison.Ordinal))
        {
            return TerminalResult(status, startRequested: true);
        }

        InvokeSafely(started, status);

        long afterSequence = 0;
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return await StopExactlyOnceAsync().ConfigureAwait(false);
            }

            RuntimeIpcResponse response;
            try
            {
                response = await _ipc.SendAsync(
                    RuntimeIpcOperation.GetRuntimeEvents,
                    new GetRuntimeEventsRequest(afterSequence),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsCommunicationFailure(exception))
            {
                return new RuntimeThermalClientResult(
                    RuntimeThermalClientOutcome.CommunicationFailed,
                    false,
                    true,
                    false,
                    $"Lost contact with Runtime Core: {exception.Message}",
                    status);
            }

            if (!response.Succeeded)
            {
                return new RuntimeThermalClientResult(
                    RuntimeThermalClientOutcome.CommunicationFailed,
                    false,
                    true,
                    false,
                    response.Error ?? "Runtime Core event polling failed without a reason.",
                    status);
            }

            RuntimeEventBatchDto batch;
            try
            {
                batch = ReadData<RuntimeEventBatchDto>(response);
            }
            catch (Exception exception) when (exception is JsonException or FormatException)
            {
                return new RuntimeThermalClientResult(
                    RuntimeThermalClientOutcome.CommunicationFailed,
                    false,
                    true,
                    false,
                    $"Runtime Core returned an invalid event batch: {exception.Message}",
                    status);
            }

            InvokeSafely(batchReceived, batch);
            foreach (RuntimeEventDto item in batch.Events)
            {
                if (item.Sequence <= afterSequence)
                {
                    continue;
                }

                InvokeSafely(eventReceived, item);
                afterSequence = item.Sequence;
            }

            status = batch.Status;
            if (!status.State.Equals(nameof(RuntimeState.Running), StringComparison.Ordinal))
            {
                return TerminalResult(status, startRequested: true);
            }

            bool moreRetainedEvents =
                batch.Events.Count == RuntimeIpcDispatcher.MaximumEventBatchSize &&
                afterSequence < batch.LatestAvailableSequence;
            if (moreRetainedEvents || _pollInterval == TimeSpan.Zero)
            {
                await Task.Yield();
                continue;
            }

            try
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await StopExactlyOnceAsync().ConfigureAwait(false);
            }
        }

        async Task<RuntimeThermalClientResult> StopExactlyOnceAsync()
        {
            RuntimeIpcResponse stop;
            try
            {
                stop = await _ipc.SendAsync(
                    RuntimeIpcOperation.StopThermalControl,
                    payload: null,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsCommunicationFailure(exception))
            {
                return new RuntimeThermalClientResult(
                    RuntimeThermalClientOutcome.CommunicationFailed,
                    false,
                    true,
                    true,
                    $"Runtime Core stop request failed: {exception.Message}",
                    status);
            }

            if (!stop.Succeeded)
            {
                return new RuntimeThermalClientResult(
                    RuntimeThermalClientOutcome.CommunicationFailed,
                    false,
                    true,
                    true,
                    stop.Error ?? "Runtime Core rejected the stop request without a reason.",
                    status);
            }

            StopThermalControlResultDto result;
            try
            {
                result = ReadData<StopThermalControlResultDto>(stop);
            }
            catch (Exception exception) when (exception is JsonException or FormatException)
            {
                return new RuntimeThermalClientResult(
                    RuntimeThermalClientOutcome.CommunicationFailed,
                    false,
                    true,
                    true,
                    $"Runtime Core returned an invalid stop response: {exception.Message}",
                    status);
            }

            return new RuntimeThermalClientResult(
                RuntimeThermalClientOutcome.CancelledAndStopped,
                result.Succeeded && result.FinalStatus.State.Equals(
                    nameof(RuntimeState.Stopped),
                    StringComparison.Ordinal),
                true,
                true,
                result.Message,
                result.FinalStatus);
        }
    }

    private static RuntimeThermalClientResult TerminalResult(
        RuntimeStatusDto status,
        bool startRequested)
    {
        bool stopped = status.State.Equals(nameof(RuntimeState.Stopped), StringComparison.Ordinal);
        return new RuntimeThermalClientResult(
            stopped
                ? RuntimeThermalClientOutcome.Stopped
                : RuntimeThermalClientOutcome.RuntimeFaulted,
            stopped && string.IsNullOrWhiteSpace(status.LastFailureReason),
            startRequested,
            false,
            status.LastFailureReason ?? status.EmergencyStatus ??
                (stopped
                    ? "Thermal control stopped through Runtime Core."
                    : $"Runtime Core entered {status.State}."),
            status);
    }

    private static T ReadData<T>(RuntimeIpcResponse response)
    {
        if (response.Data is T typed)
        {
            return typed;
        }

        if (response.Data is JsonElement element)
        {
            return element.Deserialize<T>() ??
                throw new FormatException($"IPC data for {typeof(T).Name} was empty.");
        }

        throw new FormatException($"IPC data was not a {typeof(T).Name} value.");
    }

    private static bool IsCommunicationFailure(Exception exception) =>
        exception is IOException or TimeoutException or OperationCanceledException or
            UnauthorizedAccessException;

    private static void InvokeSafely<T>(Action<T>? callback, T value)
    {
        if (callback is null)
        {
            return;
        }

        try
        {
            callback(value);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            // Rendering/observer failures must not alter Runtime Core behavior.
        }
    }
}
