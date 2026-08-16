using BladeControl.Hardware.Windows;
using BladeControl.Hardware.Windows.Telemetry;
using BladeControl.Razer;
using BladeControl.Runtime;
using BladeControl.Telemetry;
using BladeControl.Thermal;

namespace BladeControl.Cli;

internal static partial class Program
{
    private static int RunTelemetryCommand(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Missing telemetry subcommand.");
            PrintUsage();
            return 2;
        }

        string subcommand = args[0];
        if (subcommand.Equals("doctor", StringComparison.OrdinalIgnoreCase) ||
            subcommand.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseOptions(
                    $"telemetry {subcommand}",
                    args.Skip(1),
                    out bool verbose,
                    out bool help))
            {
                return 2;
            }

            if (help)
            {
                PrintUsage();
                return 0;
            }

            return subcommand.Equals("doctor", StringComparison.OrdinalIgnoreCase)
                ? RunTelemetryDoctor(verbose)
                : RunTelemetrySnapshot(verbose);
        }

        if (subcommand.Equals("monitor", StringComparison.OrdinalIgnoreCase))
        {
            return ParseAndRunTelemetryMonitor(args.Skip(1).ToArray());
        }

        Console.Error.WriteLine($"Unknown telemetry subcommand: {subcommand}");
        PrintUsage();
        return 2;
    }

    private static int RunThermalCommand(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Missing thermal subcommand.");
            PrintUsage();
            return 2;
        }

        string subcommand = args[0];
        return subcommand.ToLowerInvariant() switch
        {
            "status" => RunThermalStatusCommand(args.Skip(1).ToArray()),
            "curve" => RunThermalCurveCommand(args.Skip(1).ToArray()),
            "simulate" => RunThermalSimulationCommand(args.Skip(1).ToArray()),
            "run" => RunThermalRuntimeCommand(args.Skip(1).ToArray()),
            "selftest" => RunThermalSelfTestCommand(args.Skip(1).ToArray()),
            _ => UnknownThermalSubcommand(subcommand)
        };
    }

    private static int RunTelemetryDoctor(bool verbose)
    {
        WindowsRazerClientSession? razer = TryOpenRazerSession(out string razerDiagnostic);
        try
        {
            using WindowsTelemetrySession telemetry = WindowsTelemetrySession.Open(razer?.Client);
            TelemetrySnapshot snapshot = telemetry.GetSnapshot();
            TelemetryCapabilities capabilities = telemetry.Capabilities;
            Console.WriteLine("BladeControl telemetry doctor");
            Console.WriteLine();
            Console.WriteLine($"Razer HID                    {Availability(capabilities.RazerHidAvailable)}");
            Console.WriteLine($"NVML                         {Availability(capabilities.NvmlAvailable)}");
            Console.WriteLine($"Selected NVIDIA GPU          {FormatGpu(capabilities.SelectedGpu)}");
            Console.WriteLine($"GPU temperature              {Support(capabilities.GpuTemperatureSupported)}");
            Console.WriteLine($"GPU power                    {Support(capabilities.GpuPowerSupported)}");
            Console.WriteLine($"LibreHardwareMonitor version {capabilities.LibreHardwareMonitorVersion}");
            Console.WriteLine($"PawnIO                       {Availability(capabilities.PawnIoAvailable)}");
            Console.WriteLine($"PawnIO version               {telemetry.PawnIoProvenance.Version}");
            Console.WriteLine($"PawnIO service               {telemetry.PawnIoProvenance.ServiceState}");
            Console.WriteLine($"PawnIO driver                {telemetry.PawnIoProvenance.DriverPath}");
            Console.WriteLine($"PawnIO file version          {telemetry.PawnIoProvenance.FileVersion}");
            Console.WriteLine($"PawnIO Authenticode          {telemetry.PawnIoProvenance.AuthenticodeStatus}");
            Console.WriteLine($"PawnIO signer                {telemetry.PawnIoProvenance.SignerSubject}");
            Console.WriteLine($"PawnIO SHA256                {telemetry.PawnIoProvenance.Sha256}");
            Console.WriteLine($"PawnIO thermal safety        {(telemetry.PawnIoProvenance.IsSafeForThermalOwnership ? "safe" : "unsafe")}");
            Console.WriteLine($"CPU Package temp             {Availability(capabilities.CpuPackageTemperatureAvailable)}");
            Console.WriteLine($"CPU Package power            {Availability(capabilities.CpuPackagePowerAvailable)}");
            Console.WriteLine($"ACPI zones                   {(capabilities.AcpiZonesAvailable ? "available" : "unavailable")} / diagnostic only");
            Console.WriteLine();
            Console.WriteLine("Thermal-control qualification");
            TelemetryHealth health = TelemetryHealthEvaluator.Evaluate(snapshot, DateTimeOffset.UtcNow);
            Console.WriteLine($"  {health.Kind}: {health.Reason}");
            if (capabilities.GpuSelectionAmbiguous)
            {
                Console.WriteLine("  Automatic control refused: NVIDIA GPU selection is ambiguous.");
            }

            if (verbose)
            {
                Console.WriteLine();
                Console.WriteLine($"Razer: {razerDiagnostic}");
                foreach (TelemetryGpuIdentity gpu in capabilities.EnumeratedGpus)
                {
                    Console.WriteLine($"NVML GPU: {gpu.Name} / {gpu.Uuid} / {gpu.PciBusId}");
                }

                foreach (string diagnostic in capabilities.Diagnostics.Concat(snapshot.Warnings))
                {
                    Console.WriteLine($"Diagnostic: {diagnostic}");
                }

                if (snapshot.RazerFirmwareState is not null)
                {
                    PrintExchanges(snapshot.RazerFirmwareState.Exchanges, Console.Out, "PASS");
                }
            }

            Console.WriteLine();
            Console.WriteLine("No settings were modified.");
            return 0;
        }
        finally
        {
            razer?.Dispose();
        }
    }

    private static int RunTelemetrySnapshot(bool verbose)
    {
        WindowsRazerClientSession? razer = TryOpenRazerSession(out string razerDiagnostic);
        try
        {
            using WindowsTelemetrySession telemetry = WindowsTelemetrySession.Open(razer?.Client);
            TelemetrySnapshot snapshot = telemetry.GetSnapshot();
            PrintTelemetrySnapshot(snapshot, telemetry.Capabilities, verbose);
            if (verbose && razer is null)
            {
                Console.WriteLine($"Razer diagnostic: {razerDiagnostic}");
            }

            return 0;
        }
        finally
        {
            razer?.Dispose();
        }
    }

    private static int ParseAndRunTelemetryMonitor(string[] args)
    {
        int interval = 1000;
        bool verbose = false;
        for (int index = 0; index < args.Length; index++)
        {
            string option = args[index];
            if (option.Equals("--verbose", StringComparison.OrdinalIgnoreCase) ||
                option.Equals("-v", StringComparison.OrdinalIgnoreCase))
            {
                verbose = true;
                continue;
            }

            if (option.Equals("--interval", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length &&
                int.TryParse(args[++index], out int parsed) &&
                parsed >= 100)
            {
                interval = parsed;
                continue;
            }

            Console.Error.WriteLine($"Unknown or invalid telemetry monitor option: {option}");
            return 2;
        }

        WindowsRazerClientSession? razer = TryOpenRazerSession(out _);
        try
        {
            using WindowsTelemetrySession telemetry = WindowsTelemetrySession.Open(razer?.Client);
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += handler;
            try
            {
                Console.WriteLine($"Telemetry monitor ({interval} ms); Ctrl+C to stop.");
                while (!cancellation.IsCancellationRequested)
                {
                    PrintTelemetrySnapshot(telemetry.GetSnapshot(), telemetry.Capabilities, verbose);
                    if (cancellation.Token.WaitHandle.WaitOne(interval))
                    {
                        break;
                    }
                }
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }

            Console.WriteLine("No settings were modified.");
            return 0;
        }
        finally
        {
            razer?.Dispose();
        }
    }

    private static int RunThermalStatusCommand(string[] args)
    {
        if (!TryParseOptions("thermal status", args, out bool verbose, out bool help))
        {
            return 2;
        }

        if (help)
        {
            PrintUsage();
            return 0;
        }

        WindowsRazerClientSession? razer = TryOpenRazerSession(out _);
        try
        {
            using WindowsTelemetrySession telemetry = WindowsTelemetrySession.Open(razer?.Client);
            TelemetrySnapshot snapshot = telemetry.GetSnapshot();
            PrintTelemetrySnapshot(snapshot, telemetry.Capabilities, verbose);
            TelemetryHealth health = TelemetryHealthEvaluator.Evaluate(snapshot, DateTimeOffset.UtcNow);
            Console.WriteLine();
            Console.WriteLine("Thermal controller readiness");
            Console.WriteLine($"  Health       {health.Kind}");
            Console.WriteLine($"  Reason       {health.Reason}");
            if (health.IsHealthy)
            {
                FanRpm cpu = BuiltInThermalProfiles.Default.CpuCurve.Evaluate(
                    snapshot.CpuPackageTemperatureCelsius.Value!.Value);
                FanRpm gpu = BuiltInThermalProfiles.Default.GpuCurve.Evaluate(
                    snapshot.GpuTemperatureCelsius.Value!.Value);
                Console.WriteLine($"  CPU demand   {cpu}");
                Console.WriteLine($"  GPU demand   {gpu}");
                Console.WriteLine($"  Combined     {(cpu.Value >= gpu.Value ? cpu : gpu)}");
            }

            Console.WriteLine();
            Console.WriteLine("No settings were modified.");
            return 0;
        }
        finally
        {
            razer?.Dispose();
        }
    }

    private static int RunThermalCurveCommand(string[] args)
    {
        if (args.Length == 2 &&
            args[0].Equals("show", StringComparison.OrdinalIgnoreCase) &&
            args[1].Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(ThermalProfileSerializer.Serialize(BuiltInThermalProfiles.Default));
            return 0;
        }

        if (args.Length == 2 &&
            args[0].Equals("validate", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                ThermalProfile profile = ThermalProfileSerializer.Parse(
                    File.ReadAllText(Path.GetFullPath(args[1])));
                Console.WriteLine($"Valid thermal profile: {profile.Name}");
                Console.WriteLine($"CPU points: {profile.CpuCurve.Points.Count}");
                Console.WriteLine($"GPU points: {profile.GpuCurve.Points.Count}");
                Console.WriteLine("No hardware was opened.");
                return 0;
            }
            catch (Exception exception) when (exception is IOException or
                UnauthorizedAccessException or FormatException)
            {
                Console.Error.WriteLine($"Thermal curve validation failed: {exception.Message}");
                return 1;
            }
        }

        Console.Error.WriteLine("Expected: thermal curve show default OR thermal curve validate <file>");
        return 2;
    }

    private static int RunThermalSimulationCommand(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Expected: thermal simulate <curve-file> <telemetry-trace-file>");
            return 2;
        }

        try
        {
            ThermalProfile profile = args[0].Equals("default", StringComparison.OrdinalIgnoreCase)
                ? BuiltInThermalProfiles.Default
                : ThermalProfileSerializer.Parse(File.ReadAllText(Path.GetFullPath(args[0])));
            IReadOnlyList<TelemetryTraceSample> samples = ThermalSimulator.ParseCsv(
                File.ReadAllText(Path.GetFullPath(args[1])));
            IReadOnlyList<ThermalSimulationStep> output = ThermalSimulator.Simulate(profile, samples);
            Console.WriteLine("timestamp,cpu_temp,gpu_temp,cpu_target,gpu_target,requested,effective,write,reason");
            foreach (ThermalSimulationStep step in output)
            {
                ThermalDecision decision = step.Decision;
                Console.WriteLine(
                    $"{step.Sample.Timestamp:O},{step.Sample.CpuTemperatureCelsius:F1}," +
                    $"{step.Sample.GpuTemperatureCelsius:F1},{decision.CpuCurveTarget?.Value}," +
                    $"{decision.GpuCurveTarget?.Value},{decision.RequestedTarget?.Value}," +
                    $"{decision.EffectiveTarget.Value},{decision.ShouldWrite},\"{decision.Reason}\"");
            }

            Console.WriteLine("Simulation completed without opening Razer HID, NVML, or LibreHardwareMonitor.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or FormatException or ArgumentException)
        {
            Console.Error.WriteLine($"Thermal simulation failed: {exception.Message}");
            return 1;
        }
    }

    private static int RunThermalRuntimeCommand(string[] args)
    {
        bool verbose = false;
        bool defaultCurve = false;
        for (int index = 0; index < args.Length; index++)
        {
            string option = args[index];
            if (option.Equals("--verbose", StringComparison.OrdinalIgnoreCase) ||
                option.Equals("-v", StringComparison.OrdinalIgnoreCase))
            {
                verbose = true;
                continue;
            }

            if (option.Equals("--curve", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length &&
                args[++index].Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                defaultCurve = true;
                continue;
            }

            Console.Error.WriteLine($"Unknown or invalid thermal run option: {option}");
            return 2;
        }

        if (!defaultCurve)
        {
            Console.Error.WriteLine("Thermal Control V1 requires: thermal run --curve default");
            return 2;
        }

        return RunThermalRuntimeAsync(verbose).GetAwaiter().GetResult();
    }

    private static async Task<int> RunThermalRuntimeAsync(bool verbose)
    {
        try
        {
            using WindowsRazerClientSession razer = WindowsRazerClientSession.Open();
            using WindowsTelemetrySession telemetry = WindowsTelemetrySession.Open(razer.Client);
            await using var runtime = new BladeRuntime(
                telemetry,
                telemetry,
                new RazerRuntimeHardwareController(razer.Client),
                new NamedSemaphoreRuntimeOwnershipGate());
            if (verbose)
            {
                runtime.EventPublished += PrintRuntimeEvent;
            }
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += handler;
            ThermalSessionResult? result;
            try
            {
                runtime.StartThermalControl();
                Console.WriteLine(
                    "Runtime Core V1 thermal control active at a 500 ms monotonic deadline cadence. " +
                    "Ctrl+C performs the safe Auto handoff and performance restoration.");
                await runtime.RunScheduledAsync(cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                Console.CancelKeyPress -= handler;
                result = await runtime.StopThermalControlAsync().ConfigureAwait(false);
            }

            RuntimeStatus status = runtime.GetStatus();
            Console.WriteLine(result?.Message ?? status.LastFailureReason ?? "Thermal runtime stopped.");
            Console.WriteLine(
                $"Scheduler: {status.Scheduler.CompletedCycles} cycles; " +
                $"last start-to-start {status.Scheduler.ActualStartToStart.TotalMilliseconds:F1} ms; " +
                $"overruns {status.Scheduler.OverrunCount}; " +
                $"max {status.Scheduler.MaximumOverrun.TotalMilliseconds:F1} ms.");
            Console.WriteLine(
                "Software cleanup cannot guarantee recovery after abrupt power loss, kernel bugcheck, " +
                "forced process termination, or total OS failure.");
            return result?.Succeeded == true && status.State == RuntimeState.Stopped ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Thermal runtime stopped: {exception.Message}");
            Console.Error.WriteLine("No automatic retry was attempted.");
            return 1;
        }
    }

    private static void PrintRuntimeEvents(IEnumerable<RuntimeEvent> events)
    {
        foreach (RuntimeEvent item in events)
        {
            Console.WriteLine();
            Console.WriteLine($"{item.Kind} #{item.Sequence} {item.Timestamp:O}");
            Console.WriteLine($"  {item.Message}");
            if (item is ProtocolExchangeEvent protocol)
            {
                PrintExchange(
                    protocol.Exchange,
                    checked((int)protocol.Sequence),
                    Console.Out,
                    protocol.Exchange.HasResponse ? "PASS" : "FAILED",
                    item.Message);
            }
        }
    }

    private static void PrintRuntimeEvent(RuntimeEvent item) =>
        PrintRuntimeEvents([item]);

    private static int RunThermalSelfTestCommand(string[] args)
    {
        if (args.Length != 1 ||
            !args[0].Equals("--verbose", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Thermal hardware acceptance requires: thermal selftest --verbose");
            return 2;
        }

        using DirectHardwareOwnership? ownership = TryAcquireDirectHardwareOwnership();
        if (ownership is null)
        {
            return 1;
        }

        try
        {
            using WindowsRazerClientSession razer = WindowsRazerClientSession.Open();
            using WindowsTelemetrySession telemetry = WindowsTelemetrySession.Open(razer.Client);
            var runner = new ThermalSelfTestRunner(
                telemetry,
                new RazerThermalControlDevice(razer.Client));
            ThermalSelfTestResult result = runner.Run();
            foreach (ThermalSelfTestStageResult stage in result.Stages)
            {
                Console.WriteLine($"{stage.Stage}: {(stage.Succeeded ? "PASS" : "FAIL")}");
                Console.WriteLine($"  {stage.Message}");
            }

            PrintThermalTrace(result.Trace);
            Console.WriteLine(result.Message);
            return result.Succeeded ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Thermal selftest stopped: {exception.Message}");
            Console.Error.WriteLine("No stage, SET command, or recovery operation was retried.");
            return 1;
        }
    }

    private static WindowsRazerClientSession? TryOpenRazerSession(out string diagnostic)
    {
        try
        {
            WindowsRazerClientSession session = WindowsRazerClientSession.Open();
            diagnostic = "available";
            return session;
        }
        catch (Exception exception)
        {
            diagnostic = $"unavailable: {exception.Message}";
            return null;
        }
    }

    private static void PrintTelemetrySnapshot(
        TelemetrySnapshot snapshot,
        TelemetryCapabilities capabilities,
        bool verbose)
    {
        Console.WriteLine();
        Console.WriteLine($"Timestamp {snapshot.Timestamp:O}");
        Console.WriteLine();
        Console.WriteLine("CPU");
        PrintMetric("  Package temperature", snapshot.CpuPackageTemperatureCelsius, "C", verbose);
        PrintMetric("  Core max", snapshot.CpuCoreMaxTemperatureCelsius, "C", verbose);
        PrintMetric("  Package power", snapshot.CpuPackagePowerWatts, "W", verbose);
        PrintMetric("  Utilization", snapshot.CpuTotalLoadPercent, "%", verbose);
        PrintMetric("  Clock", snapshot.CpuClockMegahertz, "MHz", verbose);
        Console.WriteLine();
        Console.WriteLine("GPU");
        Console.WriteLine($"  Device              {FormatGpu(capabilities.SelectedGpu)}");
        PrintMetric("  Temperature", snapshot.GpuTemperatureCelsius, "C", verbose);
        PrintMetric("  Power", snapshot.GpuPowerWatts, "W", verbose);
        PrintMetric("  Utilization", snapshot.GpuUtilizationPercent, "%", verbose);
        PrintMetric("  Memory utilization", snapshot.GpuMemoryUtilizationPercent, "%", verbose);
        PrintMetric("  Graphics clock", snapshot.GpuGraphicsClockMegahertz, "MHz", verbose);
        PrintMetric("  Memory clock", snapshot.GpuMemoryClockMegahertz, "MHz", verbose);
        PrintMemoryMetric("  VRAM used", snapshot.GpuVramUsedBytes, verbose);
        PrintMemoryMetric("  VRAM total", snapshot.GpuVramTotalBytes, verbose);

        if (snapshot.RazerFirmwareState is not null)
        {
            RazerStatusSnapshot firmware = snapshot.RazerFirmwareState;
            Console.WriteLine();
            Console.WriteLine("Razer firmware (diagnostic state, not a physical tachometer)");
            Console.WriteLine($"  Reported fan 1      {firmware.Fan1.FirmwareReportedRpm} RPM");
            Console.WriteLine($"  Reported fan 2      {firmware.Fan2.FirmwareReportedRpm} RPM");
            Console.WriteLine($"  Performance        {firmware.PerformanceMode}");
            Console.WriteLine($"  Fan mode           {firmware.FanMode}");
            if (verbose)
            {
                PrintExchanges(firmware.Exchanges, Console.Out, "PASS");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Sources");
        Console.WriteLine("  CPU thermal         LibreHardwareMonitor / PawnIO (authoritative)");
        Console.WriteLine("  GPU                 NVIDIA NVML (authoritative GPU temperature)");
        Console.WriteLine("  Fan state           Razer HID (diagnostic firmware-reported value)");
        Console.WriteLine("  ACPI zones          diagnostic only; never authoritative");
        foreach (string warning in snapshot.Warnings)
        {
            Console.WriteLine($"Warning: {warning}");
        }
    }

    private static void PrintMetric(
        string label,
        TelemetryMetric<double> metric,
        string unit,
        bool verbose)
    {
        string value = metric.IsValid && metric.Value.HasValue
            ? $"{metric.Value.Value:F1} {unit}"
            : metric.IsSupported
                ? "invalid/unavailable"
                : "not supported";
        Console.WriteLine($"{label,-22} {value}");
        if (verbose && !string.IsNullOrWhiteSpace(metric.Diagnostic))
        {
            Console.WriteLine($"    {metric.Source.Provider}: {metric.Diagnostic}");
        }
    }

    private static void PrintMemoryMetric(
        string label,
        TelemetryMetric<ulong> metric,
        bool verbose)
    {
        string value = metric.IsValid && metric.Value.HasValue
            ? $"{metric.Value.Value / (1024d * 1024 * 1024):F2} GiB"
            : metric.IsSupported
                ? "invalid/unavailable"
                : "not supported";
        Console.WriteLine($"{label,-22} {value}");
        if (verbose && !string.IsNullOrWhiteSpace(metric.Diagnostic))
        {
            Console.WriteLine($"    {metric.Source.Provider}: {metric.Diagnostic}");
        }
    }

    private static void PrintThermalTrace(IEnumerable<ThermalTraceEntry> trace)
    {
        foreach (ThermalTraceEntry entry in trace)
        {
            if (entry.Kind == ThermalTraceKind.Protocol && entry.Exchange is not null)
            {
                PrintExchange(
                    entry.Exchange,
                    checked((int)entry.Sequence),
                    Console.Out,
                    "PASS",
                    entry.Message);
                continue;
            }

            Console.WriteLine();
            Console.WriteLine($"{entry.Kind} #{entry.Sequence}");
            Console.WriteLine($"  {entry.Timestamp:O}");
            Console.WriteLine($"  {entry.Message}");
        }
    }

    private static string Availability(bool available) =>
        available ? "available" : "unavailable";

    private static string Support(bool supported) =>
        supported ? "supported" : "not supported";

    private static string FormatGpu(TelemetryGpuIdentity? gpu) => gpu is null
        ? "<none>"
        : $"{gpu.Name} / {gpu.Uuid} / {gpu.PciBusId}";

    private static int UnknownThermalSubcommand(string command)
    {
        Console.Error.WriteLine($"Unknown thermal subcommand: {command}");
        PrintUsage();
        return 2;
    }
}
