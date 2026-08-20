using BladeControl.Runtime;
using BladeControl.UI.Ipc;
using BladeControl.UI.Services;
using BladeControl.UI.ViewModels;

namespace BladeControl.UI.Tests;

[TestClass]
public sealed class RuntimeConnectionTests
{
    [TestMethod]
    public async Task DisconnectedStartupReconnectsAndPopulatesShellFromRuntimeData()
    {
        var client = new FakeRuntimeUiClient { IsOnline = false };
        var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        using var shell = new ShellViewModel(
            connection,
            new UiSettings { SelectedPage = "Monitoring" },
            isDesignPreview: false);

        Assert.AreEqual(RuntimeConnectionState.Connecting, connection.State);
        Assert.AreEqual("Connecting to Runtime Core", shell.ConnectionNoticeTitle);
        Assert.AreSame(shell.Monitoring, shell.SelectedPage);

        await connection.PollOnceAsync(CancellationToken.None);

        Assert.AreEqual(RuntimeConnectionState.Offline, connection.State);
        Assert.AreEqual("Runtime Core offline", shell.ConnectionNoticeTitle);
        Assert.IsTrue(shell.CanReconnect);
        StringAssert.Contains(shell.ConnectionNoticeDetail, "not reachable");
        Assert.IsFalse(connection.CanApplyStaticProfile);

        client.IsOnline = true;
        await connection.ReconnectAsync(CancellationToken.None);
        await connection.RefreshProfilesNowAsync(CancellationToken.None);
        shell.Performance.Refresh();
        shell.Dashboard.Refresh();

        Assert.AreEqual(RuntimeConnectionState.Online, connection.State);
        Assert.AreEqual("Runtime online", shell.ConnectionLabel);
        Assert.AreEqual("Stopped", shell.RuntimeStateLabel);
        Assert.IsFalse(shell.HasConnectionNotice);
        Assert.AreNotEqual(Display.Unavailable, shell.Dashboard.CpuTemperature);
        StringAssert.Contains(shell.Dashboard.CpuTemperature, "61");
        Assert.AreEqual("Balanced", shell.Dashboard.PerformanceMode);
        Assert.AreEqual("Balanced", shell.Performance.CurrentMode);
        Assert.AreEqual(TelemetryOrigin.ProviderSample, connection.TelemetryOrigin);
    }

    [DataTestMethod]
    [DataRow("Starting", false, "Starting")]
    [DataRow("Running", true, "owns cooling")]
    [DataRow("Stopping", false, "Stopping")]
    [DataRow("Faulted", true, "Faulted")]
    [DataRow("EmergencyHandoff", true, "EmergencyHandoff")]
    public async Task EveryNonStoppedStateGatesStaticWrites(
        string state,
        bool canStop,
        string expectedReason)
    {
        var client = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(state: state)
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var performance = new PerformanceViewModel(connection, CancellationToken.None);
        performance.Refresh();

        Assert.IsFalse(connection.CanApplyStaticProfile);
        Assert.IsFalse(performance.ApplyCommand.CanExecute(null));
        Assert.AreEqual(canStop, connection.CanStopThermalControl);
        Assert.IsNotNull(connection.StaticProfileBlockedReason);
        StringAssert.Contains(connection.StaticProfileBlockedReason!, expectedReason);

        performance.ApplyCommand.Execute(null);
        Assert.AreEqual(0, client.PerformanceRequests.Count);
    }

    [TestMethod]
    public async Task ReadinessDefaultsFalseAndBackendReasonsKeepStartDisabled()
    {
        var client = new FakeRuntimeUiClient
        {
            Doctor = RuntimeUiSampleData.Doctor(
                thermalOwnershipReady: false,
                reasons: ["PawnIO provenance is not safe for thermal ownership."])
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());

        Assert.IsFalse(connection.IsThermalOwnershipReady);
        Assert.IsFalse(connection.CanStartThermalControl);
        StringAssert.Contains(connection.ThermalReadinessReason, "offline");

        await connection.PollOnceAsync(CancellationToken.None);
        var performance = new PerformanceViewModel(connection, CancellationToken.None);
        var dashboard = new DashboardViewModel(
            connection,
            performance,
            CancellationToken.None);
        dashboard.Refresh();

        Assert.IsFalse(connection.IsThermalOwnershipReady);
        Assert.IsFalse(connection.CanStartThermalControl);
        StringAssert.Contains(connection.ThermalReadinessReason, "PawnIO provenance");
        Assert.IsFalse(dashboard.StartCoolingCommand.CanExecute(null));
        StringAssert.Contains(dashboard.StartBlockedReason!, "PawnIO provenance");
    }

