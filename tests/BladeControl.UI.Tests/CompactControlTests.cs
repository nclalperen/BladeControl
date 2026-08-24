using BladeControl.Runtime;
using BladeControl.UI.Ipc;
using BladeControl.UI.Services;
using BladeControl.UI.ViewModels;

namespace BladeControl.UI.Tests;

[TestClass]
public sealed class CompactControlTests
{
    [TestMethod]
    public void DefaultLaunchIsCompactAndFullPreferenceIsRetained()
    {
        Assert.AreEqual(UiLaunchMode.Compact, new UiSettings().LaunchMode);
        Assert.AreEqual(
            InitialUiSurface.Compact,
            UiStartupPolicy.SelectInitialSurface(new UiSettings()));
        Assert.AreEqual(
            InitialUiSurface.Full,
            UiStartupPolicy.SelectInitialSurface(
                new UiSettings { LaunchMode = UiLaunchMode.Full }));
        var shell = CreateShell(out _, out _);
        try
        {
            Assert.AreEqual(UiLaunchMode.Compact, shell.LaunchMode);
            shell.LaunchMode = UiLaunchMode.Full;
            Assert.AreEqual(UiLaunchMode.Full, shell.CaptureSettings(1100, 720, false).LaunchMode);
        }
        finally
        {
            shell.Dispose();
        }
    }

    [TestMethod]
    public void CompactAndFullModelsShareOneConnectionAndStartingTwiceKeepsOnePoller()
    {
        var shell = CreateShell(out _, out RuntimeConnection connection);
        using var compact = new CompactControlViewModel(shell);
        try
        {
            Assert.AreSame(connection, shell.Connection);
            Assert.AreSame(connection, compact.Connection);

            connection.Start();
            Task firstLoop = connection.Completion;
            connection.Start();
            Assert.AreSame(firstLoop, connection.Completion);
        }
        finally
        {
            shell.Dispose();
        }
    }

    [TestMethod]
    public void PlacementStaysInsidePrimaryWorkAreaAtScaledDpi()
    {
        var work = new PixelRect(0, 0, 1920, 1040);
        PixelRect result = CompactWindowPlacement.Calculate(work, 400, 500, 1.25, 1.25);

        Assert.AreEqual(new PixelRect(1405, 400, 500, 625), result);
        Assert.IsTrue(result.Left >= work.Left && result.Right <= work.Right);
        Assert.IsTrue(result.Top >= work.Top && result.Bottom <= work.Bottom);
    }

    [TestMethod]
    public void PlacementSupportsNegativeCoordinatesAndPerMonitorDpi()
    {
        var work = new PixelRect(-2560, 0, 2560, 1440);
        PixelRect result = CompactWindowPlacement.Calculate(work, 400, 560, 1.5, 1.5);

        Assert.AreEqual(new PixelRect(-618, 582, 600, 840), result);
        Assert.IsTrue(result.Left >= work.Left && result.Right <= work.Right);
        Assert.IsTrue(result.Top >= work.Top && result.Bottom <= work.Bottom);
    }

    [DataTestMethod]
    [DataRow("Balanced")]
    [DataRow("Silent")]
    public async Task ValidatedPresetAppliesExactlyOnceThroughTypedClient(string mode)
    {
        (ShellViewModel shell, FakeRuntimeUiClient client) = await CreateOnlineShellAsync();
        using var compact = new CompactControlViewModel(shell);
        try
        {
            (mode == "Balanced" ? compact.SelectBalancedCommand : compact.SelectSilentCommand)
                .Execute(null);
            await UiTestWait.UntilAsync(() => client.PerformanceRequests.Count == 1 &&
                !compact.Connection.IsCommandInFlight);

            Assert.AreEqual(mode, client.PerformanceRequests[0].Mode);
            Assert.IsNull(client.PerformanceRequests[0].CpuLevel);
            Assert.IsNull(client.PerformanceRequests[0].GpuLevel);
        }
        finally
        {
            shell.Dispose();
        }
    }

    [TestMethod]
    public async Task CustomMediumLowAppliesExactlyOnce()
    {
        (ShellViewModel shell, FakeRuntimeUiClient client) = await CreateOnlineShellAsync();
        using var compact = new CompactControlViewModel(shell);
        try
        {
            // Through the path the compact window now uses: the level lists bind their
            // selection directly, rather than one command per level.
            compact.SelectCustomCommand.Execute(null);
            compact.Performance.SelectedCpuLevel = "Medium";
            compact.ApplyCustomCommand.Execute(null);
            await UiTestWait.UntilAsync(() => client.PerformanceRequests.Count == 1 &&
                !compact.Connection.IsCommandInFlight);

            ApplyPerformanceProfileRequest request = client.PerformanceRequests[0];
            Assert.AreEqual("Custom", request.Mode);
            Assert.AreEqual("Medium", request.CpuLevel);
            Assert.AreEqual("Low", request.GpuLevel);
        }
        finally
        {
            shell.Dispose();
        }
    }

