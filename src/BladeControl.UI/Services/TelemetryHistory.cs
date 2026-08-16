using BladeControl.Runtime;

namespace BladeControl.UI.Services;

/// <summary>
/// Fixed-capacity ring of timestamped samples. Backing arrays are allocated once, so the
/// 500 ms refresh cadence adds no per-sample allocation. A missing reading is stored as
/// <see cref="double.NaN"/> so gaps stay visible instead of being interpolated away.
/// </summary>
public sealed class MetricSeries
{
    private readonly double[] _values;
    private readonly DateTimeOffset[] _timestamps;
    private int _start;
    private int _count;

    public MetricSeries(string key, string label, string unit, int capacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (capacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Key = key;
        Label = label;
        Unit = unit;
        _values = new double[capacity];
        _timestamps = new DateTimeOffset[capacity];
    }

    public string Key { get; }

    public string Label { get; }

    public string Unit { get; }

    public int Capacity => _values.Length;

    public int Count => _count;

    public bool HasData => _count > 0;

    public double this[int index] => _values[Index(index)];

    public DateTimeOffset TimestampAt(int index) => _timestamps[Index(index)];

    /// <summary>The most recent reading, or null when the series is empty or the last read failed.</summary>
    public double? Latest
    {
        get
        {
            if (_count == 0)
            {
                return null;
            }

            double value = _values[Index(_count - 1)];
            return double.IsNaN(value) ? null : value;
        }
    }

    public void Append(DateTimeOffset timestamp, double? value)
    {
        int slot = (_start + _count) % Capacity;
        if (_count == Capacity)
        {
            slot = _start;
            _start = (_start + 1) % Capacity;
        }
        else
        {
            _count++;
        }

        _timestamps[slot] = timestamp;
        _values[slot] = value ?? double.NaN;
    }

    /// <summary>Drops samples older than <paramref name="cutoff"/> from the front.</summary>
    public void TrimOlderThan(DateTimeOffset cutoff)
    {
        while (_count > 0 && _timestamps[_start] < cutoff)
        {
            _start = (_start + 1) % Capacity;
            _count--;
        }
    }

    public void Clear()
    {
        _start = 0;
        _count = 0;
    }

    /// <summary>Smallest finite value, or null when every sample is a gap.</summary>
    public double? Minimum() => Extreme(takeSmaller: true);

    /// <summary>Largest finite value, or null when every sample is a gap.</summary>
    public double? Maximum() => Extreme(takeSmaller: false);

    private double? Extreme(bool takeSmaller)
    {
        double? result = null;
        for (int index = 0; index < _count; index++)
        {
            double value = _values[Index(index)];
            if (double.IsNaN(value))
            {
                continue;
            }

            if (result is null || (takeSmaller ? value < result : value > result))
            {
                result = value;
            }
        }

        return result;
    }

    private int Index(int index)
    {
        if ((uint)index >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return (_start + index) % Capacity;
    }
}

/// <summary>
/// Bounded in-memory telemetry history for the Monitoring page. It only ever records what
/// arrived over IPC; the UI never acquires telemetry of its own.
/// </summary>
public sealed class TelemetryHistory
{
    public const string CpuTemperatureKey = "cpu-temperature";
    public const string GpuTemperatureKey = "gpu-temperature";
    public const string FanTargetKey = "fan-target";
    public const string CpuPowerKey = "cpu-power";
    public const string GpuPowerKey = "gpu-power";
    public const string CpuLoadKey = "cpu-load";
    public const string GpuUtilizationKey = "gpu-utilization";

    /// <summary>Sampling cadence assumed when sizing the ring buffers.</summary>
    private const double SamplesPerSecond = 2;

    private readonly Dictionary<string, MetricSeries> _series;
    private TimeSpan _window;

    public TelemetryHistory(TimeSpan? window = null)
    {
        _window = window ?? TimeSpan.FromSeconds(120);
        int capacity = CapacityFor(TimeSpan.FromSeconds(120));
        _series = new Dictionary<string, MetricSeries>(StringComparer.Ordinal)
        {
            [CpuTemperatureKey] = new(CpuTemperatureKey, "CPU package", "°C", capacity),
            [GpuTemperatureKey] = new(GpuTemperatureKey, "GPU", "°C", capacity),
            [FanTargetKey] = new(FanTargetKey, "Fan target", "RPM", capacity),
            [CpuPowerKey] = new(CpuPowerKey, "CPU package power", "W", capacity),
            [GpuPowerKey] = new(GpuPowerKey, "GPU power", "W", capacity),
            [CpuLoadKey] = new(CpuLoadKey, "CPU utilization", "%", capacity),
            [GpuUtilizationKey] = new(GpuUtilizationKey, "GPU utilization", "%", capacity)
        };
    }

    /// <summary>Rolling retention window. Nothing older is kept, and nothing is persisted.</summary>
    public TimeSpan Window
    {
        get => _window;
        set
        {
            _window = value <= TimeSpan.Zero ? TimeSpan.FromSeconds(60) : value;
            Trim(LatestTimestamp ?? DateTimeOffset.UtcNow);
        }
    }

    public DateTimeOffset? LatestTimestamp { get; private set; }

    public IReadOnlyCollection<MetricSeries> Series => _series.Values;

    public MetricSeries this[string key] => _series[key];

    public int SampleCount => _series[CpuTemperatureKey].Count;

    /// <summary>
    /// Records one telemetry sample plus the fan target the runtime reported alongside it.
    /// Duplicate timestamps are ignored so a repeated status poll cannot inflate history.
    /// </summary>
    public bool Append(ThermalTelemetrySampleDto sample, int? effectiveFanTargetRpm)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (LatestTimestamp == sample.Timestamp)
        {
            return false;
        }

        LatestTimestamp = sample.Timestamp;
        _series[CpuTemperatureKey].Append(
            sample.Timestamp,
            Reading(sample.CpuPackageTemperatureCelsius));
        _series[GpuTemperatureKey].Append(
            sample.Timestamp,
            Reading(sample.GpuTemperatureCelsius));
        _series[FanTargetKey].Append(sample.Timestamp, effectiveFanTargetRpm);
        _series[CpuPowerKey].Append(sample.Timestamp, Reading(sample.CpuPackagePowerWatts));
        _series[GpuPowerKey].Append(sample.Timestamp, Reading(sample.GpuPowerWatts));
        _series[CpuLoadKey].Append(sample.Timestamp, Reading(sample.CpuTotalLoadPercent));
        _series[GpuUtilizationKey].Append(
            sample.Timestamp,
            Reading(sample.GpuUtilizationPercent));
        Trim(sample.Timestamp);
        return true;
    }

    public void Clear()
    {
        foreach (MetricSeries series in _series.Values)
        {
            series.Clear();
        }

        LatestTimestamp = null;
    }

    private void Trim(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - _window;
        foreach (MetricSeries series in _series.Values)
        {
            series.TrimOlderThan(cutoff);
        }
    }

    private static int CapacityFor(TimeSpan window) =>
        (int)Math.Ceiling(window.TotalSeconds * SamplesPerSecond) + 8;

    private static double? Reading(TelemetryMetricDto<double> metric) =>
        metric.HasValue && metric.IsValid ? metric.Value : null;
}
