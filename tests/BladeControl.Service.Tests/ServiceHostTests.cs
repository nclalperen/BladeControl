using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace BladeControl.Service.Tests;

/// <summary>
/// Hosting behaviour: how the process decides to run, that an SCM stop reaches the safe
/// shutdown path, and that two hosts cannot own the hardware at once.
/// </summary>
/// <remarks>
/// The runtime host body is injected throughout, so nothing here opens HID, telemetry
/// providers or the embedded controller, and no service is registered with the SCM.
/// </remarks>
[TestClass]
public sealed class ServiceHostTests
{
    // --- Mode selection -------------------------------------------------------------------

    [TestMethod]
    public void NoArgumentsUnderTheScmSelectsTheWindowsServiceLifetime() =>
        Assert.AreEqual(
            RuntimeHostMode.WindowsService,
            RuntimeHostBuilder.SelectMode([], isWindowsService: true));

    /// <summary>
    /// Double-clicking the executable must not silently try to be a service; it prints usage.
    /// </summary>
    [TestMethod]
    public void NoArgumentsOutsideTheScmPrintsUsageRatherThanGuessing() =>
        Assert.AreEqual(
            RuntimeHostMode.Usage,
            RuntimeHostBuilder.SelectMode([], isWindowsService: false));

    [DataTestMethod]
    [DataRow("console", RuntimeHostMode.Console)]
    [DataRow("CONSOLE", RuntimeHostMode.Console)]
    public void ConsoleSwitchSelectsForegroundHost(string argument, RuntimeHostMode expected) =>
        Assert.AreEqual(
            expected,
            RuntimeHostBuilder.SelectMode([argument], isWindowsService: false));

    [TestMethod]
    public void VerboseConsoleIsRecognised() =>
        Assert.AreEqual(
            RuntimeHostMode.VerboseConsole,
            RuntimeHostBuilder.SelectMode(["console", "--verbose"], isWindowsService: false));

    /// <summary>An explicit switch wins over what Windows reports, so tooling stays predictable.</summary>
    [TestMethod]
    public void ExplicitServiceSwitchIsHonouredEvenOutsideTheScm() =>
        Assert.AreEqual(
            RuntimeHostMode.WindowsService,
            RuntimeHostBuilder.SelectMode(["--service"], isWindowsService: false));

    [DataTestMethod]
    [DataRow("install")]
    [DataRow("uninstall")]
    [DataRow("start")]
    [DataRow("stop")]
    [DataRow("--install")]
    public void ServiceManagementVerbsAreNotAccepted(string verb)
    {
        // Installing and removing the service is the installer's job. Keeping these out of
        // the runtime host means a mistaken or hostile invocation cannot reconfigure the SCM.
        Assert.AreEqual(
            RuntimeHostMode.Usage,
            RuntimeHostBuilder.SelectMode([verb], isWindowsService: false));
    }

    // --- Stop reaches the safe shutdown path ----------------------------------------------

