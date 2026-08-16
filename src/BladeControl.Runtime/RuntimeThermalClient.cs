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
    RuntimeStatusDto? FinalStatus)
{
    public StopThermalControlResultDto? StopResult { get; init; }
}

public sealed class RuntimeThermalClient
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan DefaultFinalEventDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly IRuntimeIpcClient _ipc;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _finalEventDrainTimeout;

    public RuntimeThermalClient(
        IRuntimeIpcClient ipc,
        TimeSpan? pollInterval = null,
        TimeSpan? finalEventDrainTimeout = null)
    {
        _ipc = ipc ?? throw new ArgumentNullException(nameof(ipc));
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _finalEventDrainTimeout = finalEventDrainTimeout ?? DefaultFinalEventDrainTimeout;
        if (_pollInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        if (_finalEventDrainTimeout <= TimeSpan.Zero ||
            _finalEventDrainTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(finalEventDrainTimeout));
        }
    }

    public async Task<RuntimeThermalClientResult> RunAsync(
        string curve,
        Action<RuntimeStatusDto>? started = null,
        Action<RuntimeEventDto>? eventReceived = null,
        Action<RuntimeEventBatchDto>? batchReceived = null,
        CancellationToken cancellationToken = default,
        Action? stopping = null)
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

        Guid? targetSessionId = status.SessionId;
        long afterSequence = status.TotalEventCount > 0
            ? status.TotalEventCount - 1
            : 0;
        bool targetSessionStopped = false;
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

            EventBatchProgress progress = ProcessEventBatch(
                batch,
                afterSequence,
                targetSessionId,
                targetSessionStopped,
                eventReceived,
                batchReceived);
            if (progress.FailureReason is not null)
            {
                return CommunicationFailure(
                    progress.FailureReason,
                    status,
                    stopRequested: false);
            }

            afterSequence = progress.Cursor;
            targetSessionStopped = progress.TargetSessionStopped;
            status = batch.Status;
            if (progress.SessionTransition || IsDifferentActiveSession(status, targetSessionId))
            {
                return CommunicationFailure(
                    "Runtime event polling crossed into a different thermal session; " +
                    "the original session stream was not treated as current.",
                    status,
                    stopRequested: false);
            }

            if (!status.State.Equals(nameof(RuntimeState.Running), StringComparison.Ordinal))
            {
                return TerminalResult(status, startRequested: true);
            }

            bool moreRetainedEvents = afterSequence < batch.LatestAvailableSequence;
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
            InvokeSafely(stopping);
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

            return await DrainFinalEventsAsync(
                result,
                targetSessionId,
                afterSequence,
                targetSessionStopped,
                eventReceived,
                batchReceived).ConfigureAwait(false);
        }
    }

    private async Task<RuntimeThermalClientResult> DrainFinalEventsAsync(
        StopThermalControlResultDto stopResult,
        Guid? targetSessionId,
        long afterSequence,
        bool targetSessionStopped,
        Action<RuntimeEventDto>? eventReceived,
        Action<RuntimeEventBatchDto>? batchReceived)
    {
        RuntimeStatusDto status = stopResult.FinalStatus;
        using var timeout = new CancellationTokenSource(_finalEventDrainTimeout);
        while (true)
        {
            RuntimeIpcResponse response;
            try
            {
                response = await _ipc.SendAsync(
                    RuntimeIpcOperation.GetRuntimeEvents,
                    new GetRuntimeEventsRequest(
                        afterSequence,
                        RuntimeIpcDispatcher.MaximumEventBatchSize),
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return FinalDrainFailure(
                    $"Final Runtime event drain timed out after " +
                    $"{_finalEventDrainTimeout.TotalSeconds:F1} seconds. " +
                    "No second stop request was sent.",
                    status,
                    stopResult);
            }
            catch (Exception exception) when (IsCommunicationFailure(exception))
            {
                return FinalDrainFailure(
                    $"Runtime Core stop completed, but final event draining failed: " +
                    $"{exception.Message}",
                    status,
                    stopResult);
            }

            if (!response.Succeeded)
            {
                return FinalDrainFailure(
                    response.Error ??
                        "Runtime Core final event polling failed without a reason.",
                    status,
                    stopResult);
            }

            RuntimeEventBatchDto batch;
            try
            {
                batch = ReadData<RuntimeEventBatchDto>(response);
            }
            catch (Exception exception) when (exception is JsonException or FormatException)
            {
                return FinalDrainFailure(
                    $"Runtime Core returned an invalid final event batch: {exception.Message}",
                    status,
                    stopResult);
            }

            EventBatchProgress progress = ProcessEventBatch(
                batch,
                afterSequence,
                targetSessionId,
                targetSessionStopped,
                eventReceived,
                batchReceived);
            status = batch.Status;
            if (progress.FailureReason is not null)
            {
                return FinalDrainFailure(progress.FailureReason, status, stopResult);
            }

            afterSequence = progress.Cursor;
            targetSessionStopped = progress.TargetSessionStopped;
            if (progress.SessionTransition || IsDifferentActiveSession(status, targetSessionId))
            {
                return FinalDrainFailure(
                    "A different thermal session started during the final event drain; " +
                    "the target session could not be proven fully caught up.",
                    status,
                    stopResult);
            }

            bool caughtUp = afterSequence >= batch.LatestAvailableSequence;
            bool runtimeStopped = status.State.Equals(
                nameof(RuntimeState.Stopped),
                StringComparison.Ordinal);
            if (caughtUp && (targetSessionStopped || runtimeStopped))
            {
                return new RuntimeThermalClientResult(
                    RuntimeThermalClientOutcome.CancelledAndStopped,
                    stopResult.Succeeded && runtimeStopped,
                    true,
                    true,
                    stopResult.Message,
                    status)
                {
                    StopResult = stopResult
                };
            }

            if (!caughtUp)
            {
                await Task.Yield();
                continue;
            }

            TimeSpan wait = _pollInterval > TimeSpan.Zero
                ? _pollInterval
                : TimeSpan.FromMilliseconds(10);
            try
            {
                await Task.Delay(wait, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return FinalDrainFailure(
                    $"Final Runtime event drain timed out after " +
                    $"{_finalEventDrainTimeout.TotalSeconds:F1} seconds. " +
                    "No second stop request was sent.",
                    status,
                    stopResult);
            }
        }
    }

    private static EventBatchProgress ProcessEventBatch(
        RuntimeEventBatchDto batch,
        long cursor,
        Guid? targetSessionId,
        bool targetSessionStopped,
        Action<RuntimeEventDto>? eventReceived,
        Action<RuntimeEventBatchDto>? batchReceived)
    {
        if (batch.Events is null)
        {
            return EventBatchProgress.Failed(cursor, targetSessionStopped,
                "Runtime Core returned an event batch with no event collection.");
        }

        if (batch.Events.Count > RuntimeIpcDispatcher.MaximumEventBatchSize)
        {
            return EventBatchProgress.Failed(cursor, targetSessionStopped,
                "Runtime Core returned more than the bounded maximum event batch size.");
        }

        if (batch.OldestAvailableSequence < 0 || batch.LatestAvailableSequence < 0 ||
            batch.OldestAvailableSequence > batch.LatestAvailableSequence ||
            (batch.OldestAvailableSequence == 0) !=
                (batch.LatestAvailableSequence == 0) ||
            batch.LatestAvailableSequence < cursor ||
            batch.Status.TotalEventCount < batch.LatestAvailableSequence)
        {
            return EventBatchProgress.Failed(cursor, targetSessionStopped,
                "Runtime Core event cursor metadata moved backwards or was inconsistent; " +
                "the runtime may have restarted.");
        }

        long previousSequence = 0;
        foreach (RuntimeEventDto item in batch.Events)
        {
            if (item.Sequence <= 0 ||
                (previousSequence > 0 && item.Sequence < previousSequence) ||
                item.Sequence < batch.OldestAvailableSequence ||
                item.Sequence > batch.LatestAvailableSequence)
            {
                return EventBatchProgress.Failed(cursor, targetSessionStopped,
                    "Runtime Core returned an invalid or out-of-order event sequence.");
            }

            previousSequence = item.Sequence;
        }

        InvokeSafely(batchReceived, batch);
        bool sessionTransition = false;
        foreach (RuntimeEventDto item in batch.Events)
        {
            if (item.Sequence <= cursor)
            {
                continue;
            }

            if (item.Kind.Equals(nameof(RuntimeEventKind.SessionStarted),
                    StringComparison.Ordinal) &&
                targetSessionId.HasValue &&
                item.SessionId != targetSessionId)
            {
                sessionTransition = true;
                break;
            }

            InvokeSafely(eventReceived, item);
            cursor = item.Sequence;
            if (item.Kind.Equals(nameof(RuntimeEventKind.SessionStopped),
                    StringComparison.Ordinal) &&
                (!targetSessionId.HasValue || item.SessionId == targetSessionId))
            {
                targetSessionStopped = true;
            }
        }

        return new EventBatchProgress(
            cursor,
            targetSessionStopped,
            sessionTransition,
            FailureReason: null);
    }

    private static RuntimeThermalClientResult CommunicationFailure(
        string message,
        RuntimeStatusDto? status,
        bool stopRequested) => new(
            RuntimeThermalClientOutcome.CommunicationFailed,
            false,
            true,
            stopRequested,
            message,
            status);

    private static bool IsDifferentActiveSession(
        RuntimeStatusDto status,
        Guid? targetSessionId) =>
        targetSessionId.HasValue &&
        status.State.Equals(nameof(RuntimeState.Running), StringComparison.Ordinal) &&
        status.SessionId != targetSessionId;

    private static RuntimeThermalClientResult FinalDrainFailure(
        string message,
        RuntimeStatusDto status,
        StopThermalControlResultDto stopResult) =>
        CommunicationFailure(message, status, stopRequested: true) with
        {
            StopResult = stopResult
        };

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

    private static void InvokeSafely(Action? callback)
    {
        if (callback is null)
        {
            return;
        }

        try
        {
            callback();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            // Rendering/observer failures must not alter Runtime Core behavior.
        }
    }

    private sealed record EventBatchProgress(
        long Cursor,
        bool TargetSessionStopped,
        bool SessionTransition,
        string? FailureReason)
    {
        internal static EventBatchProgress Failed(
            long cursor,
            bool targetSessionStopped,
            string reason) => new(
                cursor,
                targetSessionStopped,
                SessionTransition: false,
                reason);
    }
}
