using System.Text.Json;
using BladeControl.Razer;

namespace BladeControl.Thermal;

public sealed record ThermalCurvePoint
{
    public ThermalCurvePoint(double temperatureCelsius, FanRpm targetRpm)
    {
        if (!double.IsFinite(temperatureCelsius) ||
            temperatureCelsius <= 0 || temperatureCelsius >= 120)
        {
            throw new ArgumentOutOfRangeException(
                nameof(temperatureCelsius),
                temperatureCelsius,
                "Thermal curve temperatures must be finite and between 0 C and 120 C.");
        }

        if (targetRpm.Value < ThermalCurve.MinimumDynamicRpm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetRpm),
                targetRpm,
                $"Thermal Control V1 requires at least {ThermalCurve.MinimumDynamicRpm} RPM.");
        }

        TemperatureCelsius = temperatureCelsius;
        TargetRpm = targetRpm;
    }

    public double TemperatureCelsius { get; }

    public FanRpm TargetRpm { get; }
}

public sealed class ThermalCurve
{
    public const int MinimumDynamicRpm = 3000;

    public ThermalCurve(IEnumerable<ThermalCurvePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        ThermalCurvePoint[] copy = points.ToArray();
        if (copy.Length < 2)
        {
            throw new ArgumentException("A thermal curve requires at least two points.", nameof(points));
        }

        for (int index = 1; index < copy.Length; index++)
        {
            if (copy[index].TemperatureCelsius <= copy[index - 1].TemperatureCelsius)
            {
                throw new ArgumentException(
                    "Thermal curve temperatures must be strictly increasing.",
                    nameof(points));
            }

            if (copy[index].TargetRpm.Value < copy[index - 1].TargetRpm.Value)
            {
                throw new ArgumentException(
                    "Thermal curve RPM values must be non-decreasing.",
                    nameof(points));
            }
        }

        Points = copy;
    }

    public IReadOnlyList<ThermalCurvePoint> Points { get; }

    public FanRpm Evaluate(double temperatureCelsius)
    {
        if (!double.IsFinite(temperatureCelsius))
        {
            throw new ArgumentOutOfRangeException(
                nameof(temperatureCelsius),
                "Temperature must be finite.");
        }

        if (temperatureCelsius <= Points[0].TemperatureCelsius)
        {
            return Points[0].TargetRpm;
        }

        if (temperatureCelsius >= Points[^1].TemperatureCelsius)
        {
            return Points[^1].TargetRpm;
        }

        for (int index = 1; index < Points.Count; index++)
        {
            ThermalCurvePoint upper = Points[index];
            if (temperatureCelsius > upper.TemperatureCelsius)
            {
                continue;
            }

            ThermalCurvePoint lower = Points[index - 1];
            double ratio = (temperatureCelsius - lower.TemperatureCelsius) /
                (upper.TemperatureCelsius - lower.TemperatureCelsius);
            double interpolated = lower.TargetRpm.Value +
                ((upper.TargetRpm.Value - lower.TargetRpm.Value) * ratio);
            int quantizedUp = checked((int)(Math.Ceiling(interpolated / FanRpm.Increment) *
                FanRpm.Increment));
            return new FanRpm(quantizedUp);
        }

        throw new InvalidOperationException("Thermal curve interpolation invariant failed.");
    }
}

public sealed record ThermalProfile(
    string Name,
    ThermalCurve CpuCurve,
    ThermalCurve GpuCurve);

public static class BuiltInThermalProfiles
{
    public static ThermalProfile Default { get; } = new(
        "default",
        new ThermalCurve(
        [
            new ThermalCurvePoint(50, new FanRpm(3000)),
            new ThermalCurvePoint(60, new FanRpm(3300)),
            new ThermalCurvePoint(70, new FanRpm(3800)),
            new ThermalCurvePoint(80, new FanRpm(4400)),
            new ThermalCurvePoint(88, new FanRpm(5000))
        ]),
        new ThermalCurve(
        [
            new ThermalCurvePoint(45, new FanRpm(3000)),
            new ThermalCurvePoint(55, new FanRpm(3300)),
            new ThermalCurvePoint(65, new FanRpm(3800)),
            new ThermalCurvePoint(72, new FanRpm(4400)),
            new ThermalCurvePoint(78, new FanRpm(5000))
        ]));
}

public static class ThermalProfileSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static ThermalProfile Parse(string json)
    {
        // The same shape that let one blank line stop the runtime service: emptiness reported
        // as an argument fault while every other malformed input is a FormatException — and
        // the line below already says "the document is empty" for a payload that deserialises
        // to null. The text here is a file a user chose, so it is input, not a caller's
        // mistake. This one is not reachable from IPC, only from the CLI's curve commands, so
        // it cost a raw "ArgumentException:" in front of the user rather than a crash. Fixed
        // anyway: one concept should not have two exception types.
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new FormatException("The thermal profile document is empty.");
        }

        ThermalProfileDocument document = JsonSerializer.Deserialize<ThermalProfileDocument>(json, Options) ??
            throw new FormatException("The thermal profile document is empty.");
        return new ThermalProfile(
            string.IsNullOrWhiteSpace(document.Name) ? "custom" : document.Name,
            CreateCurve(document.Cpu, "cpu"),
            CreateCurve(document.Gpu, "gpu"));
    }

    public static string Serialize(ThermalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return JsonSerializer.Serialize(
            new ThermalProfileDocument
            {
                Name = profile.Name,
                Cpu = profile.CpuCurve.Points
                    .Select(point => new ThermalPointDocument
                    {
                        TemperatureCelsius = point.TemperatureCelsius,
                        Rpm = point.TargetRpm.Value
                    })
                    .ToArray(),
                Gpu = profile.GpuCurve.Points
                    .Select(point => new ThermalPointDocument
                    {
                        TemperatureCelsius = point.TemperatureCelsius,
                        Rpm = point.TargetRpm.Value
                    })
                    .ToArray()
            },
            Options);
    }

    private static ThermalCurve CreateCurve(
        IReadOnlyList<ThermalPointDocument>? points,
        string property)
    {
        if (points is null)
        {
            throw new FormatException($"The '{property}' curve is missing.");
        }

        try
        {
            return new ThermalCurve(points.Select(point =>
                new ThermalCurvePoint(
                    point.TemperatureCelsius,
                    new FanRpm(point.Rpm))));
        }
        catch (ArgumentException exception)
        {
            throw new FormatException($"The '{property}' curve is invalid: {exception.Message}", exception);
        }
    }

    private sealed class ThermalProfileDocument
    {
        public string? Name { get; set; }

        public IReadOnlyList<ThermalPointDocument>? Cpu { get; set; }

        public IReadOnlyList<ThermalPointDocument>? Gpu { get; set; }
    }

    private sealed class ThermalPointDocument
    {
        public double TemperatureCelsius { get; set; }

        public int Rpm { get; set; }
    }
}
