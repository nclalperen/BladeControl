using BladeControl.Runtime;

namespace BladeControl.Cli.Tests;

[TestClass]
public sealed class ThermalIpcCommandTests
{
    [TestMethod]
    public async Task ThermalRunUsesOnlyTypedStartAndStopIpcRequests()
    {
        var ipc = new FakeIpcClient((operation, _) => operation switch
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
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunThermalIpcClientAsync(
            verbose: false,
            ipc,
            cancellation.Token,
            output,
            error);

        Assert.AreEqual(0, exitCode, error.ToString());
        CollectionAssert.AreEqual(
            new[]
            {
                RuntimeIpcOperation.StartThermalControl,
                RuntimeIpcOperation.StopThermalControl
            },
            ipc.Operations);
        Assert.AreEqual(1,
            ipc.Operations.Count(item => item == RuntimeIpcOperation.StopThermalControl));
    }

    [TestMethod]
    public async Task MissingRuntimeHasClearErrorAndNoDirectFallback()
    {
        var ipc = new FakeIpcClient((_, _) => throw new IOException("pipe unavailable"));
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunThermalIpcClientAsync(
            verbose: false,
            ipc,
            CancellationToken.None,
            output,
            error);

        Assert.AreEqual(1, exitCode);
        CollectionAssert.AreEqual(
            new[] { RuntimeIpcOperation.StartThermalControl },
            ipc.Operations);
        StringAssert.Contains(error.ToString(), "Runtime Core is not running.");
        StringAssert.Contains(error.ToString(), "BladeControl.Cli service console");
    }

    [TestMethod]
    public async Task RuntimeStartFailureIsSurfacedWithoutRetry()
    {
        var ipc = new FakeIpcClient((_, _) => new RuntimeIpcResponse(
            1,
            Guid.NewGuid(),
            false,
            null,
            "authoritative CPU Package temperature unavailable"));
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunThermalIpcClientAsync(
            verbose: false,
            ipc,
            CancellationToken.None,
            output,
            error);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(error.ToString(), "CPU Package temperature unavailable");
        CollectionAssert.AreEqual(
            new[] { RuntimeIpcOperation.StartThermalControl },
            ipc.Operations);
    }

    [TestMethod]
    public async Task VerboseRendersStructuredEventsWithoutChangingRequests()
    {
        RuntimeEventDto protocol = new(
            "ProtocolExchange",
            1,
            new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero),
            "Razer command 0x0D82",
            Exchange: new ProtocolExchangeDto(
                0x01,
                "0x0D82",
                true,
                "0001",
                "0002"));
        var ipc = new FakeIpcClient((operation, _) => operation switch
        {
            RuntimeIpcOperation.StartThermalControl => Success(Status("Running")),
            RuntimeIpcOperation.GetRuntimeEvents => Success(new RuntimeEventBatchDto(
                Status("Stopped"),
                [protocol],
                1,
                1,
                false)),
            _ => throw new AssertFailedException($"Unexpected operation {operation}.")
        });
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunThermalIpcClientAsync(
            verbose: true,
            ipc,
            CancellationToken.None,
            output,
            error);

        Assert.AreEqual(0, exitCode, error.ToString());
        StringAssert.Contains(output.ToString(), "ProtocolExchange #1");
        StringAssert.Contains(output.ToString(), "0x0D82");
        StringAssert.Contains(output.ToString(), "request  0001");
        CollectionAssert.AreEqual(
            new[]
            {
                RuntimeIpcOperation.StartThermalControl,
                RuntimeIpcOperation.GetRuntimeEvents
            },
            ipc.Operations);
    }

    private static RuntimeStatusDto Status(string state) => new(
        state,
        null,
        null,
        null,
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
        null,
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

    private sealed class FakeIpcClient : IRuntimeIpcClient
    {
        private readonly Func<RuntimeIpcOperation, object?, RuntimeIpcResponse> _handler;

        internal FakeIpcClient(
            Func<RuntimeIpcOperation, object?, RuntimeIpcResponse> handler)
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
            return Task.FromResult(_handler(operation, payload));
        }
    }
}
