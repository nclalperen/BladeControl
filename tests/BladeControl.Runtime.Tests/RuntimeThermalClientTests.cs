using BladeControl.Runtime;

namespace BladeControl.Runtime.Tests;

[TestClass]
public sealed class RuntimeThermalClientTests
{
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
                Status("Stopped"))),
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
                RuntimeIpcOperation.StopThermalControl
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
                Status("Faulted", "ownership lost"),
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
                Status("Stopped"),
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
        $"{kind} event");

    private static RuntimeStatusDto Status(string state, string? failure = null) => new(
        state,
        state == "Running" ? Guid.Parse("11111111-1111-1111-1111-111111111111") : null,
        null,
        state == "Running" ? "Thermal/default" : null,
        null,
        null,
        null,
        null,
        new SchedulerMetrics(
            TimeSpan.FromMilliseconds(500),
            0,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            0,
            TimeSpan.Zero,
            0),
        "Healthy",
        null,
        failure,
        null,
        TimeSpan.Zero,
        0,
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