    [TestMethod]
    public async Task DuplicateViewModelCommandIsPreventedWhileFirstRequestIsInFlight()
    {
        var client = new FakeRuntimeUiClient();
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var performance = new PerformanceViewModel(connection, CancellationToken.None);
        performance.Refresh();
        client.CommandGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        performance.ApplyCommand.Execute(null);
        await UiTestWait.UntilAsync(() => client.PerformanceRequests.Count == 1);

        Assert.IsTrue(connection.IsCommandInFlight);
        Assert.IsTrue(performance.ApplyCommand.IsRunning);
        Assert.IsFalse(performance.ApplyCommand.CanExecute(null));

        performance.ApplyCommand.Execute(null);
        Assert.AreEqual(1, client.PerformanceRequests.Count);

        client.CommandGate.SetResult();
        await UiTestWait.UntilAsync(() => !performance.ApplyCommand.IsRunning);

        Assert.AreEqual(1, client.PerformanceRequests.Count);
        Assert.IsFalse(connection.IsCommandInFlight);
        Assert.IsFalse(performance.StatusIsError);
    }

    [TestMethod]
    public async Task ConnectionGateRefusesConcurrentCallerWithoutInvokingIt()
    {
        var client = new FakeRuntimeUiClient();
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        client.CommandGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<RuntimeCommandOutcome> first = connection.ExecuteAsync(
            async (runtime, token) =>
            {
                await runtime.ApplyFanProfileAsync(
                    new ApplyFanProfileRequest("Auto", null, null),
                    token);
                return RuntimeCommandOutcome.Ok("first completed");
            });
        await UiTestWait.UntilAsync(() => client.FanRequests.Count == 1);
        int secondInvocations = 0;

        RuntimeCommandOutcome second = await connection.ExecuteAsync((_, _) =>
        {
            secondInvocations++;
            return Task.FromResult(RuntimeCommandOutcome.Ok("should not run"));
        });

        Assert.IsFalse(second.Succeeded);
        StringAssert.Contains(second.Message, "still in flight");
        Assert.AreEqual(0, secondInvocations);
        Assert.AreEqual(1, client.FanRequests.Count);

        client.CommandGate.SetResult();
        RuntimeCommandOutcome completed = await first;
        Assert.IsTrue(completed.Succeeded);
    }

    [TestMethod]
    public async Task BackendRejectionIsSurfacedVerbatimWithoutDisconnectOrRetry()
    {
        const string rejection = "Runtime Core rejected High because it is not hardware validated.";
        var client = new FakeRuntimeUiClient { RejectCommandsWith = rejection };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var performance = new PerformanceViewModel(connection, CancellationToken.None);
        performance.Refresh();

        performance.ApplyCommand.Execute(null);
        await UiTestWait.UntilAsync(() => client.PerformanceRequests.Count == 1);
        await UiTestWait.UntilAsync(() => !performance.ApplyCommand.IsRunning);

        Assert.AreEqual(rejection, performance.StatusMessage);
        Assert.IsTrue(performance.StatusIsError);
        Assert.AreEqual(RuntimeConnectionState.Online, connection.State);
        Assert.IsNull(connection.TransportError);
        Assert.AreEqual(1, client.PerformanceRequests.Count);
    }

    /// <summary>
    /// Both states raise a prominent alert and permit a stop, but they are not the same event
    /// and are no longer coloured the same.
    /// </summary>
    /// <remarks>
    /// EmergencyHandoff is reached only after firmware Auto has been established and verified:
    /// cooling is safely with the firmware and the machine is fine. It warrants attention — a
    /// thermal event happened and the session will not resume by itself — which is Warning.
    /// Faulted is the state where the handoff could not be established, so ownership is
    /// genuinely uncertain, and that stays Danger. The runtime separates these two outcomes
    /// deliberately; painting them identically told a user their machine was broken when the
    /// safety system had just done its job.
    /// </remarks>
    [DataTestMethod]
    [DataRow("Faulted", "CPU sensor failed", null, "CPU sensor failed", StatusTone.Danger)]
    [DataRow("EmergencyHandoff", null, "Firmware Auto handoff verified", "Emergency handoff", StatusTone.Warning)]
    public async Task FaultAndEmergencyStatesRenderProminentAlertsAndPermitStop(
        string state,
        string? failure,
        string? emergency,
        string expectedAlert,
        StatusTone expectedTone)
    {
        var client = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(
                state: state,
                lastFailureReason: failure,
                emergencyStatus: emergency)
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var performance = new PerformanceViewModel(connection, CancellationToken.None);
        var dashboard = new DashboardViewModel(
            connection,
            performance,
            CancellationToken.None);
        dashboard.Refresh();

        Assert.IsTrue(dashboard.HasRuntimeAlert);
        StringAssert.Contains(dashboard.RuntimeAlert!, expectedAlert);
        Assert.AreEqual(expectedTone, dashboard.RuntimeStateTone);
        Assert.IsTrue(connection.CanStopThermalControl);
        Assert.IsFalse(connection.CanApplyStaticProfile);
    }

