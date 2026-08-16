using BladeControl.Razer;
using BladeControl.Runtime;
using BladeControl.Thermal;

namespace BladeControl.UI.ViewModels;

public sealed record ThermalCurveValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyDictionary<int, string> PointErrors)
{
    public static ThermalCurveValidationResult Valid { get; } =
        new(true, [], new Dictionary<int, string>());
}

/// <summary>
/// Mirrors the constraints Runtime Core's <see cref="ThermalCurve"/> and
/// <see cref="FanRpm"/> already enforce, so the editor can show inline errors instead of
/// letting the user build a curve the runtime would reject. The UI validates and edits
/// only: interpolation, hysteresis and every thermal decision stay in Runtime Core.
/// </summary>
public static class ThermalCurveValidator
{
    /// <summary>Lowest RPM a dynamic curve point may request (Runtime Core constraint).</summary>
    public static int MinimumRpm => ThermalCurve.MinimumDynamicRpm;

    /// <summary>Highest RPM the fan model accepts.</summary>
    public static int MaximumRpm => FanRpm.MaximumValue;

    /// <summary>RPM values must land on this increment.</summary>
    public static int RpmIncrement => FanRpm.Increment;

    public const double MinimumExclusiveTemperature = 0;

    public const double MaximumExclusiveTemperature = 120;

    public const int MinimumPoints = 2;

    public static ThermalCurveValidationResult Validate(
        IReadOnlyList<StoredThermalCurvePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var errors = new List<string>();
        var pointErrors = new Dictionary<int, string>();

        if (points.Count < MinimumPoints)
        {
            errors.Add($"A curve needs at least {MinimumPoints} points.");
        }

        for (int index = 0; index < points.Count; index++)
        {
            StoredThermalCurvePoint point = points[index];
            string? error = ValidatePoint(point);
            if (error is null && index > 0)
            {
                error = ValidateAgainstPrevious(points[index - 1], point);
            }

            if (error is not null)
            {
                pointErrors[index] = error;
                errors.Add($"Point {index + 1}: {error}");
            }
        }

        return errors.Count == 0
            ? ThermalCurveValidationResult.Valid
            : new ThermalCurveValidationResult(false, errors, pointErrors);
    }

    private static string? ValidatePoint(StoredThermalCurvePoint point)
    {
        if (!double.IsFinite(point.TemperatureCelsius) ||
            point.TemperatureCelsius <= MinimumExclusiveTemperature ||
            point.TemperatureCelsius >= MaximumExclusiveTemperature)
        {
            return $"temperature must be above {MinimumExclusiveTemperature:0} C and below " +
                $"{MaximumExclusiveTemperature:0} C.";
        }

        if (point.Rpm < MinimumRpm || point.Rpm > MaximumRpm)
        {
            return $"fan target must be between {MinimumRpm} and {MaximumRpm} RPM.";
        }

        if (point.Rpm % RpmIncrement != 0)
        {
            return $"fan target must be a multiple of {RpmIncrement} RPM.";
        }

        return null;
    }

    private static string? ValidateAgainstPrevious(
        StoredThermalCurvePoint previous,
        StoredThermalCurvePoint current)
    {
        if (current.TemperatureCelsius <= previous.TemperatureCelsius)
        {
            return "temperature must be strictly higher than the previous point.";
        }

        if (current.Rpm < previous.Rpm)
        {
            return "fan target must not drop below the previous point.";
        }

        return null;
    }
}
