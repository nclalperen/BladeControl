using BladeControl.Runtime;

namespace BladeControl.Cli.Tests;

[TestClass]
public sealed class ThermalIpcCommandTests
{
    private static readonly Guid SessionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

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
                RuntimeIpcOperation.StopThermalControl,
                RuntimeIpcOperation.GetRuntimeEvents
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
                Status("Stopped", totalEventCount: 1),
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

    [TestMethod]
    public async Task FinalSessionStoppedEventIsRenderedBeforeSuccessfulReturnSummary()
    {
        RuntimeEventDto firstSample = Event("TelemetrySample", 1) with
        {
            AcquisitionDurationMilliseconds = 12.5
        };
        RuntimeEventDto secondSample = Event("TelemetrySample", 2) with
        {
            Timestamp = firstSample.Timestamp.AddMilliseconds(500),
            AcquisitionDurationMilliseconds = 11.0
        };
        SchedulerMetrics scheduler = SchedulerMetrics.Idle(TimeSpan.FromMilliseconds(500)) with
        {
            CompletedCycles = 2,
            LatestStartToStart = TimeSpan.FromMilliseconds(500),
            LatestCycleExecutionDuration = TimeSpan.FromMilliseconds(20),
            SlowCycleCount = 1,
            MaximumCycleExecutionDuration = TimeSpan.FromMilliseconds(508)
        };
        var ipc = new FakeIpcClient((operation, _) => operation switch
        {
            RuntimeIpcOperation.StartThermalControl => Success(Status("Running")),
            RuntimeIpcOperation.StopThermalControl => Success(new StopThermalControlResultDto(
                true,
                true,
                "safe shutdown complete",
                Status("Stopped", totalEventCount: 3, scheduler: scheduler))),
            RuntimeIpcOperation.GetRuntimeEvents => Success(new RuntimeEventBatchDto(
                Status("Stopped", totalEventCount: 3, scheduler: scheduler),
                [firstSample, secondSample, Event("SessionStopped", 3)],
                1,
                3,
                false)),
            _ => throw new AssertFailedException($"Unexpected operation {operation}.")
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunThermalIpcClientAsync(
            verbose: true,
            ipc,
            cancellation.Token,
            output,
            error);

        string rendered = output.ToString();
        Assert.AreEqual(0, exitCode, error.ToString());
        int stopRequested = rendered.IndexOf(
            "Runtime Core stop requested",
            StringComparison.Ordinal);
        int stoppedEvent = rendered.IndexOf("SessionStopped #3", StringComparison.Ordinal);
        int stopMessage = rendered.IndexOf("safe shutdown complete", StringComparison.Ordinal);
        Assert.IsTrue(stopRequested >= 0, rendered);
        Assert.IsTrue(stoppedEvent > stopRequested, rendered);
        Assert.IsTrue(stoppedEvent >= 0, rendered);
        Assert.IsTrue(stopMessage > stoppedEvent, rendered);
        StringAssert.Contains(rendered, "Average actual start-to-start    500.0 ms");
        StringAssert.Contains(rendered, "Safe Auto handoff                Completed");
        StringAssert.Contains(rendered, "Original performance restoration Completed");
        StringAssert.Contains(rendered, "no current hardware read was made");
    }

    [TestMethod]
    public void StoppedRuntimeStatusLabelsDiagnosticValuesAsHistorical()
    {
        SchedulerMetrics scheduler = SchedulerMetrics.Idle(TimeSpan.FromMilliseconds(500)) with
        {
            CompletedCycles = 122,
            LatestStartToStart = TimeSpan.FromMilliseconds(501),
            LatestCycleExecutionDuration = TimeSpan.FromMilliseconds(80),
            LatestDeadlineLateness = TimeSpan.FromMilliseconds(2),
            SlowCycleCount = 7,
            MaximumCycleExecutionDuration = TimeSpan.FromMilliseconds(544)
        };
        var watchdog = new RuntimeRazerModeStateDto(
            "Balanced",
            "Manual",
            "Balanced",
            "Manual",
            true,
            true,
            false,
            false);
        RuntimeStatusDto status = Status(
            "Stopped",
            totalEventCount: 482,
            scheduler: scheduler,
            telemetryHealth: new TelemetryHealthDto("Stale", "sample is historical", false),
            watchdog: watchdog);
        using var output = new StringWriter();

        Program.PrintRuntimeStatus(status, verbose: true, output);

        string rendered = output.ToString();
        StringAssert.Contains(
            rendered,
            "Last session telemetry (historical; not a current hardware read)");
        StringAssert.Contains(
            rendered,
            "Last watchdog observation (historical; not current firmware state)");
        StringAssert.Contains(rendered, "Last session scheduler statistics");
        StringAssert.Contains(rendered, "Balanced + Manual");
        Assert.IsFalse(rendered.Contains(
            "Current watchdog observation",
            StringComparison.Ordinal));
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
        long totalEventCount = 0,
        SchedulerMetrics? scheduler = null,
        TelemetryHealthDto? telemetryHealth = null,
        RuntimeRazerModeStateDto? watchdog = null) => new(
        state,
        SessionId,
        null,
        null,
        null,
        null,
        null,
        telemetryHealth,
        scheduler ?? new SchedulerMetrics(
            TimeSpan.FromMilliseconds(500),
            0,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            0,
            0,
            0,
            TimeSpan.Zero,
            TimeSpan.Zero,
            DurationStatistics.Empty),
        scheduler?.SlowCycleCount > 0 ? "Degraded" : "Healthy",
        watchdog,
        null,
        null,
        TimeSpan.Zero,
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
