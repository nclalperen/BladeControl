using BladeControl.Hardware.Windows;
using BladeControl.Razer;

namespace BladeControl.Cli;

internal static class Program
{
    private const string Unavailable = "<unavailable>";

    private static int Main(string[] args)
    {
        if (args.Length == 1 && IsHelp(args[0]))
        {
            PrintUsage();
            return 0;
        }

        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        string command = args[0];
        if (command.Equals("perf", StringComparison.OrdinalIgnoreCase))
        {
            return RunPerformanceCommand(args.Skip(1).ToArray());
        }

        if (!command.Equals("probe", StringComparison.OrdinalIgnoreCase) &&
            !command.Equals("status", StringComparison.OrdinalIgnoreCase) &&
            !command.Equals("writeback-mode", StringComparison.OrdinalIgnoreCase) &&
            !command.Equals("writeback-levels", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unknown command: {command}");
            PrintUsage();
            return 2;
        }

        if (!TryParseOptions(command, args.Skip(1), out bool verbose, out bool helpRequested))
        {
            return 2;
        }

        if (helpRequested)
        {
            PrintUsage();
            return 0;
        }

        if (command.Equals("probe", StringComparison.OrdinalIgnoreCase))
        {
            return RunProbe(verbose);
        }

        if (command.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            return RunStatus(verbose);
        }

        return command.Equals("writeback-mode", StringComparison.OrdinalIgnoreCase)
            ? RunWriteBackMode(verbose)
            : RunWriteBackLevels(verbose);
    }

    private static bool TryParseOptions(
        string command,
        IEnumerable<string> options,
        out bool verbose,
        out bool helpRequested)
    {
        verbose = false;
        helpRequested = false;

        foreach (string option in options)
        {
            if (option.Equals("--verbose", StringComparison.OrdinalIgnoreCase) ||
                option.Equals("-v", StringComparison.OrdinalIgnoreCase))
            {
                verbose = true;
                continue;
            }

            if (IsHelp(option))
            {
                helpRequested = true;
                continue;
            }

            Console.Error.WriteLine($"Unknown {command} option: {option}");
            PrintUsage();
            return false;
        }

        return true;
    }

    private static bool IsHelp(string argument) =>
        argument.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("help", StringComparison.OrdinalIgnoreCase);

    private static void PrintUsage()
    {
        Console.WriteLine("BladeControl - Razer Blade hardware utility");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  BladeControl.Cli probe [--verbose]");
        Console.WriteLine("  BladeControl.Cli status [--verbose]");
        Console.WriteLine("  BladeControl.Cli writeback-mode [--verbose]");
        Console.WriteLine("  BladeControl.Cli writeback-levels [--verbose]");
        Console.WriteLine("  BladeControl.Cli perf status [--verbose]");
        Console.WriteLine("  BladeControl.Cli perf apply balanced [--verbose]");
        Console.WriteLine("  BladeControl.Cli perf apply silent [--verbose]");
        Console.WriteLine("  BladeControl.Cli perf apply custom --cpu low|medium --gpu low [--verbose]");
        Console.WriteLine("  BladeControl.Cli perf selftest --verbose");
    }

    private static int RunProbe(bool verbose)
    {
        HardwareProbeResult result = WindowsHardwareProbe.Probe();
        PrintProbeResult(result, verbose);
        return 0;
    }

    private static int RunStatus(bool verbose)
    {
        try
        {
            using WindowsRazerClientSession session = WindowsRazerClientSession.Open();
            RazerStatusSnapshot status = session.Client.GetStatus();
            PrintFirmwareStatus(status, verbose);
            return 0;
        }
        catch (WindowsRazerDeviceSelectionException exception)
        {
            PrintSelectionFailure(exception, verbose);
            return 1;
        }
        catch (WindowsRazerTransportException exception)
        {
            Console.Error.WriteLine($"Razer HID transport error: {exception.Message}");
            if (verbose)
            {
                PrintTransportFailureReports(exception);
            }

            return 1;
        }
        catch (RazerProtocolException exception)
        {
            Console.Error.WriteLine($"Razer response validation error: {exception.Message}");
            Console.Error.WriteLine("No further commands were sent after the validation failure.");
            if (verbose)
            {
                PrintExchanges(exception.Exchanges, Console.Error, "FAILED");
            }

            return 1;
        }
        finally
        {
            Console.WriteLine();
            Console.WriteLine("No settings were modified.");
        }
    }

    private static int RunWriteBackMode(bool verbose)
    {
        try
        {
            using WindowsRazerClientSession session = WindowsRazerClientSession.Open();
            RazerModeWriteBackResult result =
                session.Client.RunCustomAutoModeWriteBackTest();
            PrintWriteBackResult(result, verbose);
            return result.Passed ? 0 : 1;
        }
        catch (RazerModeWriteBackPreconditionException exception)
        {
            Console.WriteLine("Razer Blade 16");
            Console.WriteLine();
            Console.WriteLine("Write-back test");
            PrintWriteBackState("Pre-write state", exception.PreWriteState);
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine("ABORT - no 0x0D02 packet was sent.");
            if (verbose)
            {
                PrintSelectedInterface(exception.PreWriteState.Device);
                PrintExchanges(exception.PreWriteState.Exchanges, Console.Error, "PASS");
            }

            return 1;
        }
        catch (RazerModeWriteBackValidationException exception)
        {
            Console.WriteLine("Razer Blade 16");
            Console.WriteLine();
            Console.WriteLine("Write-back test");
            PrintWriteBackState("Pre-write state", exception.PreWriteState);
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(exception.InnerException?.Message);
            if (verbose)
            {
                PrintSelectedInterface(exception.PreWriteState.Device);
                PrintExchanges(exception.PreWriteState.Exchanges, Console.Error, "PASS");
                PrintWriteValidationExchanges(exception.WriteExchanges, Console.Error);
            }

            return 1;
        }
        catch (WindowsRazerDeviceSelectionException exception)
        {
            PrintSelectionFailure(exception, verbose);
            return 1;
        }
        catch (WindowsRazerTransportException exception)
        {
            Console.Error.WriteLine($"Razer HID transport error: {exception.Message}");
            Console.Error.WriteLine("No retry or rollback was attempted.");
            if (verbose)
            {
                PrintTransportFailureReports(exception);
            }

            return 1;
        }
        catch (RazerProtocolException exception)
        {
            Console.Error.WriteLine($"Razer response validation error: {exception.Message}");
            Console.Error.WriteLine("No further command, retry, or rollback was attempted.");
            if (verbose)
            {
                PrintExchanges(exception.Exchanges, Console.Error, "FAILED");
            }

            return 1;
        }
    }

    private static int RunWriteBackLevels(bool verbose)
    {
        try
        {
            using WindowsRazerClientSession session = WindowsRazerClientSession.Open();
            RazerPerformanceLevelWriteBackResult result =
                session.Client.RunPerformanceLevelWriteBackTest();
            PrintPerformanceLevelWriteBackResult(result, verbose);
            return result.Passed ? 0 : 1;
        }
        catch (RazerPerformanceLevelWriteBackPreconditionException exception)
        {
            Console.WriteLine("Razer Blade 16");
            Console.WriteLine();
            Console.WriteLine("Performance-level write-back test");
            PrintWriteBackState("Pre-write state", exception.PreWriteState);
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine("ABORT - no 0x0D07 packet was sent.");
            if (verbose)
            {
                PrintSelectedInterface(exception.PreWriteState.Device);
                PrintExchanges(exception.PreWriteState.Exchanges, Console.Error, "PASS");
            }

            return 1;
        }
        catch (RazerPerformanceLevelWriteBackValidationException exception)
        {
            Console.WriteLine("Razer Blade 16");
            Console.WriteLine();
            Console.WriteLine("Performance-level write-back test");
            PrintWriteBackState("Pre-write state", exception.PreWriteState);
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(exception.InnerException?.Message);
            if (verbose)
            {
                PrintSelectedInterface(exception.PreWriteState.Device);
                PrintExchanges(exception.PreWriteState.Exchanges, Console.Error, "PASS");
                PrintPerformanceLevelWriteValidationExchanges(
                    exception.WriteExchanges,
                    Console.Error);
            }

            return 1;
        }
        catch (WindowsRazerDeviceSelectionException exception)
        {
            PrintSelectionFailure(exception, verbose);
            return 1;
        }
        catch (WindowsRazerTransportException exception)
        {
            Console.Error.WriteLine($"Razer HID transport error: {exception.Message}");
            Console.Error.WriteLine("No retry or rollback was attempted.");
            if (verbose)
            {
                PrintTransportFailureReports(exception);
            }

            return 1;
        }
        catch (RazerProtocolException exception)
        {
            Console.Error.WriteLine($"Razer response validation error: {exception.Message}");
            Console.Error.WriteLine("No further command, retry, or rollback was attempted.");
            if (verbose)
            {
                PrintExchanges(exception.Exchanges, Console.Error, "FAILED");
            }

            return 1;
        }
    }

    private static int RunPerformanceCommand(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Missing perf subcommand.");
            PrintUsage();
            return 2;
        }

        string subcommand = args[0];
        if (IsHelp(subcommand))
        {
            PrintUsage();
            return 0;
        }

        if (subcommand.Equals("status", StringComparison.OrdinalIgnoreCase) ||
            subcommand.Equals("selftest", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseOptions(
                    $"perf {subcommand}",
                    args.Skip(1),
                    out bool verbose,
                    out bool helpRequested))
            {
                return 2;
            }

            if (helpRequested)
            {
                PrintUsage();
                return 0;
            }

            return subcommand.Equals("status", StringComparison.OrdinalIgnoreCase)
                ? RunPerformanceStatus(verbose)
                : RunPerformanceSelfTest(verbose);
        }

        if (!subcommand.Equals("apply", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unknown perf subcommand: {subcommand}");
            PrintUsage();
            return 2;
        }

        if (!TryParsePerformanceProfile(
                args.Skip(1).ToArray(),
                out PerformanceProfile? profile,
                out bool applyVerbose))
        {
            return 2;
        }

        return RunPerformanceApply(profile!, applyVerbose);
    }

    private static bool TryParsePerformanceProfile(
        string[] args,
        out PerformanceProfile? profile,
        out bool verbose)
    {
        profile = null;
        verbose = false;
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Missing performance profile.");
            PrintUsage();
            return false;
        }

        string profileName = args[0];
        if (profileName.Equals("balanced", StringComparison.OrdinalIgnoreCase) ||
            profileName.Equals("silent", StringComparison.OrdinalIgnoreCase))
        {
            profile = profileName.Equals("balanced", StringComparison.OrdinalIgnoreCase)
                ? PerformanceProfile.Balanced
                : PerformanceProfile.Silent;
            foreach (string option in args.Skip(1))
            {
                if (IsVerbose(option))
                {
                    verbose = true;
                }
                else
                {
                    Console.Error.WriteLine(
                        $"Unknown perf apply {profileName} option: {option}");
                    return false;
                }
            }

            return true;
        }

        if (!profileName.Equals("custom", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unknown performance profile: {profileName}");
            return false;
        }

        string? cpu = null;
        string? gpu = null;
        for (int index = 1; index < args.Length; index++)
        {
            string option = args[index];
            if (IsVerbose(option))
            {
                verbose = true;
                continue;
            }

            if ((option.Equals("--cpu", StringComparison.OrdinalIgnoreCase) ||
                 option.Equals("--gpu", StringComparison.OrdinalIgnoreCase)) &&
                index + 1 < args.Length)
            {
                string value = args[++index];
                if (option.Equals("--cpu", StringComparison.OrdinalIgnoreCase))
                {
                    cpu = value;
                }
                else
                {
                    gpu = value;
                }

                continue;
            }

            Console.Error.WriteLine($"Unknown or incomplete perf apply custom option: {option}");
            return false;
        }

        if (cpu is null || gpu is null)
        {
            Console.Error.WriteLine(
                "Custom requires both --cpu low|medium and --gpu low.");
            return false;
        }

        if (!TryParseCpuLevel(cpu, out RazerCpuPerformanceLevel cpuLevel) ||
            !TryParseGpuLevel(gpu, out RazerGpuPerformanceLevel gpuLevel))
        {
            return false;
        }

        profile = PerformanceProfile.Custom(cpuLevel, gpuLevel);
        return true;
    }

    private static bool TryParseCpuLevel(
        string value,
        out RazerCpuPerformanceLevel level)
    {
        if (value.Equals("low", StringComparison.OrdinalIgnoreCase))
        {
            level = RazerCpuPerformanceLevel.Low;
            return true;
        }

        if (value.Equals("medium", StringComparison.OrdinalIgnoreCase))
        {
            level = RazerCpuPerformanceLevel.Medium;
            return true;
        }

        level = default;
        if (value.Equals("high", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("boost", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("overclock", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"CPU level '{value}' is known by the protocol but is not yet " +
                "hardware-validated on this device.");
        }
        else
        {
            Console.Error.WriteLine($"Unknown CPU performance level: {value}");
        }

        return false;
    }

    private static bool TryParseGpuLevel(
        string value,
        out RazerGpuPerformanceLevel level)
    {
        if (value.Equals("low", StringComparison.OrdinalIgnoreCase))
        {
            level = RazerGpuPerformanceLevel.Low;
            return true;
        }

        level = default;
        if (value.Equals("medium", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("high", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"GPU level '{value}' is known by the protocol but is not yet " +
                "hardware-validated on this device.");
        }
        else
        {
            Console.Error.WriteLine($"Unknown GPU performance level: {value}");
        }

        return false;
    }

    private static bool IsVerbose(string option) =>
        option.Equals("--verbose", StringComparison.OrdinalIgnoreCase) ||
        option.Equals("-v", StringComparison.OrdinalIgnoreCase);

    private static int RunPerformanceStatus(bool verbose)
    {
        try
        {
            using WindowsRazerClientSession session = WindowsRazerClientSession.Open();
            PerformanceState state = session.Client.GetPerformanceState();
            PrintPerformanceState("Firmware state", state);
            if (verbose)
            {
                PrintSelectedInterface(state.Device);
                PrintLabeledExchanges(state.Exchanges, Console.Out, "GET");
            }

            Console.WriteLine();
            Console.WriteLine("No settings were modified.");
            return 0;
        }
        catch (Exception exception) when (PrintPerformanceException(exception, verbose))
        {
            return 1;
        }
    }

    private static int RunPerformanceApply(
        PerformanceProfile profile,
        bool verbose)
    {
        try
        {
            using WindowsRazerClientSession session = WindowsRazerClientSession.Open();
            PerformanceApplyResult result =
                session.Client.ApplyPerformanceProfile(profile);
            PrintPerformanceApplyResult(result, verbose);
            return result.Succeeded ? 0 : 1;
        }
        catch (Exception exception) when (PrintPerformanceException(exception, verbose))
        {
            return 1;
        }
    }

    private static int RunPerformanceSelfTest(bool verbose)
    {
        try
        {
            using WindowsRazerClientSession session = WindowsRazerClientSession.Open();
            PerformanceSelfTestResult result =
                session.Client.RunPerformanceSelfTest();
            PrintPerformanceSelfTestResult(result, verbose);
            return result.Succeeded ? 0 : 1;
        }
        catch (PerformanceSelfTestPreconditionException exception)
        {
            Console.Error.WriteLine(exception.Message);
            PrintPerformanceState("Initial state", exception.InitialState);
            if (verbose)
            {
                PrintLabeledExchanges(
                    exception.InitialState.Exchanges,
                    Console.Error,
                    "GET");
            }

            return 1;
        }
        catch (Exception exception) when (PrintPerformanceException(exception, verbose))
        {
            return 1;
        }
    }

    private static bool PrintPerformanceException(Exception exception, bool verbose)
    {
        switch (exception)
        {
            case WindowsRazerDeviceSelectionException selection:
                PrintSelectionFailure(selection, verbose);
                break;
            case WindowsRazerTransportException transport:
                Console.Error.WriteLine($"Razer HID transport error: {transport.Message}");
                if (verbose)
                {
                    PrintTransportFailureReports(transport);
                }

                break;
            case RazerProtocolException protocol:
                Console.Error.WriteLine($"Razer response validation error: {protocol.Message}");
                if (verbose)
                {
                    PrintExchanges(protocol.Exchanges, Console.Error, "FAILED");
                }

                break;
            case PerformanceCapabilityException capability:
                Console.Error.WriteLine(capability.Message);
                break;
            case PerformanceStateException state:
                Console.Error.WriteLine(state.Message);
                break;
            default:
                return false;
        }

        Console.Error.WriteLine("No automatic retry was attempted.");
        return true;
    }

    private static void PrintPerformanceApplyResult(
        PerformanceApplyResult result,
        bool verbose)
    {
        Console.WriteLine("Performance apply");
        PrintPerformanceState("Initial state", result.InitialState);
        Console.WriteLine();
        Console.WriteLine($"Requested state\n  {result.RequestedProfile}");
        Console.WriteLine();
        Console.WriteLine("Calculated write plan");
        if (result.Plan.IsNoOp)
        {
            Console.WriteLine("  No SET operations required.");
        }
        else
        {
            foreach (PerformanceApplyOperation operation in result.Plan.Operations)
            {
                Console.WriteLine($"  {operation.Description}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Operations");
        foreach (PerformanceOperationResult operation in result.Operations)
        {
            Console.WriteLine(
                $"  {operation.Operation.Description}: " +
                (operation.Succeeded ? "PASS" : $"FAILED - {operation.FailureReason}"));
        }

        if (result.Operations.Count == 0)
        {
            Console.WriteLine("  None");
        }

        if (result.FinalState is not null)
        {
            PrintPerformanceState("Final state", result.FinalState);
        }

        Console.WriteLine();
        Console.WriteLine($"Verification: {result.Verification.Message}");
        Console.WriteLine($"Outcome: {result.Outcome}");
        if (result.Restoration is not null)
        {
            Console.WriteLine(result.Restoration.Message);
            Console.WriteLine("Restoration operations");
            if (result.Restoration.Operations.Count == 0)
            {
                Console.WriteLine("  No SET operations required.");
            }
            else
            {
                foreach (PerformanceOperationResult operation in
                    result.Restoration.Operations)
                {
                    Console.WriteLine(
                        $"  {operation.Operation.Description}: " +
                        (operation.Succeeded
                            ? "PASS"
                            : $"FAILED - {operation.FailureReason}"));
                }
            }

            if (result.Restoration.FinalState is not null)
            {
                PrintPerformanceState(
                    "Last verified state",
                    result.Restoration.FinalState);
            }
        }

        if (verbose)
        {
            PrintSelectedInterface(result.InitialState.Device);
            PrintLabeledExchanges(result.InitialState.Exchanges, Console.Out, "Initial GET");
            foreach (PerformanceOperationResult operation in result.Operations)
            {
                if (operation.Exchange is not null)
                {
                    PrintExchange(
                        operation.Exchange,
                        1,
                        Console.Out,
                        operation.Succeeded ? "PASS" : "FAILED",
                        operation.Operation.Description);
                }
            }

            if (result.FinalState is not null)
            {
                PrintLabeledExchanges(result.FinalState.Exchanges, Console.Out, "Final GET");
            }

            if (result.Restoration is not null)
            {
                foreach (PerformanceOperationResult operation in result.Restoration.Operations)
                {
                    if (operation.Exchange is not null)
                    {
                        PrintExchange(
                            operation.Exchange,
                            1,
                            Console.Out,
                            operation.Succeeded ? "PASS" : "FAILED",
                            $"RESTORE {operation.Operation.Description}");
                    }
                }

                if (result.Restoration.FinalState is not null)
                {
                    PrintLabeledExchanges(
                        result.Restoration.FinalState.Exchanges,
                        Console.Out,
                        "Restore final GET");
                }
            }
        }
    }

    private static void PrintPerformanceSelfTestResult(
        PerformanceSelfTestResult result,
        bool verbose)
    {
        Console.WriteLine("Performance Control V1 selftest");
        PrintPerformanceState("Stage A - captured initial state", result.InitialState);
        foreach (PerformanceSelfTestStageResult stage in result.Stages)
        {
            Console.WriteLine();
            Console.WriteLine($"Stage {stage.Stage}");
            PrintPerformanceApplyResult(stage.ApplyResult, verbose);
            if (!stage.ApplyResult.Succeeded)
            {
                break;
            }
        }

        Console.WriteLine();
        Console.WriteLine(result.Message);
    }

    private static void PrintPerformanceState(string heading, PerformanceState state)
    {
        Console.WriteLine();
        Console.WriteLine(heading);
        Console.WriteLine($"  Fan 1       {state.Fan1.RevolutionsPerMinute} RPM");
        Console.WriteLine($"  Fan 2       {state.Fan2.RevolutionsPerMinute} RPM");
        Console.WriteLine($"  Performance {FormatZoneValues(
            state.Zone1Mode.PerformanceMode,
            state.Zone2Mode.PerformanceMode)}");
        Console.WriteLine($"  CPU level   {state.CpuPerformanceLevel}");
        Console.WriteLine($"  GPU level   {state.GpuPerformanceLevel}");
        Console.WriteLine($"  Fan mode    {FormatZoneValues(
            state.Zone1Mode.FanMode,
            state.Zone2Mode.FanMode)}");
    }

    private static void PrintLabeledExchanges(
        IReadOnlyList<RazerExchangeTrace> exchanges,
        TextWriter writer,
        string prefix)
    {
        for (int index = 0; index < exchanges.Count; index++)
        {
            PrintExchange(
                exchanges[index],
                index + 1,
                writer,
                "PASS",
                $"{prefix} 0x{exchanges[index].CombinedCommand:X4}");
        }
    }

    private static void PrintFirmwareStatus(RazerStatusSnapshot status, bool verbose)
    {
        Console.WriteLine("Razer Blade 16");
        Console.WriteLine($"VID:PID       {status.Device.VendorId:X4}:{status.Device.ProductId:X4}");
        Console.WriteLine();
        Console.WriteLine("Firmware state");
        Console.WriteLine($"  Fan 1       {status.Fan1.RevolutionsPerMinute} RPM");
        Console.WriteLine($"  Fan 2       {status.Fan2.RevolutionsPerMinute} RPM");
        Console.WriteLine($"  Performance {status.PerformanceMode}");
        Console.WriteLine($"  CPU level   {status.CpuPerformanceLevel}");
        Console.WriteLine($"  GPU level   {status.GpuPerformanceLevel}");
        Console.WriteLine($"  Fan mode    {status.FanMode}");

        if (!verbose)
        {
            return;
        }

        PrintSelectedInterface(status.Device);
        PrintExchanges(status.Exchanges, Console.Out, "PASS");
    }

    private static void PrintWriteBackResult(
        RazerModeWriteBackResult result,
        bool verbose)
    {
        Console.WriteLine("Razer Blade 16");
        Console.WriteLine();
        Console.WriteLine("Write-back test");
        PrintWriteBackState("Pre-write state", result.PreWriteState);
        Console.WriteLine();
        Console.WriteLine("Writing existing mode back to firmware...");
        Console.WriteLine("  Zone 1       OK");
        Console.WriteLine("  Zone 2       OK");
        PrintWriteBackState("Post-write state", result.PostWriteState);
        Console.WriteLine();
        Console.WriteLine("Result");

        if (result.Passed)
        {
            Console.WriteLine(
                "  PASS - firmware accepted 0x0D02 and state remained unchanged.");
        }
        else
        {
            Console.WriteLine("  *** STATE DRIFT DETECTED ***");
            PrintStateDrift(result);
            Console.WriteLine("  No automatic rollback was attempted.");
        }

        if (verbose)
        {
            PrintSelectedInterface(result.PreWriteState.Device);
            PrintExchanges(result.Exchanges, Console.Out, "PASS");
        }
    }

    private static void PrintPerformanceLevelWriteBackResult(
        RazerPerformanceLevelWriteBackResult result,
        bool verbose)
    {
        Console.WriteLine("Razer Blade 16");
        Console.WriteLine();
        Console.WriteLine("Performance-level write-back test");
        PrintWriteBackState("Pre-write state", result.PreWriteState);
        Console.WriteLine();
        Console.WriteLine("Writing existing performance levels back to firmware...");
        Console.WriteLine("  CPU          OK");
        Console.WriteLine("  GPU          OK");
        PrintWriteBackState("Post-write state", result.PostWriteState);
        Console.WriteLine();
        Console.WriteLine("Result");

        if (result.Passed)
        {
            Console.WriteLine(
                "  PASS - firmware accepted 0x0D07 and state remained unchanged.");
        }
        else
        {
            Console.WriteLine("  *** STATE DRIFT DETECTED ***");
            PrintPerformanceLevelStateDrift(result);
            Console.WriteLine("  No automatic rollback was attempted.");
        }

        if (verbose)
        {
            PrintSelectedInterface(result.PreWriteState.Device);
            PrintPerformanceLevelWriteBackExchanges(result, Console.Out);
        }
    }

    private static void PrintWriteBackState(
        string heading,
        RazerStatusSnapshot state)
    {
        Console.WriteLine();
        Console.WriteLine(heading);
        Console.WriteLine($"  Performance  {FormatZoneValues(
            state.Zone1Mode.PerformanceMode,
            state.Zone2Mode.PerformanceMode)}");
        Console.WriteLine($"  CPU level    {state.CpuPerformanceLevel}");
        Console.WriteLine($"  GPU level    {state.GpuPerformanceLevel}");
        Console.WriteLine($"  Fan mode     {FormatZoneValues(
            state.Zone1Mode.FanMode,
            state.Zone2Mode.FanMode)}");
    }

    private static void PrintStateDrift(RazerModeWriteBackResult result)
    {
        if (!result.PerformanceUnchanged)
        {
            Console.WriteLine(
                $"  Performance changed: " +
                $"{FormatZoneValues(
                    result.PreWriteState.Zone1Mode.PerformanceMode,
                    result.PreWriteState.Zone2Mode.PerformanceMode)} -> " +
                $"{FormatZoneValues(
                    result.PostWriteState.Zone1Mode.PerformanceMode,
                    result.PostWriteState.Zone2Mode.PerformanceMode)}");
        }

