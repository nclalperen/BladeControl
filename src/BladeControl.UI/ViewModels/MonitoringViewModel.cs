using System.Globalization;
using BladeControl.Runtime;
using BladeControl.UI.Services;

namespace BladeControl.UI.ViewModels;

public sealed class ChartSeriesViewModel : ObservableObject
{
    private bool _isVisible = true;

    public ChartSeriesViewModel(MetricSeries series, string colorHex)
    {
        Series = series ?? throw new ArgumentNullException(nameof(series));
        ColorHex = colorHex;
    }

    public MetricSeries Series { get; }

    /// <summary>Stroke colour as hex. Kept as a string so ViewModels stay free of WPF types.</summary>
    public string ColorHex { get; }

    public string Label => Series.Label;

    public string Unit => Series.Unit;

    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }

    public bool HasData => Series.Latest.HasValue;

    public string LatestText => Series.Latest is { } value
        ? $"{value.ToString(Unit == "RPM" ? "N0" : "0.0", CultureInfo.CurrentCulture)} {Unit}"
        : Display.Unavailable;

    internal void RaiseSampleChanged() => RaiseAll(nameof(LatestText), nameof(HasData));
}

public sealed class ChartViewModel : ObservableObject
{
    private int _revision;

    public ChartViewModel(string title, string unit, params ChartSeriesViewModel[] series)
    {
        Title = title;
        Unit = unit;
        Series = series;
    }

    public string Title { get; }

    public string Unit { get; }

    public IReadOnlyList<ChartSeriesViewModel> Series { get; }

    /// <summary>Bumped after every appended sample so the chart control repaints once.</summary>
    public int Revision
    {
        get => _revision;
        private set => Set(ref _revision, value);
    }

    public bool HasAnyData => Series.Any(item => item.Series.Count > 0);

    internal void Invalidate()
    {
        foreach (ChartSeriesViewModel series in Series)
        {
            series.RaiseSampleChanged();
        }

        Revision = unchecked(_revision + 1);
        Raise(nameof(HasAnyData));
    }
}

/// <summary>
/// Real-time graphs fed exclusively by telemetry that arrived over IPC. The page never
/// acquires telemetry itself and keeps only a bounded rolling window in memory.
/// </summary>
public sealed class MonitoringViewModel : PageViewModel
{
    private const string TemperatureCpuColor = "#3FB950";
    private const string TemperatureGpuColor = "#58A6FF";
    private const string FanColor = "#D9A441";
    private const string CpuSecondaryColor = "#3FB950";
    private const string GpuSecondaryColor = "#58A6FF";

    private readonly TelemetryHistory _history;
    private int _windowSeconds = 120;
    private bool _presentationActive;

    public MonitoringViewModel(
        RuntimeConnection connection,
        CancellationToken lifetime,
        int windowSeconds = 120)
        : base(
            connection,
            lifetime,
            "Monitoring",
            "Monitoring",
            "Rolling telemetry from the runtime, kept in memory only",
            Icons.Monitoring)
    {
        _windowSeconds = windowSeconds is 60 or 120 ? windowSeconds : 120;
        _history = new TelemetryHistory(TimeSpan.FromSeconds(_windowSeconds));

        Temperatures = new ChartViewModel(
            "Temperatures",
            "°C",
            new ChartSeriesViewModel(_history[TelemetryHistory.CpuTemperatureKey], TemperatureCpuColor),
            new ChartSeriesViewModel(_history[TelemetryHistory.GpuTemperatureKey], TemperatureGpuColor));
        FanTarget = new ChartViewModel(
            "Effective fan target",
            "RPM",
            new ChartSeriesViewModel(_history[TelemetryHistory.FanTargetKey], FanColor));
        Power = new ChartViewModel(
            "Package power",
            "W",
            new ChartSeriesViewModel(_history[TelemetryHistory.CpuPowerKey], CpuSecondaryColor),
            new ChartSeriesViewModel(_history[TelemetryHistory.GpuPowerKey], GpuSecondaryColor));
        Utilization = new ChartViewModel(
            "Utilization",
            "%",
            new ChartSeriesViewModel(_history[TelemetryHistory.CpuLoadKey], CpuSecondaryColor),
            new ChartSeriesViewModel(_history[TelemetryHistory.GpuUtilizationKey], GpuSecondaryColor));
        Charts = [Temperatures, FanTarget, Power, Utilization];

        ClearCommand = new RelayCommand(Clear);
        Connection.TelemetryObserved += OnTelemetryObserved;
    }

