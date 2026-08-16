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
        if (!command.Equals("probe", StringComparison.OrdinalIgnoreCase) &&
            !command.Equals("status", StringComparison.OrdinalIgnoreCase) &&
            !command.Equals("writeback-mode", StringComparison.OrdinalIgnoreCase))
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

        return command.Equals("status", StringComparison.OrdinalIgnoreCase)
            ? RunStatus(verbose)
            : RunWriteBackMode(verbose);
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

    private static void PrintExchange(
        RazerExchangeTrace exchange,
        int index,
        TextWriter writer,
        string validationResult)
    {
        writer.WriteLine();
        writer.WriteLine($"Protocol exchange #{index}");
        writer.WriteLine($"  Transaction ID            0x{exchange.TransactionId:X2}");
        writer.WriteLine($"  Command ID                0x{exchange.CombinedCommand:X4}");
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