        if (!result.FanModeUnchanged)
        {
            Console.WriteLine(
                $"  Fan mode changed: " +
                $"{FormatZoneValues(
                    result.PreWriteState.Zone1Mode.FanMode,
                    result.PreWriteState.Zone2Mode.FanMode)} -> " +
                $"{FormatZoneValues(
                    result.PostWriteState.Zone1Mode.FanMode,
                    result.PostWriteState.Zone2Mode.FanMode)}");
        }

        if (!result.CpuPerformanceLevelUnchanged)
        {
            Console.WriteLine(
                $"  CPU level changed: {result.PreWriteState.CpuPerformanceLevel} -> " +
                result.PostWriteState.CpuPerformanceLevel);
        }

        if (!result.GpuPerformanceLevelUnchanged)
        {
            Console.WriteLine(
                $"  GPU level changed: {result.PreWriteState.GpuPerformanceLevel} -> " +
                result.PostWriteState.GpuPerformanceLevel);
        }
    }

    private static void PrintPerformanceLevelStateDrift(
        RazerPerformanceLevelWriteBackResult result)
    {
        if (!result.PerformanceUnchanged)
        {
            Console.WriteLine(
                $"  Performance changed: " +
                $"{FormatZoneValues(
                    result.PreWriteState.Zone1Mode.PerformanceMode,
                    result.PreWriteState.Zone2Mode.PerformanceMode)} -> " +
                $"{FormatZoneValues(
                    result.PostWriteState.Zone1Mode.PerformanceMode,
                    result.PostWriteState.Zone2Mode.PerformanceMode)}");
        }

        if (!result.FanModeUnchanged)
        {
            Console.WriteLine(
                $"  Fan mode changed: " +
                $"{FormatZoneValues(
                    result.PreWriteState.Zone1Mode.FanMode,
                    result.PreWriteState.Zone2Mode.FanMode)} -> " +
                $"{FormatZoneValues(
                    result.PostWriteState.Zone1Mode.FanMode,
                    result.PostWriteState.Zone2Mode.FanMode)}");
        }

        if (!result.CpuPerformanceLevelUnchanged)
        {
            Console.WriteLine(
                $"  CPU level changed: {result.PreWriteState.CpuPerformanceLevel} -> " +
                result.PostWriteState.CpuPerformanceLevel);
        }

        if (!result.GpuPerformanceLevelUnchanged)
        {
            Console.WriteLine(
                $"  GPU level changed: {result.PreWriteState.GpuPerformanceLevel} -> " +
                result.PostWriteState.GpuPerformanceLevel);
        }
    }

    private static string FormatZoneValues<T>(T zone1, T zone2)
        where T : struct
    {
        return EqualityComparer<T>.Default.Equals(zone1, zone2)
            ? zone1.ToString() ?? Unavailable
            : $"Zone 1 {zone1}; Zone 2 {zone2}";
    }

    private static void PrintSelectedInterface(RazerDeviceInfo device)
    {
        Console.WriteLine();
        Console.WriteLine("Selected HID interface");
        Console.WriteLine($"  Device path          {device.DevicePath}");
        Console.WriteLine($"  UsagePage / Usage    0x{device.UsagePage:X4} / 0x{device.Usage:X4}");
        Console.WriteLine($"  Feature report bytes {device.FeatureReportByteLength}");
    }

    private static void PrintExchanges(
        IReadOnlyList<RazerExchangeTrace> exchanges,
        TextWriter writer,
        string validationResult)
    {
        for (int index = 0; index < exchanges.Count; index++)
        {
            PrintExchange(exchanges[index], index + 1, writer, validationResult);
        }
    }

    private static void PrintWriteValidationExchanges(
        IReadOnlyList<RazerExchangeTrace> exchanges,
        TextWriter writer)
    {
        for (int index = 0; index < exchanges.Count; index++)
        {
            string validationResult = index == exchanges.Count - 1
                ? "FAILED"
                : "PASS";
            PrintExchange(exchanges[index], index + 1, writer, validationResult);
        }
    }

    private static void PrintPerformanceLevelWriteBackExchanges(
        RazerPerformanceLevelWriteBackResult result,
        TextWriter writer)
    {
        for (int index = 0; index < result.PreWriteState.Exchanges.Count; index++)
        {
            PrintExchange(
                result.PreWriteState.Exchanges[index],
                index + 1,
                writer,
                "PASS");
        }

        PrintExchange(result.CpuWriteExchange, 7, writer, "PASS", "CPU SET");
        PrintExchange(result.GpuWriteExchange, 8, writer, "PASS", "GPU SET");

        for (int index = 0; index < result.PostWriteState.Exchanges.Count; index++)
        {
            PrintExchange(
                result.PostWriteState.Exchanges[index],
                index + 9,
                writer,
                "PASS");
        }
    }

    private static void PrintPerformanceLevelWriteValidationExchanges(
        IReadOnlyList<RazerExchangeTrace> exchanges,
        TextWriter writer)
    {
        for (int index = 0; index < exchanges.Count; index++)
        {
            string validationResult = index == exchanges.Count - 1
                ? "FAILED"
                : "PASS";
            string operation = index == 0 ? "CPU SET" : "GPU SET";
            PrintExchange(
                exchanges[index],
                index + 7,
                writer,
                validationResult,
                operation);
        }
    }

    private static void PrintExchange(
        RazerExchangeTrace exchange,
        int index,
        TextWriter writer,
        string validationResult,
        string? operation = null)
    {
        writer.WriteLine();
        writer.WriteLine($"Protocol exchange #{index}");
        if (!string.IsNullOrEmpty(operation))
        {
            writer.WriteLine($"  Operation                 {operation}");
        }
        writer.WriteLine($"  Transaction ID            0x{exchange.TransactionId:X2}");
        writer.WriteLine($"  Command ID                0x{exchange.CombinedCommand:X4}");
        int dataSize = exchange.RequestPacket.Span[5];
        writer.WriteLine(
            $"  Arguments                 " +
            FormatHex(exchange.RequestPacket.Span.Slice(8, dataSize)));
        writer.WriteLine($"  HID report ID             0x{exchange.RequestReport.Span[0]:X2}");
        writer.WriteLine($"  Request CRC               0x{exchange.RequestPacket.Span[88]:X2}");
        writer.WriteLine($"  Response CRC              0x{exchange.ResponsePacket.Span[88]:X2}");
        writer.WriteLine($"  Validation result         {validationResult}");
        writer.WriteLine(
            $"  Request packet (90 bytes)  {FormatHex(exchange.RequestPacket.Span)}");
        writer.WriteLine(
            $"  Response packet (90 bytes) {FormatHex(exchange.ResponsePacket.Span)}");
    }

    private static void PrintTransportFailureReports(
        WindowsRazerTransportException exception)
    {
        if (exception.RequestReport.Length > 0)
        {
            Console.Error.WriteLine(
                $"  Request HID report ({exception.RequestReport.Length} bytes): " +
                FormatHex(exception.RequestReport.Span));
        }

        if (exception.ResponseReport.Length > 0)
        {
            Console.Error.WriteLine(
                $"  Response HID report ({exception.ResponseReport.Length} bytes): " +
                FormatHex(exception.ResponseReport.Span));
        }
    }

    private static void PrintSelectionFailure(
        WindowsRazerDeviceSelectionException exception,
        bool verbose)
    {
        Console.Error.WriteLine($"Razer HID selection error: {exception.Message}");

        if (exception.Candidates.Count == 0)
        {
            Console.Error.WriteLine("  No VID 0x1532 / PID 0x029F candidates were available.");
        }
        else
        {
            Console.Error.WriteLine("Candidates:");
            for (int index = 0; index < exception.Candidates.Count; index++)
            {
                HidDeviceInfo candidate = exception.Candidates[index];
                Console.Error.WriteLine($"  Candidate #{index + 1}");
                Console.Error.WriteLine(
                    $"    VID:PID                  {FormatHex(candidate.VendorId)}:{FormatHex(candidate.ProductId)}");
                Console.Error.WriteLine(
                    $"    UsagePage / Usage        {FormatHex(candidate.UsagePage)} / {FormatHex(candidate.Usage)}");
                Console.Error.WriteLine(
                    $"    FeatureReportByteLength  {Display(candidate.FeatureReportByteLength)}");
                if (verbose)
                {
                    Console.Error.WriteLine(
                        $"    DevicePath               {Display(candidate.DevicePath)}");
                    Console.Error.WriteLine(
                        $"    DeviceInstanceId         {Display(candidate.DeviceInstanceId)}");
                }
            }
        }

        if (exception.EnumerationWarnings.Count > 0)
        {
            Console.Error.WriteLine("Enumeration warnings:");
            foreach (string warning in exception.EnumerationWarnings)
            {
                Console.Error.WriteLine($"  - {warning}");
            }
        }
    }

    private static void PrintProbeResult(HardwareProbeResult result, bool verbose)
    {
        Console.WriteLine("BladeControl passive hardware probe");
        Console.WriteLine("No HID reports are read or sent; all device handles use zero desired access.");
        Console.WriteLine();

        PrintSystemBios(result.SystemBios);

        int exactTargetCount = result.HidDevices.Count(device => device.IsReferenceDevice);
        int targetProductCount = result.HidDevices.Count(device => device.ProductId == 0x029F);
        int possibleManagementCount = result.HidDevices.Count(
            device => device.IsPossibleManagementProtocolInterface);

        Console.WriteLine();
        Console.WriteLine($"HID interfaces: {result.HidDevices.Count}");
        Console.WriteLine($"PID 0x029F collections: {targetProductCount}");
        Console.WriteLine($"Exact VID 0x1532 / PID 0x029F collections: {exactTargetCount}");
        Console.WriteLine($"Possible management interfaces (feature length 91): {possibleManagementCount}");

        for (int index = 0; index < result.HidDevices.Count; index++)
        {
            PrintHidDevice(result.HidDevices[index], index + 1, verbose);
        }

        if (result.HidDevices.Count == 0)
        {
            Console.WriteLine("  No present HID interfaces were enumerated.");
        }

        if (result.Warnings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Probe warnings:");
            foreach (string warning in result.Warnings)
            {
                Console.WriteLine($"  - {warning}");
            }
        }
    }

    private static void PrintSystemBios(SystemBiosInfo bios)
    {
        Console.WriteLine("System BIOS registry information:");
        Console.WriteLine($"  SystemManufacturer: {Display(bios.SystemManufacturer)}");
        Console.WriteLine($"  SystemProductName:  {Display(bios.SystemProductName)}");
        Console.WriteLine($"  SystemSKU:          {Display(bios.SystemSku)}");
        Console.WriteLine($"  BIOSVersion:        {Display(bios.BiosVersion)}");
    }

    private static void PrintHidDevice(HidDeviceInfo device, int index, bool verbose)
    {
        Console.WriteLine();
        if (device.IsReferenceDevice)
        {
            Console.WriteLine(">>> TARGET RAZER BLADE 16 - VID 0x1532 / PID 0x029F <<<");
        }
        else if (device.ProductId == 0x029F)
        {
            Console.WriteLine(">>> PID 0x029F HID COLLECTION <<<");
        }

        Console.WriteLine($"HID collection #{index}");
        Console.WriteLine($"  VID:                     {FormatHex(device.VendorId)}");
        Console.WriteLine($"  PID:                     {FormatHex(device.ProductId)}");
        Console.WriteLine($"  VersionNumber:           {FormatHex(device.VersionNumber)}");
        Console.WriteLine($"  ManufacturerString:      {Display(device.ManufacturerString)}");
        Console.WriteLine($"  ProductString:           {Display(device.ProductString)}");
        Console.WriteLine($"  SerialString:            {Display(device.SerialString)}");
        Console.WriteLine($"  UsagePage:               {FormatHex(device.UsagePage)}");
        Console.WriteLine($"  Usage:                   {FormatHex(device.Usage)}");
        Console.WriteLine($"  InputReportByteLength:   {Display(device.InputReportByteLength)}");
        Console.WriteLine($"  OutputReportByteLength:  {Display(device.OutputReportByteLength)}");
        Console.WriteLine($"  FeatureReportByteLength: {Display(device.FeatureReportByteLength)}");

        if (verbose)
        {
            Console.WriteLine($"  DevicePath:              {Display(device.DevicePath)}");
            Console.WriteLine($"  DeviceInstanceId:        {Display(device.DeviceInstanceId)}");
        }

        if (device.IsPossibleManagementProtocolInterface)
        {
            Console.WriteLine(
                "  *** POSSIBLE RAZER MANAGEMENT PROTOCOL INTERFACE " +
                "(FeatureReportByteLength = 91) ***");
        }

        if (device.Warnings.Count > 0)
        {
            Console.WriteLine("  Metadata warnings:");
            foreach (string warning in device.Warnings)
            {
                Console.WriteLine($"    - {warning}");
            }
        }
    }

    private static string FormatHex(ReadOnlySpan<byte> bytes)
    {
        return string.Join(' ', bytes.ToArray().Select(value => value.ToString("X2")));
    }

    private static string Display(string? value) =>
        string.IsNullOrEmpty(value) ? Unavailable : value;

    private static string Display(ushort? value) =>
        value?.ToString() ?? Unavailable;

    private static string FormatHex(ushort? value) =>
        value is ushort number ? $"0x{number:X4}" : Unavailable;
}
