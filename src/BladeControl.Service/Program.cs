using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BladeControl.Service;

internal static class Program
{
    private static int Main(string[] args)
    {
        switch (RuntimeHostBuilder.SelectMode(args))
        {
            case RuntimeHostMode.WindowsService:
                return RunAsWindowsService();

            case RuntimeHostMode.Console:
                return RunInConsole(verbose: false);

            case RuntimeHostMode.VerboseConsole:
                return RunInConsole(verbose: true);

            default:
                return PrintUsage();
        }
    }

    private static int RunAsWindowsService()
    {
        using IHost host = RuntimeHostBuilder.BuildServiceHost();

        // host.Run() disposes the host — and with it the service provider — inside its own
        // finally block, so it is already gone by the time Run returns. Resolving the exit
        // code afterwards threw ObjectDisposedException on every single service stop, which
        // is what terminated the installed process.
        //
        // Resolve while the provider is alive and keep the instance. Reading an int off an
        // object we already hold needs no container, so nothing here depends on provider
        // lifetime. The outer using is redundant with Run's own disposal but harmless
        // (disposal is idempotent) and still covers a throw between Build and Run.
        RuntimeBackgroundService runtime =
            host.Services.GetRequiredService<RuntimeBackgroundService>();

        host.Run();
        return runtime.ExitCode;
    }

    private static int RunInConsole(bool verbose)
    {
        using var cancellation = new CancellationTokenSource();
        using RuntimeHostSingleton singleton = RuntimeHostSingleton.Acquire();
        if (!singleton.IsOwner)
        {
            Console.Error.WriteLine(
                "Another BladeControl Runtime host already owns the hardware. Stop the " +
                $"'{RuntimeServiceIdentity.DisplayName}' service before running a console host.");
            return RuntimeHostExitCode.HardwareAlreadyOwned;
        }

        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return RuntimeWindowsHost.RunAsync(cancellation.Token, verbose)
                .GetAwaiter().GetResult();
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static int PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: BladeControl.Service console [--verbose] | --service");
        Console.Error.WriteLine(
            "Run with no arguments only under the Windows Service Control Manager.");
        Console.Error.WriteLine(
            "This executable does not install, remove, start, or stop a Windows service; " +
            "the BladeControl installer does that.");
        return RuntimeHostExitCode.UsageError;
    }
}
