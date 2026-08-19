using BladeControl.Hardware.Windows;
using BladeControl.Hardware.Windows.Telemetry;
using BladeControl.Razer;
using BladeControl.Runtime;
using BladeControl.Service;
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

        if (subcommand.Equals("gpu-thermal-probe", StringComparison.OrdinalIgnoreCase))
        {
            return RunGpuThermalProbe();
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
            Console.WriteLine($"GPU thermal limits           {DescribeGpuThermalLimits(capabilities.GpuThermalLimits)}");
            Console.WriteLine($"LibreHardwareMonitor version {capabilities.LibreHardwareMonitorVersion}");
            Console.WriteLine($"PawnIO                       {Availability(capabilities.PawnIoAvailable)}");
            Console.WriteLine($"PawnIO version               {telemetry.PawnIoProvenance.Version}");
            Console.WriteLine($"PawnIO service               {telemetry.PawnIoProvenance.ServiceState}");
            Console.WriteLine($"PawnIO driver                {telemetry.PawnIoProvenance.DriverPath}");
            Console.WriteLine($"PawnIO file version          {telemetry.PawnIoProvenance.FileVersion}");
            Console.WriteLine($"PawnIO Authenticode          {telemetry.PawnIoProvenance.AuthenticodeStatus}");
            Console.WriteLine($"PawnIO signature source      {telemetry.PawnIoProvenance.SignatureSource}");
            Console.WriteLine($"PawnIO Windows signer        {telemetry.PawnIoProvenance.WindowsTrustedSignerSubject}");
            Console.WriteLine($"PawnIO embedded signer       {telemetry.PawnIoProvenance.EmbeddedSignerSubject}");
            Console.WriteLine($"PawnIO timestamp signer      {telemetry.PawnIoProvenance.TimestampSignerSubject}");
            Console.WriteLine($"PawnIO SHA256                {telemetry.PawnIoProvenance.Sha256}");
            Console.WriteLine($"PawnIO CPU provenance safety {(telemetry.PawnIoProvenance.IsSafeForThermalOwnership ? "safe" : "unsafe")}");
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
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return await RunThermalIpcClientAsync(
                verbose,
                new NamedPipeRuntimeIpcClient(),
                cancellation.Token,
                Console.Out,
                Console.Error).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    internal static async Task<int> RunThermalIpcClientAsync(
        bool verbose,
        IRuntimeIpcClient ipc,
        CancellationToken cancellationToken,
        TextWriter output,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(ipc);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            var client = new RuntimeThermalClient(ipc);
            var summary = new RuntimeSessionSummary();
            RuntimeThermalClientResult result = await client.RunAsync(
                "default",
                started: status =>
                {
                    summary.Start(status);
                    output.WriteLine(
                        "Runtime Core V1 thermal control is active through IPC. " +
                        "Ctrl+C requests the runtime's safe Auto handoff and " +
                        "performance restoration.");
                },
                eventReceived: item =>
                {
                    summary.Observe(item);
                    if (verbose)
                    {
                        PrintRuntimeEvent(item, output);
                    }
                },
                batchReceived: batch =>
                {
                    summary.Observe(batch);
                    if (verbose && batch.GapDetected)
                    {
                        output.WriteLine(
                            $"Runtime event retention gap: oldest available sequence is " +
                            $"#{batch.OldestAvailableSequence}; earlier events are no " +
                            "longer retained.");
                    }
                },
                stopping: verbose
                    ? () => output.WriteLine(
                        "Runtime Core stop requested; draining the actual Auto handoff, " +
                        "verification, performance restoration, and SessionStopped events.")
                    : null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.Outcome == RuntimeThermalClientOutcome.RuntimeUnavailable)
            {
                error.WriteLine("Runtime Core is not running.");
                error.WriteLine("Start it with:");
                error.WriteLine("BladeControl.Cli service console");
            }
            else
            {
                TextWriter writer = result.Succeeded ? output : error;
                writer.WriteLine(result.Message);
            }

            if (result.FinalStatus is not null)
            {
                PrintThermalCompletionSummary(result, summary, output);
            }

            return result.Succeeded ? 0 : 1;
        }
        catch (Exception exception)
        {
            error.WriteLine($"Thermal IPC client stopped: {exception.Message}");
            error.WriteLine("No automatic retry was attempted.");
            return 1;
        }
    }

    private static void PrintRuntimeEvent(RuntimeEventDto item, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine($"{item.Kind} #{item.Sequence} {item.Timestamp:O}");
        output.WriteLine($"  {item.Message}");
        if (item.Telemetry is not null)
        {
            output.WriteLine(
                $"  CPU {FormatMetric(item.Telemetry.CpuPackageTemperatureCelsius)}; " +
                $"GPU {FormatMetric(item.Telemetry.GpuTemperatureCelsius)}");
        }

        if (item.ThermalDecision is not null)
        {
            output.WriteLine(
                $"  target {item.ThermalDecision.EffectiveTargetRpm} RPM; " +
                $"write {item.ThermalDecision.ShouldWrite}; " +
                $"health {item.ThermalDecision.Health.Kind}");
        }

        if (item.WatchdogState is not null)
        {
            output.WriteLine(
                $"  Zone 1 {item.WatchdogState.Zone1PerformanceMode} + " +
                $"{item.WatchdogState.Zone1FanMode}; Zone 2 " +
                $"{item.WatchdogState.Zone2PerformanceMode} + " +
                $"{item.WatchdogState.Zone2FanMode}");
        }

        if (item.Exchange is not null)
        {
            output.WriteLine(
                $"  {item.Exchange.Command}; Tx 0x{item.Exchange.TransactionId:X2}; " +
                $"response {item.Exchange.HasResponse}");
            if (item.Exchange.RequestReportHex is not null)
            {
                output.WriteLine($"  request  {item.Exchange.RequestReportHex}");
            }

            if (item.Exchange.ResponseReportHex is not null)
            {
                output.WriteLine($"  response {item.Exchange.ResponseReportHex}");
            }
        }
    }

    private static void PrintThermalCompletionSummary(
        RuntimeThermalClientResult result,
        RuntimeSessionSummary summary,
        TextWriter output)
    {
        RuntimeStatusDto status = result.FinalStatus!;
        bool stopped = status.State.Equals(nameof(RuntimeState.Stopped), StringComparison.Ordinal);
        string shutdownState = result.StopResult is null
            ? "Not reported (this client did not request stop)"
            : result.StopResult.Succeeded
                ? "Completed"
                : "Not confirmed by Runtime Core";

        output.WriteLine();
        output.WriteLine("Thermal session completion summary");
        output.WriteLine($"  Final Runtime state              {status.State}");
        output.WriteLine(
            $"  Session ID                       " +
            $"{status.SessionId?.ToString() ?? summary.SessionId?.ToString() ?? "<unavailable>"}");
        output.WriteLine($"  Completed cycles                 {status.Scheduler.CompletedCycles}");
        output.WriteLine(
            $"  Average actual start-to-start    {summary.FormatAverageStartToStart()}");
        output.WriteLine(
            $"  Last telemetry acquisition       " +
            $"{status.LastTelemetryAcquisitionDuration.TotalMilliseconds:F1} ms");
        output.WriteLine($"  Overrun count                    {status.Scheduler.OverrunCount}");
        output.WriteLine(
            $"  Maximum overrun                  " +
            $"{status.Scheduler.MaximumOverrun.TotalMilliseconds:F1} ms");
        output.WriteLine($"  Skipped deadlines                {status.Scheduler.SkippedDeadlines}");
        output.WriteLine($"  Safe Auto handoff                {shutdownState}");
        output.WriteLine($"  Original performance restoration {shutdownState}");
        if (stopped)
        {
            output.WriteLine(
                "  Historical-data note             Telemetry, watchdog, and scheduler " +
                "fields above describe the finished session; no current hardware read was made.");
        }
    }

    private sealed class RuntimeSessionSummary
    {
        private DateTimeOffset? _previousTelemetryTimestamp;
        private double _totalStartToStartMilliseconds;
        private long _startToStartIntervalCount;
        private bool _retentionGap;

        internal Guid? SessionId { get; private set; }

        internal void Start(RuntimeStatusDto status) => SessionId = status.SessionId;

        internal void Observe(RuntimeEventBatchDto batch)
        {
            _retentionGap |= batch.GapDetected;
        }

        internal void Observe(RuntimeEventDto item)
        {
            if (!item.Kind.Equals(nameof(RuntimeEventKind.TelemetrySample),
                    StringComparison.Ordinal))
            {
                return;
            }

            if (_previousTelemetryTimestamp.HasValue)
            {
                double interval =
                    (item.Timestamp - _previousTelemetryTimestamp.Value).TotalMilliseconds;
                if (interval >= 0)
                {
                    _totalStartToStartMilliseconds += interval;
                    _startToStartIntervalCount++;
                }
            }

            _previousTelemetryTimestamp = item.Timestamp;
        }

        internal string FormatAverageStartToStart()
        {
            if (_startToStartIntervalCount == 0)
            {
                return _retentionGap
                    ? "<unavailable: retained event history was truncated>"
                    : "<unavailable: fewer than two cycle samples>";
            }

            string qualifier = _retentionGap ? " (partial after retention gap)" : string.Empty;
            return $"{_totalStartToStartMilliseconds / _startToStartIntervalCount:F1} ms" +
                qualifier;
        }
    }

    private static string FormatMetric(TelemetryMetricDto<double> metric) =>
        metric.HasValue ? $"{metric.Value:F1} C" : metric.Diagnostic ?? "unavailable";

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
        PrintGpuThermalLimits(capabilities.GpuThermalLimits);

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

    /// <summary>
    /// Prints the GPU thermal limits the safety ladder is built from, separating what the
    /// device reported from what BladeControl decided to do about it.
    /// </summary>
    /// <remarks>
    /// The distinction matters: NVML reports the shutdown temperature, but the decision to
    /// hand off a degree early is ours. Labelling a policy margin as a device specification
    /// would misrepresent the hardware.
    /// </remarks>
    private static void PrintGpuThermalLimits(GpuThermalLimits? limits)
    {
        Console.WriteLine();
        Console.WriteLine("GPU thermal limits");
        if (limits is null)
        {
            Console.WriteLine("  Status              unavailable");
            Console.WriteLine(
                "  Effect              closed-loop thermal control will not qualify; " +
                "no threshold is assumed");
            return;
        }

        Console.WriteLine($"  Max operating       {limits.MaxOperatingCelsius,3:F0} C   device specification");
        Console.WriteLine($"  HW slowdown         {limits.HardwareSlowdownCelsius,3:F0} C   device specification");
        Console.WriteLine($"  HW shutdown         {limits.HardwareShutdownCelsius,3:F0} C   device specification");
        Console.WriteLine($"  Maximum cooling at  {limits.CriticalCoolingCelsius,3:F0} C   BladeControl action");
        Console.WriteLine(
            $"  Release cooling at  {limits.CriticalRecoveryCelsius,3:F0} C   BladeControl policy " +
            $"({GpuThermalLimits.CriticalRecoveryPolicyMarginCelsius:F0} C hysteresis, 3 samples)");
        Console.WriteLine($"  Sustained handoff   {limits.SustainedEmergencyCelsius,3:F0} C   BladeControl action (3 samples)");
        Console.WriteLine(
            $"  Immediate handoff   {limits.ImmediateEmergencyCelsius,3:F0} C   BladeControl policy " +
            $"({GpuThermalLimits.PreShutdownPolicyMarginCelsius:F0} C pre-shutdown margin)");
        Console.WriteLine($"  Source              {limits.DescribeSource()}");
    }

    /// <summary>One-line form for the doctor summary.</summary>
    private static string DescribeGpuThermalLimits(GpuThermalLimits? limits) =>
        limits is null
            ? "unavailable (thermal control will not qualify)"
            : limits.Describe();

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