    [TestMethod]
    public async Task PerformanceApplyIsDisabledWhileRuntimeRuns()
    {
        (ShellViewModel shell, FakeRuntimeUiClient client) =
            await CreateOnlineShellAsync("Running");
        using var compact = new CompactControlViewModel(shell);
        try
        {
            Assert.IsFalse(compact.SelectSilentCommand.CanExecute(null));
            compact.SelectSilentCommand.Execute(null);
            Assert.AreEqual(0, client.PerformanceRequests.Count);
        }
        finally
        {
            shell.Dispose();
        }
    }

    [TestMethod]
    public async Task FailedApplyRefreshesAndRevertsToAuthoritativeModeWithoutRetry()
    {
        (ShellViewModel shell, FakeRuntimeUiClient client) = await CreateOnlineShellAsync();
        using var compact = new CompactControlViewModel(shell);
        try
        {
            client.RejectCommandsWith = "Firmware refused the profile.";
            compact.SelectSilentCommand.Execute(null);
            await UiTestWait.UntilAsync(() => client.PerformanceRequests.Count == 1 &&
                compact.Performance.HasStatusMessage && !compact.Connection.IsCommandInFlight);

            Assert.AreEqual("Balanced", compact.Performance.SelectedMode);
            Assert.AreEqual(1, client.PerformanceRequests.Count);
            Assert.IsTrue(compact.Performance.StatusIsError);
            StringAssert.Contains(compact.Performance.StatusMessage, "Firmware refused");
        }
        finally
        {
            shell.Dispose();
        }
    }

    [TestMethod]
    public async Task FixedSlidersClampAndSnapWithoutWritesThenApplyOneTypedRequest()
    {
        (ShellViewModel shell, FakeRuntimeUiClient client) = await CreateOnlineShellAsync();
        using var compact = new CompactControlViewModel(shell);
        try
        {
            compact.SelectFixedCommand.Execute(null);
            compact.Fans.LinkFans = false;
            compact.Fans.Fan1Target = 1_950;
            compact.Fans.Fan2Target = 5_049;
            Assert.AreEqual(2_000, compact.Fans.Fan1Target);
            Assert.AreEqual(5_000, compact.Fans.Fan2Target);
            Assert.AreEqual(0, client.FanRequests.Count, "Dragging sliders must not write.");

            compact.ApplyFixedCommand.Execute(null);
            await UiTestWait.UntilAsync(() => client.FanRequests.Count == 1 &&
                !compact.Connection.IsCommandInFlight);
            Assert.AreEqual(new ApplyFanProfileRequest("Fixed", 2_000, 5_000),
                client.FanRequests[0]);
        }
        finally
        {
            shell.Dispose();
        }
    }

    [TestMethod]
    public async Task AutoUsesOneTypedIpcRequest()
    {
        (ShellViewModel shell, FakeRuntimeUiClient client) = await CreateOnlineShellAsync();
        using var compact = new CompactControlViewModel(shell);
        try
        {
            compact.SelectFirmwareCommand.Execute(null);
            await UiTestWait.UntilAsync(() => client.FanRequests.Count == 1 &&
                !compact.Connection.IsCommandInFlight);
            Assert.AreEqual(new ApplyFanProfileRequest("Auto", null, null), client.FanRequests[0]);

            // Handing the fans back is a fan operation. It must not carry a performance change
            // with it: the two are independent, and the user did not ask to change power mode.
            Assert.AreEqual(0, client.PerformanceRequests.Count);
        }
        finally
        {
            shell.Dispose();
        }
    }

    [TestMethod]
    public async Task DynamicStartAndStopUseTypedIpcAndNeverRetry()
    {
        (ShellViewModel shell, FakeRuntimeUiClient client) = await CreateOnlineShellAsync();
        using var compact = new CompactControlViewModel(shell);
        try
        {
            compact.SelectDynamicCommand.Execute(null);
            compact.StartDynamicCommand.Execute(null);
            await UiTestWait.UntilAsync(() => client.StartThermalRequests.Count == 1 &&
                !compact.Connection.IsCommandInFlight);
            Assert.AreEqual("default", client.StartThermalRequests[0]);

            compact.StopDynamicCommand.Execute(null);
            await UiTestWait.UntilAsync(() => client.StopThermalRequestCount == 1 &&
                !compact.Connection.IsCommandInFlight);
            Assert.AreEqual(1, client.StopThermalRequestCount);
        }
        finally
        {
            shell.Dispose();
        }
    }

