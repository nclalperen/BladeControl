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
            compact.SelectCustomCommand.Execute(null);
            compact.SelectCpuMediumCommand.Execute(null);
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
            compact.SelectAutoCommand.Execute(null);
            await UiTestWait.UntilAsync(() => client.FanRequests.Count == 1 &&
                !compact.Connection.IsCommandInFlight);
            Assert.AreEqual(new ApplyFanProfileRequest("Auto", null, null), client.FanRequests[0]);
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
                scheduler: RuntimeUiSampleData.Scheduler(slowCycleCount: 9),
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
