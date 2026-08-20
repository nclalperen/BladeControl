using BladeControl.Runtime;

namespace BladeControl.Runtime.Tests;

[TestClass]
public sealed class RuntimeThermalClientTests
{
    private static readonly Guid SessionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [TestMethod]
    public async Task CancellationSendsExactlyOneTypedStopAndHasNoDirectOwner()
    {
        var ipc = new ScriptedIpcClient((operation, _, _) => operation switch
        {
            RuntimeIpcOperation.StartThermalControl => Success(Status("Running")),
            RuntimeIpcOperation.StopThermalControl => Success(new StopThermalControlResultDto(
                true,
                true,
                "safe shutdown complete",
                Status("Stopped", totalEventCount: 1))),
            RuntimeIpcOperation.GetRuntimeEvents => Success(new RuntimeEventBatchDto(
                Status("Stopped", totalEventCount: 1),
                [Event("SessionStopped", 1)],
                1,
                1,
                false)),
            _ => throw new AssertFailedException($"Unexpected operation {operation}.")
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new RuntimeThermalClient(ipc, TimeSpan.Zero);

        RuntimeThermalClientResult result = await client.RunAsync(
            "default",
            cancellationToken: cancellation.Token);

        CollectionAssert.AreEqual(
            new[]
            {
                RuntimeIpcOperation.StartThermalControl,
                RuntimeIpcOperation.StopThermalControl,
                RuntimeIpcOperation.GetRuntimeEvents
            },
            ipc.Operations);
        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.StopRequested);
        Assert.AreEqual(1,
            ipc.Operations.Count(item => item == RuntimeIpcOperation.StopThermalControl));
        AssertNoDirectHardwareDependencies(client);
    }

    [TestMethod]
    public async Task RuntimeUnavailableFailsWithoutDirectFallback()
    {
        var ipc = new ScriptedIpcClient((_, _, _) => throw new IOException("no pipe"));
        var client = new RuntimeThermalClient(ipc, TimeSpan.Zero);

        RuntimeThermalClientResult result = await client.RunAsync("default");

        Assert.AreEqual(RuntimeThermalClientOutcome.RuntimeUnavailable, result.Outcome);
        StringAssert.Contains(result.Message, "Runtime Core is not running");
        CollectionAssert.AreEqual(
            new[] { RuntimeIpcOperation.StartThermalControl },
            ipc.Operations);
        Assert.IsFalse(result.StopRequested);
        AssertNoDirectHardwareDependencies(client);
    }

    [TestMethod]
    public async Task StartFailureIsSurfacedWithoutRetryOrStop()
    {
        var ipc = new ScriptedIpcClient((operation, _, _) => new RuntimeIpcResponse(
            1,
            Guid.NewGuid(),
            false,
            null,
            operation == RuntimeIpcOperation.StartThermalControl
                ? "authoritative GPU temperature unavailable"
                : "unexpected"));
        var client = new RuntimeThermalClient(ipc, TimeSpan.Zero);

        RuntimeThermalClientResult result = await client.RunAsync("default");

        Assert.AreEqual(RuntimeThermalClientOutcome.StartRejected, result.Outcome);
        StringAssert.Contains(result.Message, "GPU temperature unavailable");
        CollectionAssert.AreEqual(
            new[] { RuntimeIpcOperation.StartThermalControl },
            ipc.Operations);
    }

    [TestMethod]
    public async Task RuntimeFaultExitsWithoutSecondStopOrRecoveryRequest()
    {
        var ipc = new ScriptedIpcClient((operation, _, _) => operation switch
        {
            RuntimeIpcOperation.StartThermalControl => Success(Status("Running")),
            RuntimeIpcOperation.GetRuntimeEvents => Success(new RuntimeEventBatchDto(
                Status("Faulted", "ownership lost", totalEventCount: 1),
                [Event("OwnershipLost", 1)],
                1,
                1,
                false)),
            _ => throw new AssertFailedException($"Unexpected operation {operation}.")
        });
        var client = new RuntimeThermalClient(ipc, TimeSpan.Zero);

        RuntimeThermalClientResult result = await client.RunAsync("default");

        Assert.AreEqual(RuntimeThermalClientOutcome.RuntimeFaulted, result.Outcome);
        StringAssert.Contains(result.Message, "ownership lost");
        Assert.IsFalse(ipc.Operations.Contains(RuntimeIpcOperation.StopThermalControl));
        CollectionAssert.AreEqual(
            new[]
            {
                RuntimeIpcOperation.StartThermalControl,
                RuntimeIpcOperation.GetRuntimeEvents
            },
            ipc.Operations);
    }

    [TestMethod]
    public async Task StructuredEventRenderingDoesNotChangeClientBehavior()
    {
        string[] eventKinds =
        [
            "SessionStarted",
            "TelemetrySample",
            "ThermalDecision",
            "FanTargetChanged",
            "RazerWatchdogCheck",
            "SchedulerOverrun",
            "EmergencyHandoff",
            "OwnershipLost",
            "RecoveryAttempt",
            "RecoveryResult",
            "SessionStopped",
            "ProtocolExchange"
        ];
        RuntimeEventDto[] events = eventKinds
            .Select((kind, index) => Event(kind, index + 1))
            .ToArray();
        (RuntimeThermalClientResult plain, RuntimeIpcOperation[] plainOperations) =
            await RunTerminalBatchAsync(events, eventReceived: null);
        var received = new List<string>();
        (RuntimeThermalClientResult verbose, RuntimeIpcOperation[] verboseOperations) =
            await RunTerminalBatchAsync(events, item => received.Add(item.Kind));
        (RuntimeThermalClientResult brokenRenderer, RuntimeIpcOperation[] brokenOperations) =
            await RunTerminalBatchAsync(
                events,
                _ => throw new IOException("injected renderer failure"));

        Assert.IsTrue(plain.Succeeded);
        Assert.IsTrue(verbose.Succeeded);
        CollectionAssert.AreEqual(plainOperations, verboseOperations);
        CollectionAssert.AreEqual(plainOperations, brokenOperations);
        CollectionAssert.AreEqual(eventKinds, received);
        Assert.IsFalse(verbose.StopRequested);
        Assert.IsTrue(brokenRenderer.Succeeded);
    }

    [TestMethod]
    public async Task CancellationDrainsAllFinalBatchesThroughTargetSessionStopped()
    {
        var received = new List<long>();
        var requestedCursors = new List<long>();
        var ipc = new ScriptedIpcClient((operation, payload, _) => operation switch
        {
            RuntimeIpcOperation.StartThermalControl => Success(Status("Running")),
            RuntimeIpcOperation.StopThermalControl => Success(new StopThermalControlResultDto(
                true,
                true,
                "safe shutdown complete",
                Status("Stopped", totalEventCount: 4))),
            RuntimeIpcOperation.GetRuntimeEvents => FinalBatch(payload),
            _ => throw new AssertFailedException($"Unexpected operation {operation}.")
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new RuntimeThermalClient(ipc, TimeSpan.Zero);

        RuntimeThermalClientResult result = await client.RunAsync(
            "default",
            eventReceived: item => received.Add(item.Sequence),
            cancellationToken: cancellation.Token);

        Assert.IsTrue(result.Succeeded, result.Message);
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, received);
        CollectionAssert.AreEqual(new long[] { 0, 2 }, requestedCursors);
        Assert.AreEqual("SessionStopped", Event("SessionStopped", received[^1]).Kind);
        Assert.AreEqual(1,
            ipc.Operations.Count(item => item == RuntimeIpcOperation.StopThermalControl));

        RuntimeIpcResponse FinalBatch(object? payload)
        {
            var request = (GetRuntimeEventsRequest)payload!;
            requestedCursors.Add(request.AfterSequence);
            return request.AfterSequence == 0
                ? Success(new RuntimeEventBatchDto(
                    Status("Stopped", totalEventCount: 4),
                    [Event("ProtocolExchange", 1), Event("ProtocolExchange", 2)],
                    1,
                    4,
                    false))
                : Success(new RuntimeEventBatchDto(
                    Status("Stopped", totalEventCount: 4),
                    [Event("ProtocolExchange", 3), Event("SessionStopped", 4)],
                    1,
                    4,
                    false));
        }
    }

    [TestMethod]
    public async Task FinalDrainTimeoutIsBoundedAndDoesNotSendSecondStop()
    {
        var ipc = new ScriptedIpcClient((operation, _, _) => operation switch
        {
            RuntimeIpcOperation.StartThermalControl => Success(Status("Running")),
            RuntimeIpcOperation.StopThermalControl => Success(new StopThermalControlResultDto(
                true,
                true,
                "safe shutdown complete",
                Status("Stopped"))),
            RuntimeIpcOperation.GetRuntimeEvents => Success(new RuntimeEventBatchDto(
                Status("Running"),
                [],
                0,
                0,
                false)),
            _ => throw new AssertFailedException($"Unexpected operation {operation}.")
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new RuntimeThermalClient(
            ipc,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(40));

        RuntimeThermalClientResult result = await client.RunAsync(
            "default",
            cancellationToken: cancellation.Token);

        Assert.AreEqual(RuntimeThermalClientOutcome.CommunicationFailed, result.Outcome);
        StringAssert.Contains(result.Message, "timed out");
        StringAssert.Contains(result.Message, "No second stop request");
        Assert.AreEqual(1,
            ipc.Operations.Count(item => item == RuntimeIpcOperation.StopThermalControl));
        Assert.IsTrue(ipc.Operations.Count < 20, "The bounded drain should not busy-loop.");
    }

    [TestMethod]
    public async Task FaultObservedDuringCancellationDoesNotSendStop()
    {
        using var cancellation = new CancellationTokenSource();
        var ipc = new ScriptedIpcClient((operation, _, _) => operation switch
        {
            RuntimeIpcOperation.StartThermalControl => Success(Status("Running")),
            RuntimeIpcOperation.GetRuntimeEvents => FaultBatch(),
            _ => throw new AssertFailedException($"Unexpected operation {operation}.")
        });
        var client = new RuntimeThermalClient(ipc, TimeSpan.Zero);

        RuntimeThermalClientResult result = await client.RunAsync(
            "default",
            cancellationToken: cancellation.Token);

        Assert.AreEqual(RuntimeThermalClientOutcome.RuntimeFaulted, result.Outcome);
        Assert.IsFalse(ipc.Operations.Contains(RuntimeIpcOperation.StopThermalControl));

        RuntimeIpcResponse FaultBatch()
        {
            cancellation.Cancel();
            return Success(new RuntimeEventBatchDto(
                Status("Faulted", "emergency handoff", totalEventCount: 1),
                [Event("EmergencyHandoff", 1)],
                1,
                1,
                false));
        }
    }

    [TestMethod]
    public async Task EventCursorDoesNotRenderDuplicateEvents()
    {
        var received = new List<long>();
        var ipc = new ScriptedIpcClient((operation, payload, _) => operation switch
        {
            RuntimeIpcOperation.StartThermalControl => Success(Status("Running")),
            RuntimeIpcOperation.GetRuntimeEvents => BatchForCursor(payload),
            _ => throw new AssertFailedException($"Unexpected operation {operation}.")
        });
        var client = new RuntimeThermalClient(ipc, TimeSpan.Zero);

        RuntimeThermalClientResult result = await client.RunAsync(
            "default",
            eventReceived: item => received.Add(item.Sequence));

        Assert.IsTrue(result.Succeeded, result.Message);
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, received);

        static RuntimeIpcResponse BatchForCursor(object? payload)
        {
            var request = (GetRuntimeEventsRequest)payload!;
            return request.AfterSequence == 0
                ? Success(new RuntimeEventBatchDto(
                    Status("Running", totalEventCount: 2),
                    [Event("TelemetrySample", 1), Event("ThermalDecision", 2)],
                    1,
                    2,
                    false))
                : Success(new RuntimeEventBatchDto(
                    Status("Stopped", totalEventCount: 3),
                    [Event("ThermalDecision", 2), Event("SessionStopped", 3)],
                    1,
                    3,
                    false));
        }
    }

    [TestMethod]
    public async Task TruncatedHistoryIsReportedAndDrainContinuesFromOldestRetainedEvent()
    {
        RuntimeEventBatchDto? observedBatch = null;
        long? requestedCursor = null;
        var ipc = new ScriptedIpcClient((operation, payload, _) => operation switch
        {
            RuntimeIpcOperation.StartThermalControl =>
                Success(Status("Running", totalEventCount: 5)),
            RuntimeIpcOperation.StopThermalControl => Success(new StopThermalControlResultDto(
                true,
                true,
                "safe shutdown complete",
                Status("Stopped", totalEventCount: 12))),
            RuntimeIpcOperation.GetRuntimeEvents => GapBatch(payload),
            _ => throw new AssertFailedException($"Unexpected operation {operation}.")
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new RuntimeThermalClient(ipc, TimeSpan.Zero);

        RuntimeThermalClientResult result = await client.RunAsync(
            "default",
            batchReceived: batch => observedBatch = batch,
            cancellationToken: cancellation.Token);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(4, requestedCursor);
        Assert.IsNotNull(observedBatch);
        Assert.IsTrue(observedBatch.GapDetected);
        Assert.AreEqual(10, observedBatch.OldestAvailableSequence);

        RuntimeIpcResponse GapBatch(object? payload)
        {
            requestedCursor = ((GetRuntimeEventsRequest)payload!).AfterSequence;
            return Success(new RuntimeEventBatchDto(
                Status("Stopped", totalEventCount: 12),
                [
                    Event("ProtocolExchange", 10),
                    Event("ProtocolExchange", 11),
                    Event("SessionStopped", 12)
                ],
                10,
                12,
                true));
        }
    }

    [TestMethod]
    public async Task DisconnectDuringFinalDrainFailsCleanlyWithoutDirectRecovery()
    {
        var ipc = new ScriptedIpcClient((operation, _, _) => operation switch
        {
            RuntimeIpcOperation.StartThermalControl => Success(Status("Running")),
            RuntimeIpcOperation.StopThermalControl => Success(new StopThermalControlResultDto(
                true,
                true,
                "safe shutdown complete",
                Status("Stopped", totalEventCount: 1))),
            RuntimeIpcOperation.GetRuntimeEvents => throw new IOException("pipe disconnected"),
            _ => throw new AssertFailedException($"Unexpected operation {operation}.")
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new RuntimeThermalClient(ipc, TimeSpan.Zero);

        RuntimeThermalClientResult result = await client.RunAsync(
            "default",
            cancellationToken: cancellation.Token);

        Assert.AreEqual(RuntimeThermalClientOutcome.CommunicationFailed, result.Outcome);
        StringAssert.Contains(result.Message, "final event draining failed");
        StringAssert.Contains(result.Message, "pipe disconnected");
        Assert.IsNotNull(result.StopResult);
        Assert.IsTrue(result.StopResult.Succeeded);
        Assert.AreEqual(1,
            ipc.Operations.Count(item => item == RuntimeIpcOperation.StopThermalControl));
        AssertNoDirectHardwareDependencies(client);
    }

    [TestMethod]
    public async Task CursorMovingBackwardsFailsExplicitlyWithoutStopOrRetry()
    {
        var ipc = new ScriptedIpcClient((operation, _, _) => operation switch
        {
            RuntimeIpcOperation.StartThermalControl =>
                Success(Status("Running", totalEventCount: 5)),
            RuntimeIpcOperation.GetRuntimeEvents => Success(new RuntimeEventBatchDto(
                Status("Running", totalEventCount: 2),
                [Event("TelemetrySample", 2)],
                1,
                2,
                false)),
            _ => throw new AssertFailedException($"Unexpected operation {operation}.")
        });
        var client = new RuntimeThermalClient(ipc, TimeSpan.Zero);

        RuntimeThermalClientResult result = await client.RunAsync("default");

        Assert.AreEqual(RuntimeThermalClientOutcome.CommunicationFailed, result.Outcome);
        StringAssert.Contains(result.Message, "cursor metadata moved backwards");
        StringAssert.Contains(result.Message, "runtime may have restarted");
        Assert.IsFalse(ipc.Operations.Contains(RuntimeIpcOperation.StopThermalControl));
    }

    [TestMethod]
    public async Task DifferentSessionTransitionIsRejectedWithoutRenderingForeignEvent()
    {
        Guid differentSession = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var received = new List<long>();
        var ipc = new ScriptedIpcClient((operation, _, _) => operation switch
        {
            RuntimeIpcOperation.StartThermalControl => Success(Status("Running")),
            RuntimeIpcOperation.GetRuntimeEvents => Success(new RuntimeEventBatchDto(
                Status("Running", totalEventCount: 1),
                [Event("SessionStarted", 1) with { SessionId = differentSession }],
                1,
                1,
                false)),
            _ => throw new AssertFailedException($"Unexpected operation {operation}.")
        });
        var client = new RuntimeThermalClient(ipc, TimeSpan.Zero);

        RuntimeThermalClientResult result = await client.RunAsync(
            "default",
            eventReceived: item => received.Add(item.Sequence));

        Assert.AreEqual(RuntimeThermalClientOutcome.CommunicationFailed, result.Outcome);
        StringAssert.Contains(result.Message, "different thermal session");
        Assert.AreEqual(0, received.Count);
        Assert.IsFalse(ipc.Operations.Contains(RuntimeIpcOperation.StopThermalControl));
    }

    [TestMethod]
    public void IpcThermalClientHoldsNoHardwareOrOwnershipObjects()
    {
        var client = new RuntimeThermalClient(
            new ScriptedIpcClient((_, _, _) => throw new IOException()),
            TimeSpan.Zero);

        AssertNoDirectHardwareDependencies(client);
    }

    private static async Task<(RuntimeThermalClientResult Result,
        RuntimeIpcOperation[] Operations)> RunTerminalBatchAsync(
        IReadOnlyList<RuntimeEventDto> events,
        Action<RuntimeEventDto>? eventReceived)
    {
        var ipc = new ScriptedIpcClient((operation, _, _) => operation switch
        {
            RuntimeIpcOperation.StartThermalControl => Success(Status("Running")),
            RuntimeIpcOperation.GetRuntimeEvents => Success(new RuntimeEventBatchDto(
                Status("Stopped", totalEventCount: events[^1].Sequence),
                events,
                events[0].Sequence,
                events[^1].Sequence,
                false)),
            _ => throw new AssertFailedException($"Unexpected operation {operation}.")
        });
        var client = new RuntimeThermalClient(ipc, TimeSpan.Zero);
        RuntimeThermalClientResult result = await client.RunAsync(
            "default",
            eventReceived: eventReceived);
        return (result, ipc.Operations.ToArray());
    }

    private static RuntimeEventDto Event(string kind, long sequence) => new(
        kind,
        sequence,
        new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero)
            .AddMilliseconds(sequence),
        $"{kind} event",
        SessionId: kind is "SessionStarted" or "SessionStopped" ? SessionId : null);

    private static RuntimeStatusDto Status(
        string state,
        string? failure = null,
        long totalEventCount = 0) => new(
        state,
        SessionId,
        null,
        state == "Running" ? "Thermal/default" : null,
        null,
        null,
        null,
        null,
        SchedulerMetrics.Idle(TimeSpan.FromMilliseconds(500)),
        "Healthy",
        null,
        null,
        failure,
        null,
        TimeSpan.Zero,
        null,
        null,
        0,
        totalEventCount,
        0,
        0,
        []);

    private static RuntimeIpcResponse Success(object data) => new(
        1,
        Guid.NewGuid(),
        true,
        data,
        null);

    private static void AssertNoDirectHardwareDependencies(RuntimeThermalClient client)
    {
        Type[] fieldTypes = client.GetType()
            .GetFields(System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();
        Assert.IsFalse(fieldTypes.Any(type =>
            type.Name.Contains("RazerClient", StringComparison.Ordinal) ||
            type.Name.Contains("Hardware", StringComparison.Ordinal) ||
            type.Name.Contains("Ownership", StringComparison.Ordinal) ||
            type.Name.Contains("Telemetry", StringComparison.Ordinal)));
    }

    private sealed class ScriptedIpcClient : IRuntimeIpcClient
    {
        private readonly Func<RuntimeIpcOperation, object?, int, RuntimeIpcResponse> _handler;

        internal ScriptedIpcClient(
            Func<RuntimeIpcOperation, object?, int, RuntimeIpcResponse> handler)
        {
            _handler = handler;
        }

        internal List<RuntimeIpcOperation> Operations { get; } = [];

        public Task<RuntimeIpcResponse> SendAsync(
            RuntimeIpcOperation operation,
            object? payload = null,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            Operations.Add(operation);
            return Task.FromResult(_handler(operation, payload, Operations.Count));
        }
    }
}