    public ChartViewModel Temperatures { get; }

    public ChartViewModel FanTarget { get; }

    public ChartViewModel Power { get; }

    public ChartViewModel Utilization { get; }

    public IReadOnlyList<ChartViewModel> Charts { get; }

    public RelayCommand ClearCommand { get; }

    public TelemetryHistory History => _history;

    /// <summary>Rolling retention window in seconds. Only 60 and 120 are offered.</summary>
    public int WindowSeconds
    {
        get => _windowSeconds;
        set
        {
            int clamped = value is 60 or 120 ? value : 120;
            if (Set(ref _windowSeconds, clamped))
            {
                _history.Window = TimeSpan.FromSeconds(clamped);
                RaiseAll(nameof(Is60Seconds), nameof(Is120Seconds), nameof(WindowLabel));
                InvalidateCharts();
            }
        }
    }

    public bool Is60Seconds
    {
        get => _windowSeconds == 60;
        set
        {
            if (value)
            {
                WindowSeconds = 60;
            }
        }
    }

    public bool Is120Seconds
    {
        get => _windowSeconds == 120;
        set
        {
            if (value)
            {
                WindowSeconds = 120;
            }
        }
    }

    public string WindowLabel => $"Last {_windowSeconds} s";

    public int SampleCount => _history.SampleCount;

    public string SampleCountLabel =>
        $"{_history.SampleCount} samples retained (nothing written to disk)";

    public string SourceLabel => Connection.TelemetryOrigin switch
    {
        TelemetryOrigin.ThermalSession => "Source: authoritative thermal-session samples",
        TelemetryOrigin.ProviderSample => "Source: provider-only Runtime Core samples",
        TelemetryOrigin.DiagnosticSnapshot => "Source: on-demand diagnostic acquisitions",
        _ => "Source: waiting for the first runtime sample"
    };

    public bool IsStale => !Connection.IsOnline || Connection.IsTelemetryStale;

    public string StaleLabel => Connection.IsOnline
        ? "Graph paused — telemetry is stale"
        : "Graph paused — Runtime Core is offline";

    public void Clear()
    {
        _history.Clear();
        InvalidateCharts();
        Raise(nameof(SampleCount));
        Raise(nameof(SampleCountLabel));
    }

    public override void Refresh() =>
        RaiseAll(nameof(SourceLabel), nameof(IsStale), nameof(StaleLabel));

    public override void Activate() => Refresh();

    public void SetPresentationActive(bool active)
    {
        if (_presentationActive == active)
        {
            return;
        }

        _presentationActive = active;
        if (active)
        {
            Refresh();
            InvalidateCharts();
        }
    }

    /// <summary>Records a sample. Public so tests can drive history without a live runtime.</summary>
    public void Append(ThermalTelemetrySampleDto sample)
    {
        if (!_history.Append(sample, Connection.Status?.CurrentEffectiveFanTargetRpm))
        {
            return;
        }

        if (_presentationActive)
        {
            InvalidateCharts();
            RaiseAll(nameof(SampleCount), nameof(SampleCountLabel));
        }
    }

    private void OnTelemetryObserved(ThermalTelemetrySampleDto sample) => Append(sample);

    private void InvalidateCharts()
    {
        foreach (ChartViewModel chart in Charts)
        {
            chart.Invalidate();
        }
    }
}
