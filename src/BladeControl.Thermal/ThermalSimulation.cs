using System.Globalization;
using BladeControl.Razer;
using BladeControl.Telemetry;

namespace BladeControl.Thermal;

public sealed record TelemetryTraceSample(
    DateTimeOffset Timestamp,
    double CpuTemperatureCelsius,
    double GpuTemperatureCelsius);

public sealed record ThermalSimulationStep(
    TelemetryTraceSample Sample,
    ThermalDecision Decision);

public static class ThermalSimulator
{
    public static IReadOnlyList<ThermalSimulationStep> Simulate(
        ThermalProfile profile,
        IEnumerable<TelemetryTraceSample> samples,
        ThermalPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(samples);
        TelemetryTraceSample[] input = samples.ToArray();
        if (input.Length == 0)
        {
            return [];
        }

        var engine = new ThermalDecisionEngine(profile, policy);
        engine.InitializeBaseline(input[0].Timestamp - TimeSpan.FromSeconds(1));
        var output = new List<ThermalSimulationStep>(input.Length);
        foreach (TelemetryTraceSample sample in input)
        {
            var snapshot = new TelemetrySnapshot(
                sample.Timestamp,
                TelemetryMetric<double>.Available(
                    sample.CpuTemperatureCelsius,
                    sample.Timestamp,
                    TelemetrySources.CpuPackageTemperature),
                TelemetryMetric<double>.Available(
                    sample.GpuTemperatureCelsius,
                    sample.Timestamp,
                    TelemetrySources.GpuTemperature));
            ThermalDecision decision = engine.Evaluate(snapshot, sample.Timestamp);
            output.Add(new ThermalSimulationStep(sample, decision));
            if (decision.EmergencyAuto)
            {
                break;
            }

            if (decision.ShouldWrite)
            {
                engine.RecordSuccessfulWrite(decision);
            }
        }

        return output;
    }

    public static IReadOnlyList<TelemetryTraceSample> ParseCsv(string csv)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csv);
        string[] lines = csv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (lines.Length < 2 ||
            !lines[0].Equals("timestamp,cpu_temp,gpu_temp", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException(
                "Telemetry trace must begin with: timestamp,cpu_temp,gpu_temp");
        }

        var samples = new List<TelemetryTraceSample>(lines.Length - 1);
        DateTimeOffset? previous = null;
        for (int lineNumber = 2; lineNumber <= lines.Length; lineNumber++)
        {
            string[] columns = lines[lineNumber - 1].Split(',', StringSplitOptions.TrimEntries);
            if (columns.Length != 3 ||
                !DateTimeOffset.TryParse(
                    columns[0],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out DateTimeOffset timestamp) ||
                !double.TryParse(columns[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double cpu) ||
                !double.TryParse(columns[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double gpu))
            {
                throw new FormatException($"Invalid telemetry trace row {lineNumber}.");
            }

            if (previous is not null && timestamp <= previous.Value)
            {
                throw new FormatException(
                    $"Telemetry timestamps must be strictly increasing (row {lineNumber}).");
            }

            samples.Add(new TelemetryTraceSample(timestamp, cpu, gpu));
            previous = timestamp;
        }

        return samples;
    }
}
