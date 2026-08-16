using System.Text.Json;
using BladeControl.Runtime;
using BladeControl.Service;

namespace BladeControl.Cli;

internal static partial class Program
{
    private static DirectHardwareOwnership? TryAcquireDirectHardwareOwnership()
    {
        var gate = new NamedSemaphoreRuntimeOwnershipGate();
        IRuntimeOwnershipLease? lease;
        try
        {
            lease = gate.TryAcquire();
        }
        catch
        {
            gate.Dispose();
            throw;
        }

        if (lease is null)
        {
            gate.Dispose();
            Console.Error.WriteLine(
                "Direct hardware write rejected: BladeControl Runtime already owns the hardware session.");
            return null;
        }

        return new DirectHardwareOwnership(gate, lease);
    }

    private static int RunRuntimeCommand(string[] args)
    {
        bool verbose = args.Length == 2 &&
            args[1].Equals("--verbose", StringComparison.OrdinalIgnoreCase);
        if ((args.Length != 1 && !verbose) ||
            (!args[0].Equals("status", StringComparison.OrdinalIgnoreCase) &&
             !args[0].Equals("doctor", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine(
                "Expected: runtime status [--verbose] OR runtime doctor [--verbose]");
            return 2;
        }

        RuntimeIpcOperation operation = args[0].Equals(
            "status",
            StringComparison.OrdinalIgnoreCase)
            ? RuntimeIpcOperation.GetRuntimeStatus
            : RuntimeIpcOperation.GetRuntimeDoctor;
        try
        {
            RuntimeIpcResponse response = RuntimePipeClient.SendAsync(operation)
                .GetAwaiter().GetResult();
            if (!response.Succeeded)
            {
                Console.Error.WriteLine($"Runtime request failed: {response.Error}");
                return 1;
            }

            Console.WriteLine(args[0].Equals("status", StringComparison.OrdinalIgnoreCase)
                ? "BladeControl runtime status"
                : "BladeControl runtime doctor");
            if (verbose)
            {
                Console.WriteLine(
                    $"IPC protocol {response.Version}; request {response.RequestId}; " +
                    "verbose rendering does not alter runtime behavior.");
            }

            Console.WriteLine(JsonSerializer.Serialize(response.Data, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            return 0;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            Console.Error.WriteLine(
                $"BladeControl runtime service is unavailable: {exception.Message}");
            return 1;
        }
    }

    private static int RunServiceCommand(string[] args)
    {
        bool verbose = args.Length == 2 &&
            args[1].Equals("--verbose", StringComparison.OrdinalIgnoreCase);
        if ((args.Length != 1 && !verbose) ||
            !args[0].Equals("console", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Expected: service console [--verbose]");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
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

    private sealed class DirectHardwareOwnership : IDisposable
    {
        private IRuntimeOwnershipGate? _gate;
        private IRuntimeOwnershipLease? _lease;

        internal DirectHardwareOwnership(
            IRuntimeOwnershipGate gate,
            IRuntimeOwnershipLease lease)
        {
            _gate = gate;
            _lease = lease;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _lease, null)?.Dispose();
            Interlocked.Exchange(ref _gate, null)?.Dispose();
        }
    }
}
