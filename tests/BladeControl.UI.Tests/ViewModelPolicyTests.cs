using BladeControl.Runtime;
using BladeControl.UI.Ipc;
using BladeControl.UI.Services;
using BladeControl.UI.ViewModels;

namespace BladeControl.UI.Tests;

[TestClass]
public sealed class ViewModelPolicyTests
{
    [TestMethod]
    public async Task UnvalidatedPerformanceLevelsCannotBecomeSelectionsOrRequests()
    {
        var client = new FakeRuntimeUiClient();
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var viewModel = new PerformanceViewModel(connection, CancellationToken.None);
        viewModel.Refresh();

        // The levels the owner asked for are selectable.
        Assert.IsTrue(viewModel.TrySelectCpuLevel("High"));
        Assert.IsTrue(viewModel.TrySelectCpuLevel("Boost"));
        Assert.IsTrue(viewModel.TrySelectGpuLevel("Medium"));
        Assert.IsTrue(viewModel.TrySelectGpuLevel("High"));

        // Overclock is not offered at all, so it cannot be selected by name either. It is
        // excluded so BladeControl cannot interfere with tuning done in XTU.
        Assert.IsFalse(viewModel.TrySelectCpuLevel("Overclock"));
        Assert.IsFalse(viewModel.CpuLevels.Any(option => option.Value == "Overclock"));

        // And a value that is not a level at all is still refused.
        Assert.IsFalse(viewModel.TrySelectCpuLevel("Raw-255"));

        // Selecting a level is not applying one.
        Assert.AreEqual(0, client.PerformanceRequests.Count);
    }

    /// <summary>
    /// Telemetry is only ever labelled "Live" while a session is actually running.
    /// </summary>
    /// <remarks>
    /// The UI keeps polling the provider while idle, so a sample really can be one second old
    /// with no session behind it. The dashboard used to reach its "Live" branch whenever the
    /// sample was fresh and the state was not Stopped, which put "Live — 1 s old" beside a
    /// runtime state of "Stopped" on a machine where firmware owned the fans. Freshness and
    /// ownership are different claims and only one of them was true.
    /// </remarks>
    [TestMethod]
    public async Task FreshTelemetryIsOnlyCalledLiveWhileASessionIsRunning()
    {
        foreach (string state in new[]
                 {
                     "Stopped", "Starting", "Stopping", "Faulted", "EmergencyHandoff"
                 })
        {
            var client = new FakeRuntimeUiClient();
            client.Status = client.Status with { State = state };
            using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
            await connection.PollOnceAsync(CancellationToken.None);

            var performance = new PerformanceViewModel(connection, CancellationToken.None);
            var dashboard = new DashboardViewModel(
                connection,
                performance,
                CancellationToken.None);

            Assert.IsFalse(
                dashboard.TelemetryFreshness.Contains("Live", StringComparison.Ordinal),
                $"State '{state}' does not own cooling, so its telemetry must not read as " +
                $"live. Got: '{dashboard.TelemetryFreshness}'.");
            Assert.AreEqual(
                StatusTone.Muted,
                dashboard.TelemetryFreshnessTone,
                $"State '{state}' must not carry the good/live tone.");
        }

        // And the one state that may say it, does.
        var running = new FakeRuntimeUiClient();
        running.Status = running.Status with { State = "Running" };
        using var liveConnection = new RuntimeConnection(running, new ImmediateUiDispatcher());
        await liveConnection.PollOnceAsync(CancellationToken.None);
        var livePerformance = new PerformanceViewModel(liveConnection, CancellationToken.None);
        var liveDashboard = new DashboardViewModel(
            liveConnection,
            livePerformance,
            CancellationToken.None);

        Assert.IsTrue(
            liveDashboard.TelemetryFreshness.Contains("Live", StringComparison.Ordinal),
            $"A running session reports live telemetry. Got: " +
            $"'{liveDashboard.TelemetryFreshness}'.");
    }

    /// <summary>
    /// The compact panel applies the same rule, so the two surfaces cannot disagree about
    /// whether BladeControl is driving the fans.
    /// </summary>
    [TestMethod]
    public async Task CompactPanelAppliesTheSameLiveTelemetryRule()
    {
        foreach (string state in new[]
                 {
                     "Stopped", "Starting", "Stopping", "Faulted", "EmergencyHandoff"
                 })
        {
            var client = new FakeRuntimeUiClient();
            client.Status = client.Status with { State = state };
            using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
            await connection.PollOnceAsync(CancellationToken.None);

            using var shell = new ShellViewModel(
                connection,
                new UiSettings { MinimizeToTray = false },
                isDesignPreview: true,
                _ => { });
            using var compact = new CompactControlViewModel(shell);

            Assert.IsFalse(
                compact.TelemetryCaption.Contains("Live", StringComparison.Ordinal),
                $"State '{state}' must not read as live in the compact panel. Got: " +
                $"'{compact.TelemetryCaption}'.");
            Assert.AreEqual(StatusTone.Muted, compact.TelemetryTone);
        }

        var running = new FakeRuntimeUiClient();
        running.Status = running.Status with { State = "Running" };
        using var liveConnection = new RuntimeConnection(running, new ImmediateUiDispatcher());
        await liveConnection.PollOnceAsync(CancellationToken.None);
        using var liveShell = new ShellViewModel(
            liveConnection,
            new UiSettings { MinimizeToTray = false },
            isDesignPreview: true,
            _ => { });
        using var liveCompact = new CompactControlViewModel(liveShell);

        Assert.AreEqual("Live telemetry", liveCompact.TelemetryCaption);
        Assert.AreEqual(
            StatusTone.Good,
            liveCompact.TelemetryTone,
            "This caught the old pair: live text was rendered with the muted/not-live tone.");
    }

    /// <summary>A transport outage outranks the state retained in the last status snapshot.</summary>
    /// <remarks>
    /// Dashboard freshness special-cased stale Stopped telemetry before checking the connection,
    /// so an offline runtime could still produce "Last session telemetry · Stopped" instead of
    /// saying the process was unreachable. The shared text/tone classification fixes the order.
    /// </remarks>
    [TestMethod]
    public async Task OfflineTelemetryFreshnessDoesNotHideBehindRetainedStoppedState()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var client = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(state: "Stopped"),
            TelemetrySample = RuntimeUiSampleData.Telemetry(timestamp: now.AddMinutes(-1))
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
        var fans = new FansThermalViewModel(connection, CancellationToken.None);
        var diagnostics = new DiagnosticsViewModel(connection, CancellationToken.None);

