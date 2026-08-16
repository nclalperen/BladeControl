using BladeControl.Hardware.Windows;
using BladeControl.Hardware.Windows.Telemetry;
using BladeControl.Runtime;
using BladeControl.Telemetry;

namespace BladeControl.Service;

public static class RuntimeWindowsHost
{
    public const string ServiceName = "BladeControlRuntime";

    public static async Task<int> RunAsync(
        CancellationToken cancellationToken,
        bool verbose = false)
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
                Console.Error.WriteLine(runtime.GetStatus().LastFailureReason);
                return 1;
            }

            Console.WriteLine("BladeControl Runtime Core V1 host is ready.");
            Console.WriteLine($"Local pipe: {RuntimeNamedPipeServer.PipeName}");
            Console.WriteLine("Ctrl+C/service stop uses the shared safe shutdown state machine.");
            if (verbose)
            {
                Console.WriteLine(
                    "Verbose console diagnostics enabled; hardware behavior is unchanged.");
            }
            var pipe = new RuntimeNamedPipeServer(dispatcher);
            await pipe.RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Runtime host failed: {exception.Message}");
            return 1;
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
