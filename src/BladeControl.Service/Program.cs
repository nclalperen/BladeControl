namespace BladeControl.Service;

internal static class Program
{
    private static int Main(string[] args)
    {
        bool verboseConsole = args.Length == 2 &&
            args[0].Equals("console", StringComparison.OrdinalIgnoreCase) &&
            args[1].Equals("--verbose", StringComparison.OrdinalIgnoreCase);
        if ((args.Length == 1 && args[0].Equals(
                "console",
                StringComparison.OrdinalIgnoreCase)) || verboseConsole)
        {
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += handler;
            try
            {
                return RuntimeWindowsHost.RunAsync(cancellation.Token, verboseConsole)
                    .GetAwaiter().GetResult();
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }
        }

        if (args.Length == 1 && args[0].Equals("--service", StringComparison.OrdinalIgnoreCase))
        {
            return WindowsServiceDispatcher.Run(
                cancellationToken => RuntimeWindowsHost.RunAsync(cancellationToken));
        }

        Console.Error.WriteLine(
            "Usage: BladeControl.Service console [--verbose] | --service");
        Console.Error.WriteLine("This executable does not install, remove, start, or stop a Windows service.");
        return 2;
    }
}
