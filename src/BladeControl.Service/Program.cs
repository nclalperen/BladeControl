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
        host.Run();
        return host.Services.GetRequiredService<RuntimeBackgroundService>().ExitCode;
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
            return 3;
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
        return 2;
    }
}
