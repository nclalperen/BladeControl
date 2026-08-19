using BladeControl.Hardware.Windows.Telemetry;
using BladeControl.Telemetry;

namespace BladeControl.Cli;

internal static partial class Program
{
    /// <summary>
    /// Prints exactly what the NVIDIA driver reports about this GPU's thermal limits, before
    /// any interpretation.
    /// </summary>
    /// <remarks>
    /// Strictly read-only: NVML is opened and nothing else. Correlate the output with
    /// <c>nvidia-smi -q -d TEMPERATURE</c> taken in the same time window — the T.Limit
    /// specifications are static and should match exactly, while the live margin moves with
    /// load.
    /// </remarks>
    private static int RunGpuThermalProbe()
    {
        GpuThermalLimitReport report = GpuThermalLimitDiagnostic.Read();
        if (!report.NvmlAvailable)
        {
            Console.Error.WriteLine($"NVML unavailable: {report.Diagnostic}");
            return 1;
        }

        Console.WriteLine("BladeControl GPU thermal probe (read-only)");
        Console.WriteLine($"Sampled {DateTimeOffset.Now:O}");
        Console.WriteLine();
        Console.WriteLine($"Device                        {report.DeviceName}");
        Console.WriteLine($"PCI bus                       {report.PciBusId}");
        Console.WriteLine($"Selection ambiguous           {report.SelectionAmbiguous}");
        Console.WriteLine();

        Console.WriteLine("Current core temperature");
        Console.WriteLine($"  Entry point                 {report.TemperatureSource}");
        Console.WriteLine($"  Result                      {report.TemperatureResult}");
        Console.WriteLine($"  Value                       {Format(report.CurrentTemperatureCelsius)}");
        Console.WriteLine();

        Console.WriteLine("nvmlDeviceGetFieldValues");
        Console.WriteLine($"  Outer result                {report.FieldCallResult}");
        PrintFieldReport("shutdown T.Limit specification", report.Shutdown);
        PrintFieldReport("slowdown T.Limit specification", report.Slowdown);
        PrintFieldReport("GPU max  T.Limit specification", report.GpuMax);
        Console.WriteLine();

        Console.WriteLine("nvmlDeviceGetMarginTemperature");
        Console.WriteLine($"  Result                      {report.MarginResult}");
        Console.WriteLine(
            "  Raw margin                  " +
            (report.MarginCelsius is { } margin ? $"{margin} C" : "unavailable"));
        Console.WriteLine();

        Console.WriteLine("nvmlDeviceGetTemperatureThreshold (legacy absolute, corroboration)");
        PrintThreshold("GPU_MAX ", report.LegacyGpuMax);
        PrintThreshold("SLOWDOWN", report.LegacySlowdown);
        PrintThreshold("SHUTDOWN", report.LegacyShutdown);
        Console.WriteLine();

        Console.WriteLine("nvmlTemperatureThresholds_t, complete set (diagnostic)");
        foreach (GpuAbsoluteThresholdReport threshold in report.AllLegacyThresholds)
        {
            Console.WriteLine(
                $"  {threshold.Threshold,-16} result {threshold.Result}, " +
                (threshold.Celsius is { } value ? $"{value:F0} C" : "unavailable"));
        }

        Console.WriteLine();

        Console.WriteLine("nvmlDeviceGetThermalSettings (diagnostic, not used for safety)");
        foreach (GpuThermalSettingsReport settings in report.ThermalSettings)
        {
            Console.WriteLine(
                $"  sensorIndex {settings.RequestedSensorIndex,-2}   result {settings.Result}, " +
                $"count {settings.ReturnedSensorCount}");
            foreach (GpuThermalSensorReport sensor in settings.Sensors)
            {
                Console.WriteLine(
                    $"      controller {sensor.Controller}, target {sensor.Target}, " +
                    $"current {sensor.CurrentTemperatureCelsius} C, " +
                    $"defaultMin {sensor.DefaultMinimumCelsius} C, " +
                    $"defaultMax {sensor.DefaultMaximumCelsius} C");
            }
        }

        Console.WriteLine();

        // Derived values come last, and only when every source succeeded: printing a
        // conversion built on a refused field would present a guess as a measurement.
        Console.WriteLine("Derived absolute limits");
        if (report.DerivedLimits is { } limits)
        {
            Console.WriteLine($"  Max operating               {limits.MaxOperatingCelsius:F0} C");
            Console.WriteLine($"  Hardware slowdown           {limits.HardwareSlowdownCelsius:F0} C");
            Console.WriteLine($"  Hardware shutdown           {limits.HardwareShutdownCelsius:F0} C");
            Console.WriteLine($"  Source                      {limits.DescribeSource()}");
        }
        else
        {
            Console.WriteLine($"  Unavailable                 {report.DerivedDiagnostic}");
        }

        Console.WriteLine();
        Console.WriteLine("No settings were modified. NVML reads only.");
        return 0;
    }

    private static void PrintThreshold(string label, GpuAbsoluteThresholdReport? reading)
    {
        if (reading is null)
        {
            Console.WriteLine($"  {label}                    not reported");
            return;
        }

        Console.WriteLine(
            $"  {label}                    result {reading.Result}, " +
            (reading.Celsius is { } celsius ? $"{celsius:F0} C" : "unavailable"));
    }

    private static void PrintFieldReport(string label, GpuThermalFieldReport? reading)
    {
        Console.WriteLine($"  {label}");
        if (reading is null)
        {
            Console.WriteLine("    not reported");
            return;
        }

        Console.WriteLine($"    field id                  {reading.FieldId}");
        Console.WriteLine($"    per-field result          {reading.Result}");
        Console.WriteLine($"    valueType                 {reading.ValueType}");
        Console.WriteLine($"    raw union payload         0x{reading.RawValue:X16}");
        Console.WriteLine(
            "    decoded                   " +
            (reading.Celsius is { } celsius
                ? $"{celsius:F0} C"
                : "not readable as a temperature"));
    }

    private static string Format(double? celsius) =>
        celsius is { } value ? $"{value:F0} C" : "unavailable";
}
