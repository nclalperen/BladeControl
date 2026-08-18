using BladeControl.Hardware.Windows;
using BladeControl.Hardware.Windows.Telemetry;
using BladeControl.Runtime;
using BladeControl.Telemetry;
using Microsoft.Extensions.Logging;

namespace BladeControl.Service;

public static class RuntimeWindowsHost
{
    public const string ServiceName = RuntimeServiceIdentity.ServiceName;

    /// <summary>
    /// Runs the runtime host: opens hardware, serves the local IPC channel, and unwinds
    /// through the validated safe shutdown when cancelled.
    /// </summary>
    /// <param name="logger">
    /// Where failures are reported when there is no console. Under the SCM,
    /// <c>Console.Error</c> goes nowhere, which is why the cause of a host failure was absent
    /// from the event log and had to be inferred. Diagnostics now go to both.
    /// </param>
    public static async Task<int> RunAsync(
        CancellationToken cancellationToken,
        bool verbose = false,
        ILogger? logger = null)
    {
        WindowsRazerClientSession? razer = null;
        WindowsTelemetrySession? telemetry = null;
        BladeRuntime? runtime = null;
        RuntimeIpcDispatcher? dispatcher = null;
        try
        {
            razer = WindowsRazerClientSession.Open();
            telemetry = WindowsTelemetrySession.Open(razer.Client);
            runtime = new BladeRuntime(
                telemetry,
                telemetry,
                new RazerRuntimeHardwareController(razer.Client),
                new NamedSemaphoreRuntimeOwnershipGate());
            dispatcher = new RuntimeIpcDispatcher(
                runtime,
                () => CreateDoctorReport(telemetry, runtime));
            if (!runtime.InitializeHost())
            {
                string reason = runtime.GetStatus().LastFailureReason ??
                    "Runtime host initialisation was refused without a reason.";
                Console.Error.WriteLine(reason);
                logger?.LogError("Runtime host initialisation failed: {Reason}", reason);
                return RuntimeHostExitCode.HostFailure;
            }

            Console.WriteLine("BladeControl Runtime Core V1 host is ready.");
            Console.WriteLine($"Local pipe: {RuntimeNamedPipeServer.PipeName}");
            Console.WriteLine("Ctrl+C/service stop uses the shared safe shutdown state machine.");
            if (verbose)
            {
                Console.WriteLine(
                    "Verbose console diagnostics enabled; hardware behavior is unchanged.");
            }
            // A client vanishing mid-exchange is absorbed by the accept loop and reported
            // here, so one closed interface can no longer cost the runtime its hardware.
            var pipe = new RuntimeNamedPipeServer(
                dispatcher,
                fault => logger?.LogWarning(
                    fault,
                    "Transient IPC connection fault; continuing to serve the channel."));
            await pipe.RunAsync(cancellationToken).ConfigureAwait(false);
            return RuntimeHostExitCode.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RuntimeHostExitCode.Success;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Runtime host failed: {exception.Message}");
            logger?.LogError(exception, "Runtime host failed and will stop.");
            return RuntimeHostExitCode.HostFailure;
        }
        finally
        {
            if (dispatcher is not null)
            {
                await dispatcher.DisposeAsync().ConfigureAwait(false);
            }

            if (runtime is not null)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }

            telemetry?.Dispose();
            razer?.Dispose();
        }
    }

    private static object CreateDoctorReport(
        WindowsTelemetrySession telemetry,
        BladeRuntime runtime)
    {
        ThermalOwnershipQualification qualification = runtime.QualifyThermalOwnership();
        return new
        {
            qualification.Capabilities,
            telemetry.PawnIoProvenance,
            qualification.CpuProviderProvenanceSafe,
            qualification.CpuPackageTemperatureHealthy,
            qualification.GpuTemperatureHealthy,
            qualification.GpuSelectionDeterministic,
            qualification.RazerHidAvailable,
            qualification.ThermalOwnershipReady,
            qualification.Reasons,
            QualificationTimestamp = qualification.Timestamp
        };
    }
}
