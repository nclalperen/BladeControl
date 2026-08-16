namespace BladeControl.Service;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0].Equals("console", StringComparison.OrdinalIgnoreCase))
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
                return RuntimeWindowsHost.RunAsync(cancellation.Token).GetAwaiter().GetResult();
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }
        }

        if (args.Length == 1 && args[0].Equals("--service", StringComparison.OrdinalIgnoreCase))
        {
            return WindowsServiceDispatcher.Run(RuntimeWindowsHost.RunAsync);
        }

        Console.Error.WriteLine("Usage: BladeControl.Service console | --service");
        Console.Error.WriteLine("This executable does not install, remove, start, or stop a Windows service.");
        return 2;
    }
}