    [TestMethod]
    public async Task ThermalNotReadyDisablesDynamicStartAndShowsReason()
    {
        (ShellViewModel shell, FakeRuntimeUiClient client) = await CreateOnlineShellAsync(
            configure: fake =>
            fake.Doctor = fake.Doctor with
            {
                ThermalOwnershipReady = false,
                Reasons = ["CPU telemetry is unavailable."]
            });
        using var compact = new CompactControlViewModel(shell);
        try
        {
            compact.SelectDynamicCommand.Execute(null);
            Assert.IsFalse(compact.StartDynamicCommand.CanExecute(null));
            Assert.IsTrue(compact.HasDynamicBlockedReason);
            StringAssert.Contains(compact.DynamicBlockedReason, "CPU telemetry");
            compact.StartDynamicCommand.Execute(null);
            Assert.AreEqual(0, client.StartThermalRequests.Count);
        }
        finally
        {
            shell.Dispose();
        }
    }

    [TestMethod]
    public async Task HidingOrExitingUiDoesNotStopRuntimeSession()
    {
        (ShellViewModel shell, FakeRuntimeUiClient client) =
            await CreateOnlineShellAsync("Running");
        var compact = new CompactControlViewModel(shell);

        compact.Dispose(); // Equivalent to hiding/closing the compact presentation.
        Assert.AreEqual(0, client.StopThermalRequestCount);
        shell.Dispose(); // UI process disposal also issues no thermal command.
        Assert.AreEqual(0, client.StopThermalRequestCount);
    }

    [TestMethod]
    public async Task StoppedHistoricalHealthIsMutedAndExplicitlyLabelled()
    {
        DateTimeOffset old = DateTimeOffset.UtcNow.AddMinutes(-1);
        (ShellViewModel shell, _) = await CreateOnlineShellAsync(configure: fake =>
        {
            fake.Status = RuntimeUiSampleData.Status(
                state: "Stopped",
                telemetry: RuntimeUiSampleData.Telemetry(timestamp: old),
                // A session that actually ran: nine slow cycles out of twelve hundred. The
                // fixture previously left completedCycles at zero, which describes a session
                // that produced slow cycles without producing any cycles.
                scheduler: RuntimeUiSampleData.Scheduler(
                    completedCycles: 1200,
                    slowCycleCount: 9),
                schedulerHealth: "Degraded · overruns",
                watchdog: RuntimeUiSampleData.Watchdog());
        }, now: () => DateTimeOffset.UtcNow);
        using var compact = new CompactControlViewModel(shell);
        try
        {
            Assert.AreEqual(StatusTone.Muted, shell.Dashboard.SchedulerHealthTone);
            Assert.AreEqual("LAST SESSION SCHEDULER", shell.Dashboard.SchedulerLabel);
            Assert.AreEqual("LAST WATCHDOG OBSERVATION", shell.Dashboard.FirmwareFanModeLabel);
            Assert.AreEqual(StatusTone.Muted, compact.TelemetryTone);
        }
        finally
        {
            shell.Dispose();
        }
    }

    private static ShellViewModel CreateShell(
        out FakeRuntimeUiClient client,
        out RuntimeConnection connection)
    {
        client = new FakeRuntimeUiClient();
        connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        return new ShellViewModel(connection, new UiSettings(), true);
    }

