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
            !command.Equals("status", StringComparison.OrdinalIgnoreCase))
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

        return command.Equals("probe", StringComparison.OrdinalIgnoreCase)
            ? RunProbe(verbose)
            : RunStatus(verbose);
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
                PrintExchanges(exception.Exchanges, Console.Error);
            }

            return 1;
        }
        finally
        {
            Console.WriteLine();
            Console.WriteLine("No settings were modified.");
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

        Console.WriteLine();
        Console.WriteLine("Selected HID interface");
        Console.WriteLine($"  Device path          {status.Device.DevicePath}");
        Console.WriteLine($"  UsagePage / Usage    0x{status.Device.UsagePage:X4} / 0x{status.Device.Usage:X4}");
        Console.WriteLine($"  Feature report bytes {status.Device.FeatureReportByteLength}");
        PrintExchanges(status.Exchanges, Console.Out);
    }

    private static void PrintExchanges(
        IReadOnlyList<RazerExchangeTrace> exchanges,
        TextWriter writer)
    {
        for (int index = 0; index < exchanges.Count; index++)
        {
            RazerExchangeTrace exchange = exchanges[index];
            writer.WriteLine();
            writer.WriteLine($"Protocol exchange #{index + 1}");
            writer.WriteLine($"  Transaction ID            0x{exchange.TransactionId:X2}");
            writer.WriteLine($"  Command ID                0x{exchange.CombinedCommand:X4}");
            writer.WriteLine($"  HID report ID             0x{exchange.RequestReport.Span[0]:X2}");
            writer.WriteLine(
                $"  Request packet (90 bytes)  {FormatHex(exchange.RequestPacket.Span)}");
            writer.WriteLine(
                $"  Response packet (90 bytes) {FormatHex(exchange.ResponsePacket.Span)}");
        }
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