        client.IsOnline = false;
        await connection.PollOnceAsync(CancellationToken.None);
        diagnostics.Refresh();

        StringAssert.Contains(dashboard.TelemetryFreshness, "Runtime Core offline");
        Assert.AreEqual(StatusTone.Muted, dashboard.TelemetryFreshnessTone);
        Assert.IsFalse(
            dashboard.TelemetryFreshness.Contains("Stopped", StringComparison.Ordinal),
            "The connection fact is newer than the retained runtime-state snapshot.");
        Assert.AreEqual("LAST REPORTED SCHEDULER", dashboard.SchedulerLabel);
        StringAssert.StartsWith(dashboard.SchedulerHealth, "Last reported ·");
        Assert.AreEqual("LAST REPORTED TELEMETRY", dashboard.TelemetryLabel);
        Assert.AreEqual(Display.LastReportedNoSessionTelemetry, dashboard.TelemetryHealth);
        Assert.AreEqual(
            Display.LastReportedNoSessionTelemetryDetail,
            dashboard.TelemetryHealthDetail);
        Assert.AreEqual(Display.LastReportedNoSessionTelemetry, fans.TelemetryHealth);
        Assert.AreEqual(
            Display.LastReportedNoSessionTelemetryDetail,
            fans.TelemetryHealthDetail);
        Assert.AreEqual("Last reported scheduler", diagnostics.Scheduler.Title);
        DiagnosticItem history = diagnostics.Scheduler.Items.Single(item =>
            item.Label == "History");
        Assert.AreEqual("No session reported", history.Value);
        StringAssert.Contains(history.Detail!, "Retained status snapshot");
    }

    /// <summary>Every session-derived status field uses the same six-state classification.</summary>
    /// <remarks>
    /// Scheduler, telemetry-health and watchdog labels still special-cased Stopped after the
    /// freshness badge had moved to Display.IsLiveSession. Faulted, EmergencyHandoff and both
    /// transitions therefore showed last-session values under current headings and live tones.
    /// The loop would have caught each incomplete state enumeration independently.
    /// </remarks>
    [TestMethod]
    public async Task EveryNonRunningStateLabelsSessionStatusByItsActualScope()
    {
        foreach (string state in new[]
                 {
                     "Stopped", "Starting", "Stopping", "Faulted", "EmergencyHandoff"
                 })
        {
            var client = new FakeRuntimeUiClient
            {
                Status = RuntimeUiSampleData.Status(
                    state: state,
                    sessionId: Guid.NewGuid(),
                    telemetry: RuntimeUiSampleData.Telemetry(),
                    health: new TelemetryHealthDto("Healthy", "Last read succeeded.", true),
                    scheduler: RuntimeUiSampleData.Scheduler(completedCycles: 20),
                    watchdog: RuntimeUiSampleData.Watchdog())
            };
            using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
            await connection.PollOnceAsync(CancellationToken.None);
            var performance = new PerformanceViewModel(connection, CancellationToken.None);
            var dashboard = new DashboardViewModel(
                connection,
                performance,
                CancellationToken.None);
            var fans = new FansThermalViewModel(connection, CancellationToken.None);

            Assert.AreEqual(
                state switch
                {
                    "Starting" => "SCHEDULER · SESSION STARTING",
                    "Stopping" => "SCHEDULER · SESSION STOPPING",
                    _ => "LAST SESSION SCHEDULER"
                },
                dashboard.SchedulerLabel,
                state);
            Assert.AreEqual(StatusTone.Muted, dashboard.SchedulerHealthTone, state);
            Assert.AreEqual(
                state switch
                {
                    "Starting" => "TELEMETRY · SESSION STARTING",
                    "Stopping" => "TELEMETRY · SESSION STOPPING",
                    _ => "LAST SESSION TELEMETRY"
                },
                dashboard.TelemetryLabel,
                state);
            Assert.AreEqual(StatusTone.Muted, dashboard.TelemetryHealthTone, state);
            Assert.AreEqual("LAST WATCHDOG OBSERVATION", dashboard.FirmwareFanModeLabel, state);
            StringAssert.StartsWith(
                fans.TelemetryHealth,
                state switch
                {
                    "Starting" => "Retained while session starts ·",
                    "Stopping" => "Retained while session stops ·",
                    _ => "Last session ·"
                },
                state);
            Assert.AreEqual(StatusTone.Muted, fans.TelemetryHealthTone, state);
        }
    }

    /// <summary>An offline retained Running snapshot is history everywhere, never current.</summary>
    [TestMethod]
    public async Task OfflineRetainedRunningStateNeverPresentsSessionReadingsAsCurrent()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var client = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(
                state: "Running",
                sessionId: Guid.NewGuid(),
                currentProfile: "Thermal/default",
                effectiveFanTargetRpm: 4_200,
                telemetry: RuntimeUiSampleData.Telemetry(timestamp: now),
                health: new TelemetryHealthDto("Healthy", "Last read succeeded.", true),
                scheduler: RuntimeUiSampleData.Scheduler(completedCycles: 20),
                watchdog: RuntimeUiSampleData.Watchdog()),
            TelemetrySample = RuntimeUiSampleData.Telemetry(timestamp: now),
            FanState = new FanStateDto(
                new RuntimeModeDto("Balanced", "Manual", "Balanced", "Manual"),
                4_200,
                4_200)
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        using var shell = new ShellViewModel(
            connection,
            new UiSettings { MinimizeToTray = false },
            isDesignPreview: true,
            _ => { });
        using var compact = new CompactControlViewModel(shell);

        client.IsOnline = false;
        await connection.PollOnceAsync(CancellationToken.None);
        shell.Dashboard.Refresh();
        shell.FansThermal.Refresh();
        shell.Diagnostics.Refresh();
        compact.Refresh();
        shell.Monitoring.Append(RuntimeUiSampleData.Telemetry(timestamp: now.AddSeconds(1)));

        Assert.AreEqual("Running", connection.RuntimeStateName, "The offline path retains status.");
        Assert.AreEqual("Last reported · Running", shell.RuntimeStateLabel);
        StringAssert.StartsWith(shell.Dashboard.RuntimeState, "Last reported · Running");
        Assert.AreEqual(StatusTone.Muted, shell.Dashboard.RuntimeStateTone);
        Assert.AreEqual("LAST REPORTED SCHEDULER", shell.Dashboard.SchedulerLabel);
        Assert.AreEqual("LAST REPORTED TELEMETRY", shell.Dashboard.TelemetryLabel);
        Assert.AreEqual("LAST WATCHDOG OBSERVATION", shell.Dashboard.FirmwareFanModeLabel);
        Assert.AreEqual(Display.Unavailable, shell.Dashboard.FanTarget);
        Assert.AreEqual(Display.Unavailable, shell.Dashboard.FirmwareFan1Value);
        Assert.AreEqual(Display.Unavailable, shell.Dashboard.FirmwareFan2Value);
        StringAssert.Contains(shell.Dashboard.FanTargetDetail, "offline");
        StringAssert.StartsWith(shell.Dashboard.PerformanceMode, "Last reported ·");
        StringAssert.StartsWith(shell.Performance.CurrentSummary, "Last reported ·");

        Assert.AreEqual(Display.Unavailable, shell.FansThermal.EffectiveFanTarget);
        Assert.AreEqual(Display.Unavailable, shell.FansThermal.ActiveCurve);
        Assert.AreEqual(Display.Unavailable, shell.FansThermal.FirmwareFan1Value);
        Assert.AreEqual(Display.Unavailable, shell.FansThermal.FirmwareFanMode);
        StringAssert.StartsWith(shell.FansThermal.ThermalSession, "Last reported · Running");
        StringAssert.StartsWith(shell.FansThermal.TelemetryHealth, "Last reported ·");
        Assert.AreEqual(StatusTone.Muted, shell.FansThermal.TelemetryHealthTone);

        Assert.IsFalse(compact.IsDynamicRunning);
        StringAssert.StartsWith(compact.DynamicState, "Last reported · Running");
        Assert.AreEqual(Display.Unavailable, compact.DynamicTarget);
        Assert.AreEqual(Display.Unavailable, compact.FanValue);
        Assert.AreEqual("Runtime offline", compact.FooterText);
        Assert.IsNull(shell.Monitoring.History[TelemetryHistory.FanTargetKey].Latest);

        DiagnosticItem runtime = shell.Diagnostics.Runtime.Items.Single(item =>
            item.Label == "Last reported runtime state");
        Assert.AreEqual(StatusTone.Muted, runtime.Tone);
        StringAssert.Contains(runtime.Detail!, "offline");
        Assert.AreEqual("Last reported scheduler", shell.Diagnostics.Scheduler.Title);
        Assert.IsTrue(shell.Diagnostics.Razer.Items.Any(item =>
            item.Label == "Last watchdog zone 1"));
        Assert.IsTrue(shell.Diagnostics.Telemetry.Items.Any(item =>
            item.Label == "Last reported telemetry origin"));
    }

    /// <summary>Offline without a snapshot is unavailable, not imaginary retained history.</summary>
    [TestMethod]
    public async Task ColdOfflineConnectionDoesNotClaimAStateWasReported()
    {
        var client = new FakeRuntimeUiClient { IsOnline = false };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var performance = new PerformanceViewModel(connection, CancellationToken.None);
        var dashboard = new DashboardViewModel(
            connection,
            performance,
            CancellationToken.None);
        using var shell = new ShellViewModel(
            connection,
            new UiSettings { MinimizeToTray = false },
            isDesignPreview: true,
            _ => { });
        using var compact = new CompactControlViewModel(shell);
        var diagnostics = new DiagnosticsViewModel(connection, CancellationToken.None);
        diagnostics.Refresh();

        Assert.AreEqual(
            SessionObservationScope.Unavailable,
            Display.SessionObservation(connection.IsOnline, connection.RuntimeStateName));
        Assert.AreEqual(Display.Unavailable, dashboard.RuntimeState);
        Assert.AreEqual(Display.Unavailable, shell.RuntimeStateLabel);
        StringAssert.Contains(dashboard.RuntimeStateDescription, "no runtime state");
        Assert.AreEqual("SCHEDULER", dashboard.SchedulerLabel);
        Assert.IsFalse(dashboard.HasNoSessionHistory, "Unknown is not known-empty history.");
        Assert.AreEqual(Display.Unavailable, dashboard.SchedulerHealth);
        Assert.AreEqual(Display.Unavailable, dashboard.TelemetryHealth);
        Assert.IsNull(dashboard.TelemetryHealthDetail);
        Assert.AreEqual(Display.Unavailable, shell.FansThermal.TelemetryHealth);
        Assert.IsNull(shell.FansThermal.TelemetryHealthDetail);
        Assert.AreEqual("FAN", compact.FanHeading);
        Assert.AreEqual("runtime offline", compact.FanCaption);
        Assert.AreEqual("FIRMWARE MODE", dashboard.FirmwareFanModeLabel);

        DiagnosticItem runtime = diagnostics.Runtime.Items.Single(item =>
            item.Label == "Runtime state");
        Assert.AreEqual(Display.Unavailable, runtime.Value);
        StringAssert.Contains(runtime.Detail!, "no runtime state");
        Assert.AreEqual("Scheduler", diagnostics.Scheduler.Title);
        DiagnosticItem scheduler = diagnostics.Scheduler.Items.Single(item =>
            item.Label == "History");
        Assert.AreEqual(Display.Unavailable, scheduler.Value);
        StringAssert.Contains(scheduler.Detail!, "No runtime status snapshot");
        Assert.IsFalse(scheduler.Detail!.Contains(
            "has not run", StringComparison.OrdinalIgnoreCase));
        DiagnosticItem runtimeVersion = diagnostics.Runtime.Items.Single(item =>
            item.Label == "Runtime version");
        StringAssert.Contains(runtimeVersion.Detail!, "No runtime status snapshot");
        Assert.IsFalse(runtimeVersion.Detail!.Contains(
            "older", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(diagnostics.Runtime.Items.Any(item =>
            item.Label == "Last reported runtime state"));
        Assert.IsFalse(diagnostics.Runtime.Items.Any(item =>
            item.Label == "Last reported emergency status"));
        Assert.IsFalse(diagnostics.Razer.Items.Any(item =>
            item.Label.StartsWith("Last watchdog", StringComparison.Ordinal)));
        Assert.IsFalse(diagnostics.Telemetry.Items.Any(item =>
            item.Label == "Last reported telemetry origin"));
    }

    [TestMethod]
    public async Task OfflineTerminalAlertIsRetainedHistoryNotCurrentOwnership()
    {
        foreach ((string State, string? Emergency, string? Failure) item in new[]
                 {
                     ("EmergencyHandoff", "Firmware Auto handoff completed.", (string?)null),
                     ("Faulted", (string?)null, "Cooling ownership could not be established.")
                 })
        {
            var client = new FakeRuntimeUiClient
            {
                Status = RuntimeUiSampleData.Status(
                    state: item.State,
                    emergencyStatus: item.Emergency,
                    lastFailureReason: item.Failure)
            };
            using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
            await connection.PollOnceAsync(CancellationToken.None);
            using var shell = new ShellViewModel(
                connection,
                new UiSettings { MinimizeToTray = false },
                isDesignPreview: true,
                _ => { });
            using var compact = new CompactControlViewModel(shell);

            client.IsOnline = false;
            await connection.PollOnceAsync(CancellationToken.None);
            shell.Dashboard.Refresh();
            shell.Diagnostics.Refresh();
            compact.Refresh();

            StringAssert.StartsWith(shell.Dashboard.RuntimeAlert!, "Last reported");
            StringAssert.Contains(shell.Dashboard.RuntimeAlert!, "offline");
            Assert.IsFalse(shell.Dashboard.RuntimeAlert!.Contains(
                "owns cooling", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(shell.Dashboard.RuntimeAlert.Contains(
                "service is still running", StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(StatusTone.Muted, shell.Dashboard.RuntimeAlertTone, item.State);
            Assert.IsFalse(compact.IsEmergencyHandoff, item.State);

            DiagnosticItem emergency = shell.Diagnostics.Runtime.Items.Single(row =>
                row.Label == "Last reported emergency status");
            Assert.AreEqual(StatusTone.Muted, emergency.Tone, item.State);
            DiagnosticItem failure = shell.Diagnostics.Runtime.Items.Single(row =>
                row.Label == "Last failure");
            Assert.AreEqual(StatusTone.Muted, failure.Tone, item.State);
        }
    }

    [TestMethod]
    public async Task OfflineRetainedRunningDoesNotLatchFixedEditorModeAcrossReconnect()
    {
        var client = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(
                state: "Running",
                currentProfile: "Thermal/default",
                effectiveFanTargetRpm: 4_200),
            FanState = new FanStateDto(
                new RuntimeModeDto("Balanced", "Manual", "Balanced", "Manual"),
                4_200,
                4_200)
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        client.IsOnline = false;
        await connection.PollOnceAsync(CancellationToken.None);

        using var shell = new ShellViewModel(
            connection,
            new UiSettings { MinimizeToTray = false },
            isDesignPreview: true,
            _ => { });
        Assert.AreEqual(CoolingMode.FirmwareAuto, shell.FansThermal.Mode);

        client.IsOnline = true;
        await connection.PollOnceAsync(CancellationToken.None);

        Assert.AreEqual(CoolingMode.DynamicCurve, shell.FansThermal.Mode);
    }

    /// <summary>Stopped can truthfully mean a standalone Fixed profile, not firmware Auto.</summary>
    [TestMethod]
    public async Task StoppedFixedProfileReportsRuntimeOwnershipFromTheCurrentSnapshot()
    {
        var client = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(
                state: "Stopped",
                currentProfile: "Fan/Fixed",
                effectiveFanTargetRpm: 4_200),
            FanState = new FanStateDto(
                new RuntimeModeDto("Balanced", "Manual", "Balanced", "Manual"),
                4_200,
                4_200)
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        using var shell = new ShellViewModel(
            connection,
            new UiSettings { MinimizeToTray = false },
            isDesignPreview: true,
            _ => { });
        using var compact = new CompactControlViewModel(shell);
        shell.Diagnostics.Refresh();

        StringAssert.Contains(shell.Dashboard.RuntimeStateDescription, "Runtime Core holds");
        Assert.IsFalse(shell.Dashboard.RuntimeStateDescription.Contains(
            "firmware owns", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(Display.Rpm(4_200), shell.Dashboard.FanTarget);
        Assert.AreEqual(Display.Rpm(4_200), shell.FansThermal.EffectiveFanTarget);
        Assert.AreEqual("Fan/Fixed", shell.FansThermal.ActiveCurve);
        Assert.AreEqual(Display.FirmwareFanValue(4_200), shell.Dashboard.FirmwareFan1Value);
        Assert.AreEqual($"Fixed · {Display.Rpm(4_200)}", compact.FooterText);

        DiagnosticItem runtime = shell.Diagnostics.Runtime.Items.Single(item =>
            item.Label == "Runtime state");
        StringAssert.Contains(runtime.Detail!, "Runtime Core holds");
    }

    /// <summary>An untimestamped direct fan read cannot prove stopped-state ownership.</summary>
    [TestMethod]
    public async Task ManualFanProfileSnapshotDoesNotBecomeAnAuthoritativeFixedClaim()
    {
        var client = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(
                state: "Stopped",
                currentProfile: null,
                effectiveFanTargetRpm: 4_200),
            FanState = new FanStateDto(
                new RuntimeModeDto("Balanced", "Manual", "Balanced", "Manual"),
                4_200,
                4_200)
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        using var shell = new ShellViewModel(
            connection,
            new UiSettings { MinimizeToTray = false },
            isDesignPreview: true,
            _ => { });
        using var compact = new CompactControlViewModel(shell);
        shell.Diagnostics.Refresh();

        StringAssert.Contains(shell.Dashboard.RuntimeStateDescription, "no dynamic thermal session");
        Assert.IsFalse(shell.Dashboard.RuntimeStateDescription.Contains(
            "holds", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(Display.Unavailable, shell.Dashboard.FanTarget);
        Assert.AreEqual(Display.Unavailable, shell.FansThermal.EffectiveFanTarget);
        Assert.AreEqual(Display.FirmwareFanValue(4_200), shell.FansThermal.FirmwareFan1Value);
        Assert.AreEqual("selected target · not applied", compact.FanCaption);
        Assert.AreEqual("Stopped · no Dynamic session", compact.FooterText);

        DiagnosticItem runtime = shell.Diagnostics.Runtime.Items.Single(item =>
            item.Label == "Runtime state");
        StringAssert.Contains(runtime.Detail!, "no dynamic thermal session");
        Assert.IsFalse(runtime.Detail!.Contains("holds", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task StoppedFixedProfileWithoutATargetDoesNotClaimFirmwareOwnership()
    {
        var client = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(
                state: "Stopped",
                currentProfile: "Fan/Fixed",
                effectiveFanTargetRpm: null)
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        using var shell = new ShellViewModel(
            connection,
            new UiSettings { MinimizeToTray = false },
            isDesignPreview: true,
            _ => { });
        using var compact = new CompactControlViewModel(shell);

        Assert.AreEqual(Display.Unavailable, shell.Dashboard.FanTarget);
        StringAssert.Contains(shell.Dashboard.FanTargetDetail, "Fixed profile");
        Assert.IsFalse(shell.Dashboard.FanTargetDetail.Contains(
            "firmware", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(Display.Unavailable, compact.FanValue);
        Assert.AreEqual("fixed target unavailable", compact.FanCaption);
        Assert.AreEqual(StatusTone.Warning, compact.FanTone);
    }

    /// <summary>The compact footer derives its sentence and colour from one classification.</summary>
    /// <remarks>
    /// The old tone switch put EmergencyHandoff in Danger, omitted Starting and Stopping, and
    /// checked a retained Running state before checking that the connection was offline. That
    /// last ordering rendered the literal sentence "Runtime offline" in green.
    /// </remarks>
    [TestMethod]
    public async Task CompactFooterTextAndToneCannotContradictRuntimeOrConnectionState()
    {
        foreach (string state in new[]
                 {
                     "Stopped", "Starting", "Running", "Stopping", "Faulted",
                     "EmergencyHandoff"
                 })
        {
            var client = new FakeRuntimeUiClient();
            client.Status = client.Status with
            {
                State = state,
                CurrentProfile = state == "Running" ? "Thermal/default" : null,
                CurrentEffectiveFanTargetRpm = state == "Running" ? 4_200 : null
            };
            using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
            await connection.PollOnceAsync(CancellationToken.None);
            using var shell = new ShellViewModel(
                connection,
                new UiSettings { MinimizeToTray = false },
                isDesignPreview: true,
                _ => { });
            using var compact = new CompactControlViewModel(shell);

            Assert.AreEqual(Display.RuntimeStateTone(state), compact.FooterTone, state);
            if (state == "EmergencyHandoff")
            {
                StringAssert.Contains(compact.FooterText, "firmware Auto owns cooling");
            }
            else if (state is "Starting" or "Stopping")
            {
                StringAssert.Contains(compact.FooterText, state);
            }
        }

        var disconnectedClient = new FakeRuntimeUiClient();
        disconnectedClient.Status = disconnectedClient.Status with { State = "Running" };
        using var disconnectedConnection = new RuntimeConnection(
            disconnectedClient,
            new ImmediateUiDispatcher());
        await disconnectedConnection.PollOnceAsync(CancellationToken.None);
        using var disconnectedShell = new ShellViewModel(
            disconnectedConnection,
            new UiSettings { MinimizeToTray = false },
            isDesignPreview: true,
            _ => { });
        using var disconnectedCompact = new CompactControlViewModel(disconnectedShell);

        disconnectedClient.IsOnline = false;
        await disconnectedConnection.PollOnceAsync(CancellationToken.None);

        Assert.AreEqual("Runtime offline", disconnectedCompact.FooterText);
        Assert.AreEqual(StatusTone.Danger, disconnectedCompact.FooterTone);
        Assert.AreEqual(Display.Unavailable, disconnectedCompact.FanValue);
        Assert.AreEqual("runtime offline", disconnectedCompact.FanCaption);
        Assert.AreEqual(StatusTone.Danger, disconnectedCompact.FanTone);
    }

    /// <summary>Event outcome text and colour cannot make opposite claims.</summary>
    /// <remarks>
    /// RuntimeEventViewModel printed Succeeded in its detail but coloured only by Kind. A
    /// successful handoff was red and a failed RecoveryResult was green; these assertions fail
    /// against that kind-only switch.
    /// </remarks>
    [TestMethod]
    public void RuntimeEventToneUsesTheOutcomePrintedInItsDetail()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var successfulHandoff = new RuntimeEventViewModel(new RuntimeEventDto(
            "EmergencyHandoff", 1, now, "Firmware Auto handoff completed.", Succeeded: true));
        var failedHandoff = new RuntimeEventViewModel(new RuntimeEventDto(
            "EmergencyHandoff", 2, now, "Firmware Auto handoff failed.", Succeeded: false));
        var successfulRecovery = new RuntimeEventViewModel(new RuntimeEventDto(
            "RecoveryResult", 3, now, "Recovery completed.", Succeeded: true));
        var failedRecovery = new RuntimeEventViewModel(new RuntimeEventDto(
            "RecoveryResult", 4, now, "Recovery failed.", Succeeded: false));

        Assert.AreEqual(StatusTone.Warning, successfulHandoff.Tone);
        Assert.AreEqual(StatusTone.Danger, failedHandoff.Tone);
        Assert.AreEqual(StatusTone.Good, successfulRecovery.Tone);
        Assert.AreEqual(StatusTone.Danger, failedRecovery.Tone);
    }

    /// <summary>A command result is never made visible before its severity is current.</summary>
    /// <remarks>
    /// PageViewModel used to notify StatusMessage first and StatusIsError second. The compact
    /// model listens to both notifications, so a failure briefly appeared in the old success
    /// tone. Inspecting the model at the message notification catches that ordering defect.
    /// </remarks>
    [TestMethod]
    public async Task CommandSeverityIsCurrentWhenTheMessageBecomesVisible()
    {
        var client = new FakeRuntimeUiClient
        {
            RejectCommandsWith = "Firmware refused the profile."
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var fans = new FansThermalViewModel(connection, CancellationToken.None);
        bool? severityWhenPublished = null;
        fans.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PageViewModel.StatusMessage) &&
                fans.HasStatusMessage)
            {
                severityWhenPublished = fans.StatusIsError;
            }
        };

        fans.ApplyFirmwareAutoCommand.Execute(null);
        await UiTestWait.UntilAsync(() => !fans.ApplyFirmwareAutoCommand.IsRunning);

        Assert.AreEqual(true, severityWhenPublished);
        Assert.IsTrue(fans.StatusIsError);
    }

    /// <summary>Compact mode shows the newest operation result, regardless of source page.</summary>
    [TestMethod]
    public async Task CompactOperationUsesMessageRevisionInsteadOfPagePriority()
    {
        var client = new FakeRuntimeUiClient();
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        using var shell = new ShellViewModel(
            connection,
            new UiSettings { MinimizeToTray = false },
            isDesignPreview: true,
            _ => { });
        using var compact = new CompactControlViewModel(shell);

        Assert.IsTrue(shell.Performance.TrySelectMode("Silent"));
        shell.Performance.ApplyCommand.Execute(null);
        await UiTestWait.UntilAsync(() => !shell.Performance.ApplyCommand.IsRunning);

        Assert.IsFalse(shell.Performance.StatusIsError);
        Assert.AreEqual(shell.Performance.StatusMessage, compact.OperationMessage);
        Assert.AreEqual(StatusTone.Good, compact.OperationTone);

        client.RejectCommandsWith = "Firmware refused the newer fan operation.";
        shell.FansThermal.ApplyFirmwareAutoCommand.Execute(null);
        await UiTestWait.UntilAsync(() => !shell.FansThermal.ApplyFirmwareAutoCommand.IsRunning);

        Assert.IsTrue(shell.FansThermal.StatusMessageRevision >
            shell.Performance.StatusMessageRevision);
        Assert.IsTrue(shell.FansThermal.StatusIsError);
        Assert.AreEqual(shell.FansThermal.StatusMessage, compact.OperationMessage);
        StringAssert.Contains(compact.OperationMessage!, "newer fan operation");
        Assert.IsTrue(compact.OperationIsError);
        Assert.AreEqual(StatusTone.Danger, compact.OperationTone);
    }

    /// <summary>A retained target is hidden once its command is no longer known to be current.</summary>
    /// <remarks>
    /// Runtime status can retain the last dynamic target through a transition or terminal
    /// safety state. The dashboard, Fans page and rolling chart all used to present that number
    /// as current after firmware took over or ownership became uncertain.
    /// </remarks>
    [TestMethod]
    public async Task TransitionAndTerminalStatesDoNotPresentRetainedFanTargetsAsCurrent()
    {
        foreach (string state in new[]
                 {
                     "Starting", "Stopping", "Faulted", "EmergencyHandoff"
                 })
        {
            var client = new FakeRuntimeUiClient
            {
                Status = RuntimeUiSampleData.Status(
                    state: state,
                    currentProfile: "Thermal/default",
                    effectiveFanTargetRpm: 4_200,
                    telemetry: RuntimeUiSampleData.Telemetry()),
                FanState = new FanStateDto(
                    new RuntimeModeDto("Balanced", "Manual", "Balanced", "Manual"),
                    4_200,
                    4_200)
            };
            using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
            await connection.PollOnceAsync(CancellationToken.None);
            var performance = new PerformanceViewModel(connection, CancellationToken.None);
            var dashboard = new DashboardViewModel(
                connection,
                performance,
                CancellationToken.None);
            var fans = new FansThermalViewModel(connection, CancellationToken.None);
            var monitoring = new MonitoringViewModel(connection, CancellationToken.None);
            using var shell = new ShellViewModel(
                connection,
                new UiSettings { MinimizeToTray = false },
                isDesignPreview: true,
                _ => { });
            using var compact = new CompactControlViewModel(shell);

            monitoring.Append(RuntimeUiSampleData.Telemetry(
                timestamp: client.TelemetrySample.Timestamp.AddSeconds(1)));

            Assert.AreEqual(Display.Unavailable, dashboard.FanTarget, state);
            Assert.AreEqual(Display.Unavailable, dashboard.FirmwareFan1Value, state);
            Assert.AreEqual(Display.Unavailable, dashboard.FirmwareFan2Value, state);
            Assert.AreEqual(Display.Unavailable, fans.EffectiveFanTarget, state);
            Assert.AreEqual(Display.Unavailable, fans.ActiveCurve, state);
            Assert.AreEqual(Display.Unavailable, fans.FirmwareFan1Value, state);
            Assert.AreEqual(Display.Unavailable, fans.FirmwareFan2Value, state);
            Assert.AreEqual(Display.Unavailable, fans.FirmwareFanMode, state);
            Assert.AreEqual(Display.Unavailable, compact.FanValue, state);
            Assert.AreEqual(
                state switch
                {
                    "Starting" => "session starting",
                    "Stopping" => "session stopping",
                    "Faulted" => "runtime fault · open Diagnostics",
                    "EmergencyHandoff" => "firmware owns cooling",
                    _ => throw new AssertInconclusiveException($"Unhandled state '{state}'.")
                },
                compact.FanCaption,
                state);
            Assert.IsNull(
                monitoring.History[TelemetryHistory.FanTargetKey].Latest,
                $"State '{state}' must append a chart gap, not the retained target.");
        }

        foreach (string state in new[] { "Stopped", "Running" })
        {
            var client = new FakeRuntimeUiClient
            {
                Status = RuntimeUiSampleData.Status(
                    state: state,
                    currentProfile: state == "Stopped" ? "Fan/Fixed" : "Thermal/default",
                    effectiveFanTargetRpm: 4_200),
                FanState = new FanStateDto(
                    new RuntimeModeDto("Balanced", "Manual", "Balanced", "Manual"),
                    4_200,
                    4_200)
            };
            using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
            await connection.PollOnceAsync(CancellationToken.None);
            var fans = new FansThermalViewModel(connection, CancellationToken.None);

            Assert.AreNotEqual(Display.Unavailable, fans.EffectiveFanTarget, state);
            Assert.AreEqual(
                state == "Stopped"
                    ? Display.FirmwareFanValue(4_200)
                    : Display.Unavailable,
                fans.FirmwareFan1Value,
                state);
        }

        var missingRunningTarget = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(
                state: "Running",
                effectiveFanTargetRpm: null)
        };
        using var missingConnection = new RuntimeConnection(
            missingRunningTarget,
            new ImmediateUiDispatcher());
        await missingConnection.PollOnceAsync(CancellationToken.None);
        var missingPerformance = new PerformanceViewModel(
            missingConnection,
            CancellationToken.None);
        var missingDashboard = new DashboardViewModel(
            missingConnection,
            missingPerformance,
            CancellationToken.None);
        using var missingShell = new ShellViewModel(
            missingConnection,
            new UiSettings { MinimizeToTray = false },
            isDesignPreview: true,
            _ => { });
        using var missingCompact = new CompactControlViewModel(missingShell);

        Assert.AreEqual(Display.Unavailable, missingDashboard.FanTarget);
        StringAssert.Contains(missingDashboard.FanTargetDetail, "has not reported");
        Assert.IsFalse(
            missingDashboard.FanTargetDetail.Contains("firmware Auto", StringComparison.Ordinal),
            "A Running state says Runtime Core owns cooling, even when its target is absent.");
        Assert.AreEqual(Display.Unavailable, missingCompact.FanValue);
        Assert.AreEqual("dynamic target unavailable", missingCompact.FanCaption);
        Assert.AreEqual(StatusTone.Warning, missingCompact.FanTone);
        Assert.AreEqual("Dynamic · target unavailable", missingCompact.FooterText);
        Assert.AreEqual(StatusTone.Warning, missingCompact.FooterTone);
    }

    /// <summary>A real zero-cycle session is not relabelled as a runtime that never ran.</summary>
    /// <remarks>
    /// SessionId is issued before the first scheduler cycle and retained after stop. Looking at
    /// CompletedCycles alone made the dashboard and Diagnostics deny a session whose ID they
    /// displayed at the same time.
    /// </remarks>
    [TestMethod]
    public async Task SessionIdentityDistinguishesZeroCyclesFromNoSessionHistory()
    {
        var client = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(
                state: "Stopped",
                sessionId: Guid.NewGuid(),
                scheduler: RuntimeUiSampleData.Scheduler(completedCycles: 0),
                schedulerHealth: "Healthy")
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var performance = new PerformanceViewModel(connection, CancellationToken.None);
        var dashboard = new DashboardViewModel(
            connection,
            performance,
            CancellationToken.None);
        var diagnostics = new DiagnosticsViewModel(connection, CancellationToken.None);
        diagnostics.Activate();

        Assert.IsFalse(dashboard.HasNoSessionHistory);
        Assert.AreEqual("LAST SESSION SCHEDULER", dashboard.SchedulerLabel);
        StringAssert.Contains(dashboard.SchedulerHealth, "No scheduler cycle");
        DiagnosticItem history = diagnostics.Scheduler.Items.Single(item => item.Label == "History");
        Assert.AreEqual("No scheduler cycle yet", history.Value);
        StringAssert.Contains(history.Detail!, "last thermal session ended");
    }

    [DataTestMethod]
    [DataRow("Starting", "session starting", "is starting")]
    [DataRow("Stopping", "session stopping", "is stopping")]
    public async Task ZeroCycleTransitionDoesNotClaimTheSessionAlreadyEnded(
        string state,
        string expectedTitle,
        string expectedDetail)
    {
        var client = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(
                state: state,
                sessionId: Guid.NewGuid(),
                scheduler: RuntimeUiSampleData.Scheduler(completedCycles: 0),
                schedulerHealth: "Healthy")
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var performance = new PerformanceViewModel(connection, CancellationToken.None);
        var dashboard = new DashboardViewModel(
            connection,
            performance,
            CancellationToken.None);
        var diagnostics = new DiagnosticsViewModel(connection, CancellationToken.None);
        diagnostics.Activate();

        StringAssert.Contains(dashboard.SchedulerLabel, state.ToUpperInvariant());
        StringAssert.Contains(diagnostics.Scheduler.Title, expectedTitle);
        DiagnosticItem history = diagnostics.Scheduler.Items.Single(item => item.Label == "History");
        StringAssert.Contains(history.Detail!, expectedDetail);
        Assert.IsFalse(history.Detail!.Contains("ended", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void DefaultRuntimeCurveSatisfiesEveryMirroredConstraint()
    {
        StoredThermalCurveDocument curve = RuntimeUiSampleData.DefaultCurve();

        ThermalCurveValidationResult cpu = ThermalCurveValidator.Validate(curve.Cpu);
        ThermalCurveValidationResult gpu = ThermalCurveValidator.Validate(curve.Gpu);

        Assert.IsTrue(cpu.IsValid);
        Assert.AreEqual(0, cpu.Errors.Count);
        Assert.IsTrue(gpu.IsValid);
        Assert.AreEqual(0, gpu.Errors.Count);
    }

    [DataTestMethod]
    [DataRow(50.0, 3000, 50.0, 3200, "strictly higher")]
    [DataRow(50.0, 3300, 60.0, 3200, "must not drop")]
    [DataRow(50.0, 3050, 60.0, 3300, "multiple of 100")]
    [DataRow(0.0, 3000, 60.0, 3300, "above 0 C")]
    [DataRow(50.0, 2900, 60.0, 3300, "between 3000 and 5000")]
    public void InvalidCurveShapesAreRejectedWithPointSpecificErrors(
        double firstTemperature,
        int firstRpm,
        double secondTemperature,
        int secondRpm,
        string expected)
    {
        StoredThermalCurvePoint[] points =
        [
            new(firstTemperature, firstRpm),
            new(secondTemperature, secondRpm)
        ];

        ThermalCurveValidationResult result = ThermalCurveValidator.Validate(points);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.PointErrors.Count > 0);
        Assert.IsTrue(result.Errors.Any(error =>
            error.Contains(expected, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CurveNeedsAtLeastTwoPoints()
    {
        ThermalCurveValidationResult result = ThermalCurveValidator.Validate(
            [new StoredThermalCurvePoint(50, 3000)]);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(error =>
            error.Contains("at least 2 points", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CurveEditorMarksEditsDirtyAndSurfacesInlineValidation()
    {
        var editor = new ThermalCurveEditorViewModel();
        editor.Load(RuntimeUiSampleData.DefaultCurve());

        Assert.IsTrue(editor.IsLoadedFromRuntime);
        Assert.IsTrue(editor.IsValid);
        Assert.IsFalse(editor.IsDirty);
        Assert.IsFalse(editor.CanApply);

        editor.CpuPoints[1].TemperatureCelsius = editor.CpuPoints[0].TemperatureCelsius;

        Assert.IsTrue(editor.IsDirty);
        Assert.IsFalse(editor.IsValid);
        Assert.IsTrue(editor.CpuPoints[1].HasError);
        StringAssert.Contains(editor.CpuPoints[1].Error!, "strictly higher");
        Assert.IsTrue(editor.Errors.Any(error => error.StartsWith("CPU curve", StringComparison.Ordinal)));
        StringAssert.Contains(editor.ApplyBlockedReason, "not sent to the runtime");
    }

    [TestMethod]
    public void FixedFanInputsAreClampedSnappedAndLinkedBeforeAnyRequest()
    {
        var client = new FakeRuntimeUiClient();
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        var viewModel = new FansThermalViewModel(connection, CancellationToken.None);

        viewModel.Fan1Target = 1;
        Assert.AreEqual(RuntimeUiPolicy.MinimumFanRpm, viewModel.Fan1Target);
        Assert.AreEqual(RuntimeUiPolicy.MinimumFanRpm, viewModel.Fan2Target);

        viewModel.LinkFans = false;
        viewModel.Fan1Target = 4_949;
        viewModel.Fan2Target = 5_999;

        Assert.AreEqual(4_900, viewModel.Fan1Target);
        Assert.AreEqual(RuntimeUiPolicy.MaximumFanRpm, viewModel.Fan2Target);
        Assert.AreEqual(0, client.FanRequests.Count);
    }

    [TestMethod]
    public async Task InitialCustomRuntimeStateBecomesPendingWithoutApplying()
    {
        var client = new FakeRuntimeUiClient
        {
            PerformanceState = Performance("Custom", "Medium", "Low")
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var viewModel = new PerformanceViewModel(connection, CancellationToken.None);

        viewModel.Refresh();

        Assert.AreEqual("Custom", viewModel.CurrentMode);
        Assert.AreEqual("Custom", viewModel.SelectedMode);
        Assert.AreEqual("Medium", viewModel.SelectedCpuLevel);
        Assert.AreEqual("Low", viewModel.SelectedGpuLevel);
        Assert.AreEqual("Custom · CPU Medium · GPU Low", viewModel.PendingSummary);
        Assert.IsFalse(viewModel.HasPendingChanges);
        Assert.AreEqual(0, client.PerformanceRequests.Count);
    }

    [TestMethod]
    public async Task RestoreSelectionMirrorsCurrentStateWithoutApplying()
    {
        var client = new FakeRuntimeUiClient
        {
            PerformanceState = Performance("Custom", "Medium", "Low")
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var viewModel = new PerformanceViewModel(connection, CancellationToken.None);
        viewModel.Refresh();
        Assert.IsTrue(viewModel.TrySelectMode("Silent"));
        Assert.IsTrue(viewModel.HasPendingChanges);

        viewModel.RestoreFromCurrent();

        Assert.AreEqual("Custom", viewModel.SelectedMode);
        Assert.AreEqual("Medium", viewModel.SelectedCpuLevel);
        Assert.AreEqual("Low", viewModel.SelectedGpuLevel);
        Assert.IsFalse(viewModel.HasPendingChanges);
        Assert.AreEqual(0, client.PerformanceRequests.Count);
    }

    [TestMethod]
    public async Task ExplicitPerformanceRefreshResynchronizesWithoutApplying()
    {
        var client = new FakeRuntimeUiClient
        {
            PerformanceState = Performance("Balanced", "Medium", "Low")
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var viewModel = new PerformanceViewModel(connection, CancellationToken.None);
        viewModel.Refresh();
        Assert.IsTrue(viewModel.TrySelectMode("Silent"));
        client.PerformanceState = Performance("Custom", "Low", "Low");

        viewModel.RefreshCommand.Execute(null);
        await UiTestWait.UntilAsync(() => !viewModel.RefreshCommand.IsRunning);

        Assert.AreEqual("Custom", viewModel.CurrentMode);
        Assert.AreEqual("Custom", viewModel.SelectedMode);
        Assert.AreEqual("Low", viewModel.SelectedCpuLevel);
        Assert.AreEqual("Low", viewModel.SelectedGpuLevel);
        Assert.AreEqual(2, client.PerformanceStateRequestCount);
        Assert.AreEqual(0, client.PerformanceRequests.Count);
    }

    [TestMethod]
    public async Task UnsupportedCurrentCustomLevelsRequireValidatedUserChoices()
    {
        var client = new FakeRuntimeUiClient
        {
            // Overclock: still not sendable, so a machine sitting in it cannot be re-applied
            // from here. High and Medium used to serve this role and are now ordinary choices.
            PerformanceState = Performance("Custom", "Overclock", "Medium")
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var viewModel = new PerformanceViewModel(connection, CancellationToken.None);
        viewModel.Refresh();
        var dashboard = new DashboardViewModel(
            connection,
            viewModel,
            CancellationToken.None);
        dashboard.Refresh();

        Assert.AreEqual("Custom", viewModel.SelectedMode);
        Assert.AreEqual("Overclock", viewModel.SelectedCpuLevel);
        Assert.AreEqual("Medium", viewModel.SelectedGpuLevel);
        Assert.IsFalse(viewModel.CanApply);
        Assert.IsFalse(viewModel.ApplyCommand.CanExecute(null));
        Assert.IsFalse(dashboard.ApplyCustomCommand.CanExecute(null));
        Assert.IsFalse(string.IsNullOrWhiteSpace(viewModel.ApplyBlockedReason));
        Assert.AreEqual(0, client.PerformanceRequests.Count);

        // The reported state is shown accurately; the user just has to pick something this
        // build will send before it can apply.
        Assert.IsTrue(viewModel.TrySelectCpuLevel("Boost"));
        Assert.IsTrue(viewModel.TrySelectGpuLevel("High"));
        dashboard.Refresh();

        Assert.IsTrue(viewModel.CanApply);
        Assert.IsTrue(dashboard.ApplyCustomCommand.CanExecute(null));
        Assert.AreEqual(0, client.PerformanceRequests.Count);
    }

    [TestMethod]
    public async Task DisagreeingRuntimeZonesAreShownWithoutBalancedSubstitution()
    {
        var client = new FakeRuntimeUiClient
        {
            PerformanceState = new PerformanceStateDto(
                new RuntimeModeDto("Balanced", "Auto", "Silent", "Auto"),
                "Medium",
                "Low",
                0,
                0)
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var viewModel = new PerformanceViewModel(connection, CancellationToken.None);

        viewModel.Refresh();

        Assert.AreEqual("Balanced / Silent", viewModel.SelectedMode);
        StringAssert.Contains(viewModel.CurrentSummary, "Zone 1 Balanced");
        StringAssert.Contains(viewModel.CurrentSummary, "Zone 2 Silent");
        Assert.IsFalse(viewModel.CanApply);
        Assert.IsTrue(viewModel.HasPendingChanges);
        Assert.AreEqual(0, client.PerformanceRequests.Count);

        Assert.IsTrue(viewModel.TrySelectMode("Silent"));
        Assert.IsTrue(viewModel.CanApply);
        Assert.AreEqual(0, client.PerformanceRequests.Count);
    }

    [TestMethod]
    public async Task NormalPollingDoesNotClobberPendingPerformanceEdit()
    {
        var client = new FakeRuntimeUiClient
        {
            PerformanceState = Performance("Custom", "Medium", "Low")
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var viewModel = new PerformanceViewModel(connection, CancellationToken.None);
        viewModel.Refresh();
        Assert.IsTrue(viewModel.TrySelectMode("Silent"));
        client.PerformanceState = Performance("Balanced", "Low", "Low");

        for (int tick = 0; tick < 20; tick++)
        {
            await connection.PollOnceAsync(CancellationToken.None);
            viewModel.Refresh();
        }

        Assert.AreEqual("Silent", viewModel.SelectedMode);
        Assert.AreEqual(1, client.PerformanceStateRequestCount);
        Assert.AreEqual(1, client.FanStateRequestCount);
        Assert.AreEqual(0, client.PerformanceRequests.Count);
    }

    private static PerformanceStateDto Performance(
        string mode,
        string cpu,
        string gpu) => new(
        new RuntimeModeDto(mode, "Auto", mode, "Auto"),
        cpu,
        gpu,
        0,
        0);
}
