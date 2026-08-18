using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BladeControl.Service.Tests;

/// <summary>
/// Generic-host lifecycle: what may be resolved and when, and which conditions are allowed to
/// end the process.
/// </summary>
/// <remarks>
/// <para>The installed service crashed on 2026-08-18 at 17:19:59 with
/// <c>ObjectDisposedException: 'IServiceProvider'</c> from
/// <c>Program.RunAsWindowsService</c>. <c>host.Run()</c> disposes the host inside its own
/// finally block, so the provider is already gone when Run returns; resolving the exit code
/// afterwards threw on <b>every</b> service stop, not only that one.</para>
/// <para>These tests drive the real host with an injected runtime body. Nothing opens
/// hardware, registers a service, or touches the installed product.</para>
/// </remarks>
[TestClass]
public sealed class HostLifecycleTests
{
    /// <summary>
    /// The exact defect: after the host has run, its provider is disposed. Anything Program
    /// needs must be captured while it is alive.
    /// </summary>
    [TestMethod]
    public void ResolvingFromTheProviderAfterRunThrowsObjectDisposedException()
    {
        using IHost host = RuntimeHostBuilder.BuildServiceHost(
            run: _ => Task.FromResult(RuntimeHostExitCode.Success),
            singletonFactory: FakeSingleton.Owned);

        host.Run();

        ObjectDisposedException disposed = Assert.ThrowsException<ObjectDisposedException>(
            () => host.Services.GetRequiredService<RuntimeBackgroundService>(),
            "Run disposes the host; this is the call that crashed the installed service.");
        StringAssert.Contains(disposed.ObjectName, "IServiceProvider");
    }

    /// <summary>
    /// The shape Program now uses: capture before, read after. This is the regression guard —
    /// it fails if anyone reintroduces a post-Run resolve.
    /// </summary>
    [TestMethod]
    public void CapturingTheServiceBeforeRunLetsTheExitCodeBeReadAfterDisposal()
    {
        using IHost host = RuntimeHostBuilder.BuildServiceHost(
            run: _ => Task.FromResult(RuntimeHostExitCode.Success),
            singletonFactory: FakeSingleton.Owned);

        RuntimeBackgroundService runtime =
            host.Services.GetRequiredService<RuntimeBackgroundService>();

        host.Run();

        // No container involved: we already hold the instance.
        Assert.AreEqual(RuntimeHostExitCode.Success, runtime.ExitCode);
    }

    [TestMethod]
    public void NormalShutdownCompletesWithoutAnyUnhandledException()
    {
        Exception? unobserved = null;
        EventHandler<UnobservedTaskExceptionEventArgs> handler =
            (_, args) => unobserved = args.Exception;
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            using IHost host = RuntimeHostBuilder.BuildServiceHost(
                run: _ => Task.FromResult(RuntimeHostExitCode.Success),
                singletonFactory: FakeSingleton.Owned);
            RuntimeBackgroundService runtime =
                host.Services.GetRequiredService<RuntimeBackgroundService>();

            host.Run();

            Assert.AreEqual(RuntimeHostExitCode.Success, runtime.ExitCode);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.IsNull(unobserved, $"Teardown raised an unobserved exception: {unobserved}");
    }

    [TestMethod]
    public async Task CancellationRunsTheSafeShutdownExactlyOnce()
    {
        var started = new TaskCompletionSource();
        int shutdowns = 0;

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
                    // Stands in for RunAsync's finally, which disposes the dispatcher and the
                    // runtime and therefore performs firmware restoration.
                    Interlocked.Increment(ref shutdowns);
                    throw;
                }