    /// <summary>
    /// A runtime that has served no session says so, rather than reporting a healthy one.
    /// </summary>
    /// <remarks>
    /// Stopped and never-run are different. A stopped runtime that ran a session has a last
    /// session to report; one that has never run a session does not, and labelling its zeroes
    /// "LAST SESSION SCHEDULER ... Healthy" describes a session that never happened. This is the
    /// same defect as rendering an unreported metric as zero, one level up.
    /// </remarks>
    [TestMethod]
    public async Task ARuntimeWithNoSessionDoesNotReportAHealthyLastSession()
    {
        (ShellViewModel shell, _) = await CreateOnlineShellAsync(configure: fake =>
        {
            fake.Status = RuntimeUiSampleData.Status(
                state: "Stopped",
                scheduler: RuntimeUiSampleData.Scheduler(completedCycles: 0),
                schedulerHealth: "Healthy",
                health: new TelemetryHealthDto("Healthy", "Last read succeeded.", true));
        });
        using var compact = new CompactControlViewModel(shell);
        try
        {
            Assert.IsTrue(shell.Dashboard.HasNoSessionHistory);
            Assert.AreEqual("SCHEDULER", shell.Dashboard.SchedulerLabel);
            StringAssert.Contains(shell.Dashboard.SchedulerHealth, "No thermal session");
            Assert.AreNotEqual("Healthy", shell.Dashboard.SchedulerHealth);
            Assert.AreEqual("TELEMETRY", shell.Dashboard.TelemetryLabel);
            Assert.AreEqual(Display.NoSessionTelemetry, shell.Dashboard.TelemetryHealth);
            Assert.AreEqual(
                Display.NoSessionTelemetryDetail,
                shell.Dashboard.TelemetryHealthDetail);
            Assert.AreEqual(StatusTone.Muted, shell.Dashboard.TelemetryHealthTone);
            Assert.AreEqual(Display.NoSessionTelemetry, shell.FansThermal.TelemetryHealth);
            Assert.AreEqual(
                Display.NoSessionTelemetryDetail,
                shell.FansThermal.TelemetryHealthDetail);
            Assert.AreEqual(StatusTone.Muted, shell.FansThermal.TelemetryHealthTone);
        }
        finally
        {
            shell.Dispose();
        }
    }

    /// <summary>
    /// The full app's emergency banner reads as safe-but-latched, not as a fault.
    /// </summary>
    /// <remarks>
    /// The banner was hardcoded to the danger palette, so an emergency handoff rendered in red
    /// while its own text said "the machine is safe" — the colour contradicting the sentence.
    /// Protection having worked is not protection having failed. A genuine fault stays red.
    /// </remarks>
    [TestMethod]
    public async Task EmergencyHandoffBannerIsWarningAndAFaultStaysDanger()
    {
        (ShellViewModel handoff, _) = await CreateOnlineShellAsync("EmergencyHandoff");
        try
        {
            Assert.IsTrue(handoff.Dashboard.HasRuntimeAlert);
            Assert.AreEqual(StatusTone.Warning, handoff.Dashboard.RuntimeAlertTone);

            string alert = handoff.Dashboard.RuntimeAlert!;
            StringAssert.Contains(alert, "firmware owns cooling");
            StringAssert.Contains(alert, "service is still running");
            StringAssert.Contains(alert, "will not resume by itself");
        }
        finally
        {
            handoff.Dispose();
        }

        (ShellViewModel faulted, _) = await CreateOnlineShellAsync(configure: fake =>
        {
            fake.Status = RuntimeUiSampleData.Status(
                state: "Faulted",
                lastFailureReason: "Razer HID transport closed unexpectedly.");
        });
        try
        {
            Assert.AreEqual(StatusTone.Danger, faulted.Dashboard.RuntimeAlertTone);
        }
        finally
        {
            faulted.Dispose();
        }
    }

    // --- The FAN tile is the part most able to lie ----------------------------------------

    /// <summary>
    /// Under firmware Auto the fan tile shows no number and names who owns cooling.
    /// </summary>
    /// <remarks>
    /// There is no measured fan speed on this machine — 0x0D81 echoes the commanded target,
    /// LibreHardwareMonitor reports no fan sensors, NVML reports fan speed unavailable. Under
    /// Auto there is not even a BladeControl target, so any number here would be a stale one
    /// from a previous mode dressed as the current state.
    /// </remarks>
    [TestMethod]
    public async Task FirmwareAutoShowsNoFanNumberAndNamesTheOwner()
    {
        (ShellViewModel shell, _) = await CreateOnlineShellAsync();
        using var compact = new CompactControlViewModel(shell);
        try
        {
            compact.Fans.Mode = CoolingMode.FirmwareAuto;

            Assert.AreEqual(Display.Unavailable, compact.FanValue);
            Assert.AreEqual("FIRMWARE", compact.FanHeading);
            Assert.IsTrue(compact.IsFirmwareSelected);
        }
        finally
        {
            shell.Dispose();
        }
    }