    [TestMethod]
    public async Task OldAuthoritativeTelemetryIsMarkedStaleInsteadOfRenderedAsLive()
    {
        DateTimeOffset now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
        ThermalTelemetrySampleDto oldSample = RuntimeUiSampleData.Telemetry(
            timestamp: now - RuntimeConnection.StaleTelemetryThreshold - TimeSpan.FromSeconds(2));
        var client = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(
                state: "Running",
                sessionId: Guid.NewGuid(),
                currentProfile: "default",
                telemetry: oldSample,
                health: new TelemetryHealthDto("Healthy", "Last read succeeded.", true))
        };
        using var connection = new RuntimeConnection(
            client,
            new ImmediateUiDispatcher(),
            now: () => now);
        await connection.PollOnceAsync(CancellationToken.None);
        var performance = new PerformanceViewModel(connection, CancellationToken.None);
        var dashboard = new DashboardViewModel(
            connection,
            performance,
            CancellationToken.None);

        Assert.AreEqual(TelemetryOrigin.ThermalSession, connection.TelemetryOrigin);
        Assert.IsTrue(connection.IsTelemetryStale);
        TimeSpan? telemetryAge = connection.TelemetryAge;
        Assert.IsTrue(
            telemetryAge is { } age && age > RuntimeConnection.StaleTelemetryThreshold);
        StringAssert.Contains(dashboard.TelemetryFreshness, "Stale");
        Assert.AreEqual(StatusTone.Warning, dashboard.TelemetryFreshnessTone);
    }

    [TestMethod]
    public async Task CancellationReturnsFailureAndAlwaysReleasesCommandGate()
    {
        var client = new FakeRuntimeUiClient();
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        client.CommandGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        Task<RuntimeCommandOutcome> pending = connection.ExecuteAsync(
            async (runtime, token) =>
            {
                await runtime.ApplyPerformanceProfileAsync(
                    new ApplyPerformanceProfileRequest("Balanced", null, null),
                    token);
                return RuntimeCommandOutcome.Ok("Applied");
            },
            cancellation.Token);
        await UiTestWait.UntilAsync(() => client.PerformanceRequests.Count == 1);
        cancellation.Cancel();

        RuntimeCommandOutcome outcome = await pending;

        Assert.IsFalse(outcome.Succeeded);
        StringAssert.Contains(outcome.Message, "cancelled");
        Assert.IsFalse(connection.IsCommandInFlight);
        Assert.AreEqual(RuntimeConnectionState.Online, connection.State);
    }

    [TestMethod]
    public async Task TransportFailureMovesConnectionOfflineWithoutRetry()
    {
        var client = new FakeRuntimeUiClient();
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        client.IsOnline = false;

        RuntimeCommandOutcome outcome = await connection.ExecuteAsync(
            async (runtime, token) =>
            {
                await runtime.ApplyFanProfileAsync(
                    new ApplyFanProfileRequest("Auto", null, null),
                    token);
                return RuntimeCommandOutcome.Ok("Applied");
            });

        Assert.IsFalse(outcome.Succeeded);
        Assert.AreEqual(RuntimeConnectionState.Offline, connection.State);
        Assert.IsNotNull(connection.TransportError);
        Assert.AreEqual(1, client.FanRequests.Count);
        Assert.IsFalse(connection.CanIssueCommand);
    }

    [TestMethod]
    public async Task EventCursorOrdersFreshEventsAndResetsAfterRuntimeRestart()
    {
        var client = new ScriptedRuntimeUiClient();
        RuntimeStatusDto status = client.Status;
        client.EnqueueEventBatch(new RuntimeEventBatchDto(
            status,
            [
                RuntimeUiSampleData.Event("Five", 5, "five"),
                RuntimeUiSampleData.Event("Three", 3, "three"),
                RuntimeUiSampleData.Event("Four", 4, "four")
            ],
            3,
            5,
            true));
        client.EnqueueEventBatch(new RuntimeEventBatchDto(status, [], 1, 2, false));
        client.EnqueueEventBatch(new RuntimeEventBatchDto(
            status,
            [
                RuntimeUiSampleData.Event("Two", 2, "two"),
                RuntimeUiSampleData.Event("One", 1, "one")
            ],
            1,
            2,
            false));
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        var received = new List<(long[] Sequences, bool Gap)>();
        int resets = 0;
        connection.EventsReceived += (events, gap) =>
            received.Add((events.Select(item => item.Sequence).ToArray(), gap));
        connection.EventStreamReset += () => resets++;

        for (int tick = 0; tick < 6; tick++)
        {
            await connection.PollOnceAsync(CancellationToken.None);
        }

        CollectionAssert.AreEqual(new long[] { 0, 5, 0 }, client.RequestedEventCursors);
        Assert.AreEqual(2, received.Count);
        CollectionAssert.AreEqual(new long[] { 3, 4, 5 }, received[0].Sequences);
        Assert.IsTrue(received[0].Gap);
        CollectionAssert.AreEqual(new long[] { 1, 2 }, received[1].Sequences);
        Assert.AreEqual(1, resets);
        Assert.AreEqual(2L, connection.EventCursor);
    }

    [TestMethod]
    public async Task StoppedPollingUsesOneFastSampleAndNoRepeatedHeavyReads()
    {
        var client = new FakeRuntimeUiClient();
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());

        for (int tick = 0; tick < 25; tick++)
        {
            await connection.PollOnceAsync(CancellationToken.None);
        }

        Assert.AreEqual(25, client.StatusRequestCount);
        Assert.AreEqual(25, client.FastTelemetryRequestCount);
        Assert.AreEqual(0, client.DiagnosticSnapshotRequestCount);
        Assert.AreEqual(1, client.DoctorRequestCount);
        Assert.AreEqual(1, client.PerformanceStateRequestCount);
        Assert.AreEqual(1, client.FanStateRequestCount);
        Assert.AreEqual(TelemetryOrigin.ProviderSample, connection.TelemetryOrigin);
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), RuntimeConnection.DefaultPollInterval);
    }

    [TestMethod]
    public async Task RunningPollingUsesStatusCacheWithoutASecondAcquisition()
    {
        ThermalTelemetrySampleDto authoritative = RuntimeUiSampleData.Telemetry(
            cpuTemperature: 72.5,
            gpuTemperature: 64.25);
        var client = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(
                state: "Running",
                sessionId: Guid.NewGuid(),
                currentProfile: "default",
                telemetry: authoritative)
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());

        for (int tick = 0; tick < 10; tick++)
        {
            await connection.PollOnceAsync(CancellationToken.None);
        }

        Assert.AreSame(authoritative, connection.Telemetry);
        Assert.AreEqual(TelemetryOrigin.ThermalSession, connection.TelemetryOrigin);
        Assert.AreEqual(0, client.FastTelemetryRequestCount);
        Assert.AreEqual(0, client.DiagnosticSnapshotRequestCount);
    }

    [TestMethod]
    public async Task DiagnosticsRefreshExplicitlyRequestsOneHeavySnapshot()
    {
        var client = new FakeRuntimeUiClient();
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var diagnostics = new DiagnosticsViewModel(connection, CancellationToken.None);

        diagnostics.RefreshDiagnosticsCommand.Execute(null);
        await UiTestWait.UntilAsync(() => !diagnostics.RefreshDiagnosticsCommand.IsRunning);

        Assert.AreEqual(2, client.DoctorRequestCount);
        Assert.AreEqual(1, client.DiagnosticSnapshotRequestCount);
        Assert.AreEqual(TelemetryOrigin.DiagnosticSnapshot, connection.TelemetryOrigin);
        Assert.AreEqual("Diagnostics refreshed from Runtime Core.", diagnostics.StatusMessage);
        Assert.IsFalse(diagnostics.StatusIsError);
    }

    [TestMethod]
    public async Task StartingPollerTwiceKeepsOneCadenceLoop()
    {
        var client = new FakeRuntimeUiClient();
        using var connection = new RuntimeConnection(
            client,
            new ImmediateUiDispatcher(),
            pollInterval: TimeSpan.FromMilliseconds(5));

        connection.Start();
        Task first = connection.Completion;
        connection.Start();
        Task second = connection.Completion;
        await UiTestWait.UntilAsync(() => client.StatusRequestCount >= 3);
        await connection.StopAsync();

        Assert.AreSame(first, second);
        Assert.IsTrue(
            client.FastTelemetryRequestCount == client.StatusRequestCount ||
            client.FastTelemetryRequestCount == client.StatusRequestCount - 1);
        Assert.AreEqual(0, client.DiagnosticSnapshotRequestCount);
        Assert.IsTrue(first.IsCompleted);
    }

    [TestMethod]
    public void DashboardDoesNotRepeatTheOfflineReasonForStart()
    {
        var client = new FakeRuntimeUiClient { IsOnline = false };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        var performance = new PerformanceViewModel(connection, CancellationToken.None);
        var dashboard = new DashboardViewModel(
            connection,
            performance,
            CancellationToken.None);

        dashboard.Refresh();

        Assert.AreEqual("Runtime Core is offline.", dashboard.ProfileBlockedReason);
        Assert.IsNull(dashboard.StartBlockedReason);
    }
}