    [TestMethod]
    public async Task StoppingTheServiceCancelsTheRuntimeSoSafeShutdownRuns()
    {
        var started = new TaskCompletionSource();
        var shutdownCompleted = new TaskCompletionSource();
        bool sawCancellation = false;

        using IHost host = RuntimeHostBuilder.BuildServiceHost(
            run: async token =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    // This is the seam the real host body uses: cancellation unwinds RunAsync
                    // and its finally block disposes the dispatcher and then the runtime,
                    // which is what restores firmware state and stops any thermal session.
                    sawCancellation = true;
                    shutdownCompleted.TrySetResult();
                    throw;
                }

                return 0;
            },
            singletonFactory: FakeSingleton.Owned);

        await host.StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await host.StopAsync();

        await shutdownCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(sawCancellation, "An SCM stop must cancel the runtime host body.");
    }

    [TestMethod]
    public void ShutdownTimeoutLeavesRoomForFirmwareRestoration()
    {
        using IHost host = RuntimeHostBuilder.BuildServiceHost(
            run: token => Task.Delay(Timeout.Infinite, token).ContinueWith(_ => 0),
            singletonFactory: FakeSingleton.Owned);

        var options = host.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<HostOptions>>().Value;

        // The safe shutdown path drains events and restores firmware fan mode. A default
        // five-second budget would truncate that; anything above ~20 s is enough and stays
        // inside the stop hint reported to the SCM.
        Assert.IsTrue(
            options.ShutdownTimeout >= TimeSpan.FromSeconds(20),
            $"ShutdownTimeout was {options.ShutdownTimeout}, too short for safe shutdown.");
    }

    [TestMethod]
    public async Task ExitCodeFromTheRuntimeIsReportedToTheServiceControlManager()
    {
        using IHost host = RuntimeHostBuilder.BuildServiceHost(
            run: _ => Task.FromResult(1),
            singletonFactory: FakeSingleton.Owned);

        await host.StartAsync();
        await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(
            1,
            host.Services.GetRequiredService<RuntimeBackgroundService>().ExitCode,
            "A refused initialisation must surface as a non-zero service exit code.");
    }

    // --- No duplicate runtime -------------------------------------------------------------

    [TestMethod]
    public async Task ASecondHostRefusesToRunAndNeverOpensHardware()
    {
        bool bodyRan = false;

        using IHost host = RuntimeHostBuilder.BuildServiceHost(
            run: _ =>
            {
                bodyRan = true;
                return Task.FromResult(0);
            },
            singletonFactory: FakeSingleton.NotOwned);

        await host.StartAsync();
        await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsFalse(
            bodyRan,
            "Losing the host singleton must stop the process before any device is opened.");
        Assert.AreEqual(
            3,
            host.Services.GetRequiredService<RuntimeBackgroundService>().ExitCode);
    }

    [TestMethod]
    public void HostSingletonIsMachineWideSoItSpansTheServiceAndAnInteractiveSession()
    {
        // Session-scoped (Local\) would let a console host in the user's session and the
        // service in session 0 both believe they were alone.
        StringAssert.StartsWith(RuntimeServiceIdentity.HostSingletonName, @"Global\");
    }

    [TestMethod]
    public void SecondSingletonInThisProcessDoesNotGetOwnership()
    {
        string name = $@"Local\BladeControl.Test.{Guid.NewGuid():N}";

        using RuntimeHostSingleton first = RuntimeHostSingleton.Acquire(name);
        Assert.IsTrue(first.IsOwner, "The first host must take ownership.");

        using RuntimeHostSingleton second = RuntimeHostSingleton.Acquire(name);
        Assert.IsFalse(second.IsOwner, "A second host must be refused.");
    }

    [TestMethod]
    public void ReleasingTheSingletonLetsTheNextHostStart()
    {
        string name = $@"Local\BladeControl.Test.{Guid.NewGuid():N}";

        using (RuntimeHostSingleton first = RuntimeHostSingleton.Acquire(name))
        {
            Assert.IsTrue(first.IsOwner);
        }

        // After a clean stop — or an upgrade replacing the service — the next host must be
        // able to claim the hardware.
        using RuntimeHostSingleton next = RuntimeHostSingleton.Acquire(name);
        Assert.IsTrue(next.IsOwner);
    }

    // --- The test host must stay out of the installed product's diagnostics ----------------

    /// <summary>
    /// A test run must not write into the Windows Event Log under the installed product's
    /// source.
    /// </summary>
    /// <remarks>
    /// It did. Every `dotnet test` run logged host-lifetime errors as "BladeControl Runtime",
    /// so someone diagnosing the installed service saw entries such as "Another BladeControl
    /// Runtime host already owns the hardware (Local\BladeControl.Test.&lt;guid&gt;)" that had
    /// nothing to do with their machine. The provider is now registered only when the process
    /// really is a Windows service.
    /// </remarks>
    [TestMethod]
    public void HostDoesNotRegisterTheEventLogProviderOutsideAWindowsService()
    {
        Assert.IsFalse(
            WindowsServiceHelpers.IsWindowsService(),
            "Sanity: the test host is not a Windows service.");

        using IHost host = RuntimeHostBuilder.BuildServiceHost(
            run: _ => Task.FromResult(0),
            singletonFactory: FakeSingleton.Owned);

        Assert.IsFalse(
            host.Services.GetServices<ILoggerProvider>().Any(provider =>
                provider.GetType().Name.Contains("EventLog", StringComparison.OrdinalIgnoreCase)),
            "A test or console run must not log into the installed product's event source.");
    }

    // --- Service identity -----------------------------------------------------------------

    [TestMethod]
    public void ServiceIdentityMatchesWhatTheInstallerRegisters()
    {
        // These strings appear in installer/Product.wxs. If they drift, the SCM registration
        // and the running process disagree about who they are.
        Assert.AreEqual("BladeControl.Runtime", RuntimeServiceIdentity.ServiceName);
        Assert.AreEqual("BladeControl Runtime", RuntimeServiceIdentity.DisplayName);
        Assert.AreEqual(RuntimeServiceIdentity.ServiceName, RuntimeWindowsHost.ServiceName);
        Assert.IsFalse(string.IsNullOrWhiteSpace(RuntimeServiceIdentity.Description));
    }

    private static class FakeSingleton
    {
        internal static RuntimeHostSingleton Owned() =>
            RuntimeHostSingleton.Acquire($@"Local\BladeControl.Test.{Guid.NewGuid():N}");

        internal static RuntimeHostSingleton NotOwned()
        {
            // Hold the name first, so the instance handed to the host loses the race.
            string name = $@"Local\BladeControl.Test.{Guid.NewGuid():N}";
            RuntimeHostSingleton holder = RuntimeHostSingleton.Acquire(name);
            Assert.IsTrue(holder.IsOwner);
            Holders.Add(holder);
            return RuntimeHostSingleton.Acquire(name);
        }

        // Kept alive for the duration of the test run; disposing would release the name.
        private static readonly List<RuntimeHostSingleton> Holders = [];
    }
}