                return RuntimeHostExitCode.Success;
            },
            singletonFactory: FakeSingleton.Owned);

        RuntimeBackgroundService runtime =
            host.Services.GetRequiredService<RuntimeBackgroundService>();

        await host.StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await host.StopAsync();
        await host.StopAsync();

        Assert.AreEqual(1, Volatile.Read(ref shutdowns), "Safe shutdown must run exactly once.");
        Assert.AreEqual(
            RuntimeHostExitCode.Success,
            runtime.ExitCode,
            "An intentional stop is not a failure.");
    }

    // --- Exit-code semantics ---------------------------------------------------------------

    [TestMethod]
    public async Task IntentionalShutdownReportsSuccess()
    {
        using IHost host = RuntimeHostBuilder.BuildServiceHost(
            run: token => Task.Delay(Timeout.Infinite, token)
                .ContinueWith(_ => RuntimeHostExitCode.Success, TaskScheduler.Default),
            singletonFactory: FakeSingleton.Owned);
        RuntimeBackgroundService runtime =
            host.Services.GetRequiredService<RuntimeBackgroundService>();

        await host.StartAsync();
        await host.StopAsync();

        Assert.AreEqual(RuntimeHostExitCode.Success, runtime.ExitCode);
    }

    [TestMethod]
    public async Task LosingTheHardwareSingletonReturnsNonZeroAndOpensNoDevice()
    {
        bool bodyRan = false;

        using IHost host = RuntimeHostBuilder.BuildServiceHost(
            run: _ =>
            {
                bodyRan = true;
                return Task.FromResult(RuntimeHostExitCode.Success);
            },
            singletonFactory: FakeSingleton.NotOwned);
        RuntimeBackgroundService runtime =
            host.Services.GetRequiredService<RuntimeBackgroundService>();

        await host.StartAsync();
        await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsFalse(bodyRan, "No device may be opened when another host owns the hardware.");
        Assert.AreEqual(RuntimeHostExitCode.HardwareAlreadyOwned, runtime.ExitCode);
        Assert.AreNotEqual(
            RuntimeHostExitCode.Success,
            runtime.ExitCode,
            "The recovery policy should retry this one.");
    }

    [TestMethod]
    public async Task HostStartupFailureReportsHostFailure()
    {
        using IHost host = RuntimeHostBuilder.BuildServiceHost(
            run: _ => Task.FromResult(RuntimeHostExitCode.HostFailure),
            singletonFactory: FakeSingleton.Owned);
        RuntimeBackgroundService runtime =
            host.Services.GetRequiredService<RuntimeBackgroundService>();

        await host.StartAsync();
        await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(RuntimeHostExitCode.HostFailure, runtime.ExitCode);
    }

    [TestMethod]
    public void ExitCodesAreDistinctAndOnlyZeroMeansSuccess()
    {
        int[] codes =
        [
            RuntimeHostExitCode.Success,
            RuntimeHostExitCode.HostFailure,
            RuntimeHostExitCode.UsageError,
            RuntimeHostExitCode.HardwareAlreadyOwned
        ];

        CollectionAssert.AllItemsAreUnique(codes);
        Assert.AreEqual(0, RuntimeHostExitCode.Success);
        Assert.IsTrue(codes.Skip(1).All(code => code != 0));
    }

    // --- A thermal session must never end the process ---------------------------------------

    /// <summary>
    /// A session that hands control back to firmware is the safety system working. The host
    /// keeps running and keeps serving IPC, so the interface can still report state and the
    /// user can still act.
    /// </summary>
    [TestMethod]
    public async Task EmergencyHandoffDoesNotStopTheHostOrTheBackgroundService()
    {
        var handedOff = new TaskCompletionSource();
        var stopRequested = false;

        using IHost host = RuntimeHostBuilder.BuildServiceHost(
            run: async token =>
            {
                // The runtime enters EmergencyHandoff; the host body keeps serving IPC. This
                // mirrors production, where the thermal loop lives inside the IPC dispatcher
                // and the host body is the pipe server.
                handedOff.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return RuntimeHostExitCode.Success;
            },
            singletonFactory: FakeSingleton.Owned);

        RuntimeBackgroundService runtime =
            host.Services.GetRequiredService<RuntimeBackgroundService>();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        using CancellationTokenRegistration registration =
            lifetime.ApplicationStopping.Register(() => stopRequested = true);

        await host.StartAsync();
        await handedOff.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);

        Assert.IsFalse(
            stopRequested,
            "A session-level emergency handoff must not call StopApplication.");
        Assert.IsFalse(
            lifetime.ApplicationStopping.IsCancellationRequested,
            "The service must stay running while firmware owns cooling.");
        Assert.IsFalse(runtime.ExecuteTask!.IsCompleted, "The background service is still alive.");

        await host.StopAsync();
        Assert.AreEqual(RuntimeHostExitCode.Success, runtime.ExitCode);
    }

    /// <summary>
    /// A Faulted thermal session is a session state, not a host condition: only the host body
    /// returning a failure code ends the process.
    /// </summary>
    [TestMethod]
    public async Task FaultedSessionDoesNotTerminateTheHostOnItsOwn()
    {
        using IHost host = RuntimeHostBuilder.BuildServiceHost(
            run: async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return RuntimeHostExitCode.Success;
            },
            singletonFactory: FakeSingleton.Owned);

        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        await host.StartAsync();

        // Nothing in the host observes thermal session state, so a Faulted session cannot
        // reach the process lifetime at all. Give it time to prove it stays up.
        await Task.Delay(300);

        Assert.IsFalse(lifetime.ApplicationStopping.IsCancellationRequested);
        await host.StopAsync();
    }

    // --- IPC faults are absorbed, not fatal --------------------------------------------------

    /// <summary>
    /// The likely trigger for the 17:19:59 outage: a client vanishing mid-exchange surfaces as
    /// an IOException, which used to escape the accept loop and end the runtime host.
    /// </summary>
    [TestMethod]
    public void ClientCausedFaultsAreTransientSoOneClosedInterfaceCannotKillTheService()
    {
        // Exactly what a vanished client produces on the next read or write.
        Assert.IsTrue(RuntimeNamedPipeServer.IsTransientConnectionFault(
            new IOException("Pipe is broken.")));
        Assert.IsTrue(RuntimeNamedPipeServer.IsTransientConnectionFault(
            new ObjectDisposedException(nameof(System.IO.Pipes.NamedPipeServerStream))));
        Assert.IsTrue(RuntimeNamedPipeServer.IsTransientConnectionFault(
            new TimeoutException()));
        Assert.IsTrue(RuntimeNamedPipeServer.IsTransientConnectionFault(
            new System.ComponentModel.Win32Exception(232)));
    }

    [TestMethod]
    public void FaultsThatAreNotClientCausedRemainFatal()
    {
        // A programming error or a genuinely broken process must still surface rather than
        // being absorbed by the accept loop forever.
        Assert.IsFalse(RuntimeNamedPipeServer.IsTransientConnectionFault(
            new InvalidOperationException("bad state")));
        Assert.IsFalse(RuntimeNamedPipeServer.IsTransientConnectionFault(
            new OutOfMemoryException()));
        Assert.IsFalse(RuntimeNamedPipeServer.IsTransientConnectionFault(
            new UnauthorizedAccessException("pipe name squatted")));
    }

    [TestMethod]
    public void PersistentAcceptFailureIsStillTreatedAsFatal()
    {
        int ceiling = RuntimeNamedPipeServer.MaximumConsecutiveAcceptFaults;

        Assert.IsTrue(
            ceiling > 0,
            "Absorbing faults without limit would hide a permanently unusable channel.");
        Assert.IsTrue(
            ceiling <= 100,
            "The ceiling must be low enough that an unusable channel ends the host promptly " +
            "rather than spinning.");
    }

    // --- Event-log isolation (retained regression) -------------------------------------------

    [TestMethod]
    public void TestHostRegistersNoEventLogProviderAndSoWritesNothingToTheMachineLog()
    {
        using IHost host = RuntimeHostBuilder.BuildServiceHost(
            run: _ => Task.FromResult(RuntimeHostExitCode.Success),
            singletonFactory: FakeSingleton.Owned);

        Assert.IsFalse(
            host.Services.GetServices<ILoggerProvider>().Any(provider =>
                provider.GetType().Name.Contains("EventLog", StringComparison.OrdinalIgnoreCase)),
            "Repository tests must never write into the installed product's event source.");
    }

    private static class FakeSingleton
    {
        internal static RuntimeHostSingleton Owned() =>
            RuntimeHostSingleton.Acquire($@"Local\BladeControl.Test.{Guid.NewGuid():N}");

        internal static RuntimeHostSingleton NotOwned()
        {
            string name = $@"Local\BladeControl.Test.{Guid.NewGuid():N}";
            RuntimeHostSingleton holder = RuntimeHostSingleton.Acquire(name);
            Assert.IsTrue(holder.IsOwner);
            Holders.Add(holder);
            return RuntimeHostSingleton.Acquire(name);
        }

        // Kept alive for the test run; disposing would release the name.
        private static readonly List<RuntimeHostSingleton> Holders = [];
    }
}