    /// <summary>An unapplied fixed target is shown as a selection, never current fan state.</summary>
    [TestMethod]
    public async Task FixedModeLabelsTheNumberAsATargetNotASpeed()
    {
        (ShellViewModel shell, _) = await CreateOnlineShellAsync();
        using var compact = new CompactControlViewModel(shell);
        try
        {
            compact.SelectFixedCommand.Execute(null);
            compact.FanTarget = 3400;

            Assert.AreEqual("FAN", compact.FanHeading);
            StringAssert.Contains(compact.FanValue, "3");
            Assert.AreEqual("selected target · not applied", compact.FanCaption);
            Assert.IsFalse(
                compact.FanCaption.Contains("RPM", StringComparison.OrdinalIgnoreCase),
                "No measured-speed claim: nothing here is a tachometer reading.");
        }
        finally
        {
            shell.Dispose();
        }
    }

    /// <summary>
    /// One fan control drives both zones; the runtime still receives them separately.
    /// </summary>
    /// <remarks>
    /// The compact window used to expose Fan 1 and Fan 2 as independent sliders, which asked
    /// the user for a decision they have no basis to make and let the zones drift apart for no
    /// benefit. Zone-aware safety is unchanged underneath.
    /// </remarks>
    [TestMethod]
    public async Task TheSingleFanControlKeepsBothZonesTogether()
    {
        (ShellViewModel shell, _) = await CreateOnlineShellAsync();
        using var compact = new CompactControlViewModel(shell);
        try
        {
            compact.SelectFixedCommand.Execute(null);
            compact.FanTarget = 4200;

            Assert.AreEqual(4200, compact.Fans.Fan1Target);
            Assert.AreEqual(4200, compact.Fans.Fan2Target);
            Assert.IsTrue(compact.Fans.LinkFans);
        }
        finally
        {
            shell.Dispose();
        }
    }

    /// <summary>
    /// An emergency handoff is presented as finished, not as something in progress.
    /// </summary>
    [TestMethod]
    public async Task EmergencyHandoffReadsAsTerminalAndSaysWhatToDo()
    {
        (ShellViewModel shell, _) = await CreateOnlineShellAsync("EmergencyHandoff");
        using var compact = new CompactControlViewModel(shell);
        try
        {
            Assert.IsTrue(compact.IsEmergencyHandoff);
            StringAssert.Contains(compact.EmergencyTitle, "Firmware Auto");

            // Nothing resolves on its own, and the interface has to say so rather than
            // implying that waiting is the remedy.
            Assert.IsFalse(
                compact.EmergencyAction.Contains("in progress", StringComparison.OrdinalIgnoreCase));
            StringAssert.Contains(compact.EmergencyAction, "will not resume");

            // And no stale commanded value presented as the current fan state.
            Assert.AreEqual(Display.Unavailable, compact.FanValue);
        }
        finally
        {
            shell.Dispose();
        }
    }

    /// <summary>
    /// The compact window offers every level this build will send, and does not list Overclock.
    /// </summary>
    /// <remarks>
    /// Overclock is excluded so BladeControl cannot interfere with tuning done in XTU. It stays
    /// a value the runtime can read and report — a machine already in it is described
    /// accurately — but it is not offered as a choice anywhere.
    /// </remarks>
    [TestMethod]
    public async Task EveryOfferedPerformanceLevelIsSelectableAndOverclockIsAbsent()
    {
        (ShellViewModel shell, _) = await CreateOnlineShellAsync();
        using var compact = new CompactControlViewModel(shell);
        try
        {
            CollectionAssert.AreEquivalent(
                new[] { "Low", "Medium", "High", "Boost" },
                compact.CpuLevels.Select(level => level.Value).ToArray());
            CollectionAssert.AreEquivalent(
                new[] { "Low", "Medium", "High" },
                compact.GpuLevels.Select(level => level.Value).ToArray());

            Assert.IsTrue(
                compact.CpuLevels.All(level => level.IsAvailable),
                "Anything offered must be selectable.");
            Assert.IsTrue(compact.GpuLevels.All(level => level.IsAvailable));
            Assert.IsFalse(compact.CpuLevels.Any(level => level.Value == "Overclock"));
        }
        finally
        {
            shell.Dispose();
        }
    }

    private static async Task<(ShellViewModel Shell, FakeRuntimeUiClient Client)>
        CreateOnlineShellAsync(
        string state = "Stopped",
        Action<FakeRuntimeUiClient>? configure = null,
        Func<DateTimeOffset>? now = null)
    {
        var client = new FakeRuntimeUiClient();
        client.Status = client.Status with { State = state };
        configure?.Invoke(client);
        var connection = new RuntimeConnection(client, new ImmediateUiDispatcher(), now: now);
        await connection.PollOnceAsync(CancellationToken.None);
        return (new ShellViewModel(connection, new UiSettings(), true), client);
    }
}
