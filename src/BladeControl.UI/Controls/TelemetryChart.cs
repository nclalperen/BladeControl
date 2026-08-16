using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BladeControl.UI.Services;
using BladeControl.UI.ViewModels;

namespace BladeControl.UI.Controls;

/// <summary>
/// Lightweight retained-mode renderer for the bounded telemetry held by a
/// <see cref="ChartViewModel"/>. The control never samples hardware and owns no timer;
/// it redraws only when the ViewModel revision or a series' visibility changes.
/// </summary>
public sealed class TelemetryChart : FrameworkElement
{
    private const int MaximumRenderedPoints = 512;
    private const int VerticalGridDivisions = 4;
    private const double DefaultWidth = 420;
    private const double DefaultHeight = 220;
    private const double OuterPadding = 12;
    private const double HeaderHeight = 62;
    private const double TimeAxisHeight = 24;
    private const double MinimumPlotWidth = 80;
    private const double MinimumPlotHeight = 44;

    private static readonly Typeface RegularTypeface = new("Segoe UI");
    private static readonly Typeface SemiboldTypeface = new(
        new FontFamily("Segoe UI"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    private static readonly Brush BackgroundBrush = FrozenBrush(0x0E, 0x13, 0x10);
    private static readonly Brush PlotBackgroundBrush = FrozenBrush(0x0B, 0x10, 0x0D);
    private static readonly Brush PrimaryTextBrush = FrozenBrush(0xED, 0xF2, 0xEE);
    private static readonly Brush SecondaryTextBrush = FrozenBrush(0xA0, 0xAB, 0xA3);
    private static readonly Brush MutedTextBrush = FrozenBrush(0x70, 0x7C, 0x74);
    private static readonly Brush EndpointHaloBrush = FrozenBrush(0x0B, 0x10, 0x0D);
    private static readonly Pen FramePen = FrozenPen(Color.FromRgb(0x27, 0x31, 0x29), 1);
    private static readonly Pen PlotBorderPen = FrozenPen(Color.FromRgb(0x2B, 0x36, 0x2E), 1);
    private static readonly Pen GridPen = FrozenPen(
        Color.FromArgb(0x7A, 0x31, 0x3D, 0x34),
        1,
        new DashStyle([2, 4], 0));

    private readonly Dictionary<string, SeriesVisual> _seriesVisuals =
        new(StringComparer.OrdinalIgnoreCase);
    private ChartViewModel? _subscribedChart;

    public TelemetryChart()
    {
        ClipToBounds = true;
        Focusable = false;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public static readonly DependencyProperty ChartProperty = DependencyProperty.Register(
        nameof(Chart),
        typeof(ChartViewModel),
        typeof(TelemetryChart),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnChartChanged));

    public ChartViewModel? Chart
    {
        get => (ChartViewModel?)GetValue(ChartProperty);
        set => SetValue(ChartProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width)
            ? DefaultWidth
            : Math.Max(0, availableSize.Width);
        double height = double.IsInfinity(availableSize.Height)
            ? DefaultHeight
            : Math.Max(0, availableSize.Height);
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 2 || height < 2)
        {
            return;
        }

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Rect frame = new(0.5, 0.5, width - 1, height - 1);
        drawingContext.DrawRoundedRectangle(BackgroundBrush, FramePen, frame, 9, 9);

        ChartViewModel? chart = Chart;
        if (chart is null)
        {
            DrawCenteredMessage(drawingContext, "Chart unavailable", frame, pixelsPerDip);
            return;
        }

        int visibleSeriesCount = DrawHeader(drawingContext, chart, width, pixelsPerDip);
        if (height <= HeaderHeight + TimeAxisHeight + MinimumPlotHeight)
        {
            return;
        }

        ChartBounds bounds = GetBounds(chart);
        AxisRange axis = bounds.HasFiniteValues
            ? CreateAxisRange(chart.Unit, bounds.MinimumValue, bounds.MaximumValue)
            : DefaultAxisRange(chart.Unit);

        double labelWidth = MeasureAxisLabelWidth(axis, chart.Unit, pixelsPerDip);
        Rect plot = new(
            OuterPadding + labelWidth + 8,
            HeaderHeight,
            width - (OuterPadding * 2) - labelWidth - 8,
            height - HeaderHeight - TimeAxisHeight);

        if (plot.Width < MinimumPlotWidth || plot.Height < MinimumPlotHeight)
        {
            return;
        }

        DrawGridAndAxes(drawingContext, plot, axis, bounds, chart.Unit, pixelsPerDip);

        if (visibleSeriesCount == 0)
        {
            DrawCenteredMessage(drawingContext, "No series selected", plot, pixelsPerDip);
            return;
        }

        if (!bounds.HasFiniteValues)
        {
            DrawCenteredMessage(drawingContext, "Waiting for telemetry", plot, pixelsPerDip);
            return;
        }

        drawingContext.PushClip(new RectangleGeometry(plot));
        foreach (ChartSeriesViewModel series in chart.Series)
        {
            if (series.IsVisible)
            {
                DrawSeries(drawingContext, series, plot, axis, bounds);
            }
        }

        drawingContext.Pop();
    }

    private static void OnChartChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        TelemetryChart control = (TelemetryChart)dependencyObject;
        if (control.IsLoaded)
        {
            control.Subscribe((ChartViewModel?)args.NewValue);
        }

        control.RequestRender();
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => Subscribe(Chart);

    private void OnUnloaded(object sender, RoutedEventArgs args) => Subscribe(null);

    private void Subscribe(ChartViewModel? chart)
    {
        if (ReferenceEquals(_subscribedChart, chart))
        {
            return;
        }

        if (_subscribedChart is not null)
        {
            _subscribedChart.PropertyChanged -= OnChartPropertyChanged;
            foreach (ChartSeriesViewModel series in _subscribedChart.Series)
            {
                series.PropertyChanged -= OnSeriesPropertyChanged;
            }
        }

        _subscribedChart = chart;
        if (_subscribedChart is null)
        {
            return;
        }

        _subscribedChart.PropertyChanged += OnChartPropertyChanged;
        foreach (ChartSeriesViewModel series in _subscribedChart.Series)
        {
            series.PropertyChanged += OnSeriesPropertyChanged;
        }
    }

    private void OnChartPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) ||
            args.PropertyName == nameof(ChartViewModel.Revision))
        {
            RequestRender();
        }
    }

    private void OnSeriesPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) ||
            args.PropertyName == nameof(ChartSeriesViewModel.IsVisible) ||
            args.PropertyName == nameof(ChartSeriesViewModel.LatestText) ||
            args.PropertyName == nameof(ChartSeriesViewModel.HasData))
        {
            RequestRender();
        }
    }

    private void RequestRender()
    {
        if (Dispatcher.CheckAccess())
        {
            InvalidateVisual();
            return;
        }

        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                new Action(InvalidateVisual));
        }
    }

    private int DrawHeader(
        DrawingContext drawingContext,
        ChartViewModel chart,
        double width,
        double pixelsPerDip)
    {
        FormattedText title = CreateText(
            chart.Title,
            13.5,
            PrimaryTextBrush,
            SemiboldTypeface,
            pixelsPerDip);
        title.Trimming = TextTrimming.CharacterEllipsis;
        title.MaxTextWidth = Math.Max(1, width - (OuterPadding * 2));
        drawingContext.DrawText(title, new Point(OuterPadding, 9));

        int visibleCount = 0;
        foreach (ChartSeriesViewModel series in chart.Series)
        {
            if (series.IsVisible)
            {
                visibleCount++;
            }
        }

        if (visibleCount == 0)
        {
            FormattedText hidden = CreateText(
                "All series hidden",
                10.5,
                MutedTextBrush,
                RegularTypeface,
                pixelsPerDip);
            drawingContext.DrawText(hidden, new Point(OuterPadding, 35));
            return 0;
        }

        double availableWidth = Math.Max(1, width - (OuterPadding * 2));
        double gap = visibleCount > 1 ? 14 : 0;
        double slotWidth = Math.Max(1, (availableWidth - (gap * (visibleCount - 1))) / visibleCount);
        double x = OuterPadding;

        foreach (ChartSeriesViewModel series in chart.Series)
        {
            if (!series.IsVisible)
            {
                continue;
            }

            SeriesVisual visual = GetSeriesVisual(series.ColorHex);
            double swatchY = 42;
            drawingContext.DrawLine(visual.Pen, new Point(x, swatchY), new Point(x + 11, swatchY));
            drawingContext.DrawEllipse(visual.Brush, null, new Point(x + 5.5, swatchY), 2.25, 2.25);

            FormattedText legend = CreateText(
                $"{series.Label}  {series.LatestText}",
                10.5,
                SecondaryTextBrush,
                RegularTypeface,
                pixelsPerDip);
            legend.Trimming = TextTrimming.CharacterEllipsis;
            legend.MaxTextWidth = Math.Max(1, slotWidth - 18);
            drawingContext.DrawText(legend, new Point(x + 17, 34));
            x += slotWidth + gap;
        }

        return visibleCount;
    }

    private void DrawGridAndAxes(
        DrawingContext drawingContext,
        Rect plot,
        AxisRange axis,
        ChartBounds bounds,
        string unit,
        double pixelsPerDip)
    {
        drawingContext.DrawRectangle(PlotBackgroundBrush, null, plot);

        int horizontalIntervals = Math.Clamp(
            (int)Math.Round((axis.Maximum - axis.Minimum) / axis.Step),
            1,
            10);
        for (int index = 0; index <= horizontalIntervals; index++)
        {
            double fraction = index / (double)horizontalIntervals;
            double y = plot.Bottom - (fraction * plot.Height);
            drawingContext.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));

            double value = axis.Minimum + (fraction * (axis.Maximum - axis.Minimum));
            FormattedText label = CreateText(
                FormatAxisValue(value, unit),
                9.5,
                MutedTextBrush,
                RegularTypeface,
                pixelsPerDip);
            label.TextAlignment = TextAlignment.Right;
            drawingContext.DrawText(label, new Point(plot.Left - 6, y - (label.Height / 2)));
        }

        for (int index = 0; index <= VerticalGridDivisions; index++)
        {
            double fraction = index / (double)VerticalGridDivisions;
            double x = plot.Left + (fraction * plot.Width);
            drawingContext.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }

        drawingContext.DrawRectangle(null, PlotBorderPen, plot);

        if (!bounds.HasTimestamps)
        {
            return;
        }

        string timeFormat = bounds.EndTicks - bounds.StartTicks < TimeSpan.FromSeconds(10).Ticks
            ? "HH:mm:ss.f"
            : "HH:mm:ss";
        for (int index = 0; index <= 2; index++)
        {
            double fraction = index / 2d;
            long ticks = bounds.StartTicks +
                (long)((bounds.EndTicks - bounds.StartTicks) * fraction);
            string text = new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc))
                .ToLocalTime()
                .ToString(timeFormat, CultureInfo.CurrentCulture);
            FormattedText label = CreateText(
                text,
                9.5,
                MutedTextBrush,
                RegularTypeface,
                pixelsPerDip);
            label.TextAlignment = index switch
            {
                0 => TextAlignment.Left,
                1 => TextAlignment.Center,
                _ => TextAlignment.Right
            };
            double x = plot.Left + (fraction * plot.Width);
            drawingContext.DrawText(label, new Point(x, plot.Bottom + 5));
        }
    }

    private void DrawSeries(
        DrawingContext drawingContext,
        ChartSeriesViewModel seriesViewModel,
        Rect plot,
        AxisRange axis,
        ChartBounds bounds)
    {
        MetricSeries series = seriesViewModel.Series;
        int count = series.Count;
        if (count == 0)
        {
            return;
        }

        int start = Math.Max(0, count - MaximumRenderedPoints);
        long gapThresholdTicks = GetGapThresholdTicks(series, start, count);
        SeriesVisual visual = GetSeriesVisual(seriesViewModel.ColorHex);
        StreamGeometry geometry = new();
        Point? currentPoint = null;
        bool hasPoint = false;
        bool continueFigure = false;
        long previousTicks = 0;

        using (StreamGeometryContext context = geometry.Open())
        {
            for (int index = start; index < count; index++)
            {
                long ticks = series.TimestampAt(index).UtcDateTime.Ticks;
                bool invalidTimestamp = ticks <= 0 ||
                    (previousTicks > 0 && ticks <= previousTicks);
                bool largeGap = previousTicks > 0 &&
                    ticks > previousTicks &&
                    ticks - previousTicks > gapThresholdTicks;
                previousTicks = ticks;

                double value = series[index];
                if (invalidTimestamp || !double.IsFinite(value))
                {
                    continueFigure = false;
                    currentPoint = null;
                    continue;
                }

                if (largeGap)
                {
                    continueFigure = false;
                }

                double xFraction = (ticks - bounds.StartTicks) /
                    (double)(bounds.EndTicks - bounds.StartTicks);
                double yFraction = (value - axis.Minimum) / (axis.Maximum - axis.Minimum);
                Point point = new(
                    plot.Left + (Math.Clamp(xFraction, 0, 1) * plot.Width),
                    plot.Bottom - (Math.Clamp(yFraction, 0, 1) * plot.Height));

                if (continueFigure)
                {
                    context.LineTo(point, true, false);
                }
                else
                {
                    context.BeginFigure(point, false, false);
                }

                continueFigure = true;
                hasPoint = true;
                currentPoint = index == count - 1 ? point : null;
            }
        }

        if (!hasPoint)
        {
            return;
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, visual.Pen, geometry);

        if (currentPoint is { } endpoint)
        {
            drawingContext.DrawEllipse(EndpointHaloBrush, null, endpoint, 4, 4);
            drawingContext.DrawEllipse(visual.Brush, null, endpoint, 2.5, 2.5);
        }
    }

    private static ChartBounds GetBounds(ChartViewModel chart)
    {
        long startTicks = long.MaxValue;
        long endTicks = long.MinValue;
        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;

        foreach (ChartSeriesViewModel seriesViewModel in chart.Series)
        {
            if (!seriesViewModel.IsVisible)
            {
                continue;
            }

            MetricSeries series = seriesViewModel.Series;
            int count = series.Count;
            int start = Math.Max(0, count - MaximumRenderedPoints);
            for (int index = start; index < count; index++)
            {
                long ticks = series.TimestampAt(index).UtcDateTime.Ticks;
                double value = series[index];
                if (ticks > 0)
                {
                    startTicks = Math.Min(startTicks, ticks);
                    endTicks = Math.Max(endTicks, ticks);
                }

                if (ticks <= 0 || !double.IsFinite(value))
                {
                    continue;
                }

                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }
        }

        bool hasTimestamps = startTicks != long.MaxValue;
        bool hasFiniteValues = double.IsFinite(minimum) && double.IsFinite(maximum);
        if (!hasTimestamps)
        {
            startTicks = 0;
            endTicks = 1;
        }
        else if (startTicks == endTicks)
        {
            startTicks -= TimeSpan.TicksPerSecond;
            endTicks += TimeSpan.TicksPerSecond;
        }

        return new ChartBounds(
            startTicks,
            endTicks,
            minimum,
            maximum,
            hasTimestamps,
            hasFiniteValues);
    }

    private static long GetGapThresholdTicks(MetricSeries series, int start, int count)
    {
        int intervalCapacity = Math.Min(Math.Max(0, count - start - 1), MaximumRenderedPoints - 1);
        if (intervalCapacity == 0)
        {
            return TimeSpan.FromSeconds(2).Ticks;
        }

        Span<long> intervals = stackalloc long[intervalCapacity];
        int intervalCount = 0;
        long previousTicks = series.TimestampAt(start).UtcDateTime.Ticks;
        for (int index = start + 1; index < count; index++)
        {
            long ticks = series.TimestampAt(index).UtcDateTime.Ticks;
            if (ticks > previousTicks && previousTicks > 0)
            {
                intervals[intervalCount++] = ticks - previousTicks;
            }

            previousTicks = ticks;
        }

        if (intervalCount == 0)
        {
            return TimeSpan.FromSeconds(2).Ticks;
        }

        Span<long> populated = intervals[..intervalCount];
        populated.Sort();
        long medianTicks = populated[intervalCount / 2];
        long minimumGapTicks = TimeSpan.FromSeconds(2).Ticks;
        long maximumGapTicks = TimeSpan.FromSeconds(3).Ticks;
        long adaptiveGapTicks = medianTicks > maximumGapTicks / 4
            ? maximumGapTicks
            : medianTicks * 4;
        return Math.Clamp(adaptiveGapTicks, minimumGapTicks, maximumGapTicks);
    }

    private static AxisRange CreateAxisRange(string unit, double minimum, double maximum)
    {
        if (string.Equals(unit, "%", StringComparison.Ordinal))
        {
            double lower = Math.Min(0, minimum);
            double upper = Math.Max(100, maximum);
            if (lower == 0 && upper == 100)
            {
                return new AxisRange(0, 100, 25);
            }

            return RoundAxis(lower, upper);
        }

        double minimumSpan = string.Equals(unit, "RPM", StringComparison.OrdinalIgnoreCase)
            ? 400
            : string.Equals(unit, "W", StringComparison.OrdinalIgnoreCase)
                ? 5
                : 4;
        double span = maximum - minimum;
        double lowerBound;
        double upperBound;
        if (span < minimumSpan)
        {
            double center = minimum + (span / 2);
            lowerBound = center - (minimumSpan / 2);
            upperBound = center + (minimumSpan / 2);
        }
        else
        {
            double padding = span * 0.1;
            lowerBound = minimum - padding;
            upperBound = maximum + padding;
        }

        if (minimum >= 0 && lowerBound < 0)
        {
            lowerBound = 0;
        }

        return RoundAxis(lowerBound, upperBound);
    }

    private static AxisRange RoundAxis(double minimum, double maximum)
    {
        double step = NiceNumber((maximum - minimum) / VerticalGridDivisions);
        double roundedMinimum = Math.Floor(minimum / step) * step;
        double roundedMaximum = Math.Ceiling(maximum / step) * step;
        if (roundedMaximum <= roundedMinimum)
        {
            roundedMaximum = roundedMinimum + step;
        }

        return new AxisRange(roundedMinimum, roundedMaximum, step);
    }

    private static AxisRange DefaultAxisRange(string unit) => unit switch
    {
        "%" => new AxisRange(0, 100, 25),
        "RPM" => new AxisRange(0, 5000, 1000),
        _ => new AxisRange(0, 100, 25)
    };

    private static double NiceNumber(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            return 1;
        }

        double exponent = Math.Floor(Math.Log10(value));
        double magnitude = Math.Pow(10, exponent);
        double fraction = value / magnitude;
        double niceFraction = fraction switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 5 => 5,
            _ => 10
        };
        return niceFraction * magnitude;
    }

    private double MeasureAxisLabelWidth(AxisRange axis, string unit, double pixelsPerDip)
    {
        double width = 0;
        int intervals = Math.Clamp(
            (int)Math.Round((axis.Maximum - axis.Minimum) / axis.Step),
            1,
            10);
        for (int index = 0; index <= intervals; index++)
        {
            double fraction = index / (double)intervals;
            double value = axis.Minimum + (fraction * (axis.Maximum - axis.Minimum));
            FormattedText label = CreateText(
                FormatAxisValue(value, unit),
                9.5,
                MutedTextBrush,
                RegularTypeface,
                pixelsPerDip);
            width = Math.Max(width, label.WidthIncludingTrailingWhitespace);
        }

        return Math.Clamp(width, 20, 58);
    }

    private static string FormatAxisValue(double value, string unit)
    {
        if (Math.Abs(value) < 0.000_000_1)
        {
            value = 0;
        }

        string format = string.Equals(unit, "RPM", StringComparison.OrdinalIgnoreCase) ||
                        Math.Abs(value) >= 100
            ? "N0"
            : "0.#";
        return value.ToString(format, CultureInfo.CurrentCulture);
    }

    private void DrawCenteredMessage(
        DrawingContext drawingContext,
        string message,
        Rect area,
        double pixelsPerDip)
    {
        FormattedText text = CreateText(
            message,
            11,
            MutedTextBrush,
            RegularTypeface,
            pixelsPerDip);
        Point origin = new(
            area.Left + Math.Max(0, (area.Width - text.Width) / 2),
            area.Top + Math.Max(0, (area.Height - text.Height) / 2));
        drawingContext.DrawText(text, origin);
    }

    private FormattedText CreateText(
        string text,
        double fontSize,
        Brush brush,
        Typeface typeface,
        double pixelsPerDip) => new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection,
            typeface,
            fontSize,
            brush,
            pixelsPerDip);

    private SeriesVisual GetSeriesVisual(string colorHex)
    {
        if (_seriesVisuals.TryGetValue(colorHex, out SeriesVisual? visual))
        {
            return visual;
        }

        Color color = ParseColor(colorHex);
        SolidColorBrush brush = new(color);
        brush.Freeze();
        Pen pen = new(brush, 1.75)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        visual = new SeriesVisual(brush, pen);
        _seriesVisuals[colorHex] = visual;
        return visual;
    }

    private static Color ParseColor(string colorHex)
    {
        ReadOnlySpan<char> value = colorHex.AsSpan();
        if (value.Length == 7 && value[0] == '#' &&
            uint.TryParse(
                value[1..],
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out uint rgb))
        {
            return Color.FromRgb(
                (byte)(rgb >> 16),
                (byte)(rgb >> 8),
                (byte)rgb);
        }

        if (value.Length == 9 && value[0] == '#' &&
            uint.TryParse(
                value[1..],
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out uint argb))
        {
            return Color.FromArgb(
                (byte)(argb >> 24),
                (byte)(argb >> 16),
                (byte)(argb >> 8),
                (byte)argb);
        }

        return Color.FromRgb(0x46, 0xD2, 0x7B);
    }

    private static SolidColorBrush FrozenBrush(byte red, byte green, byte blue)
    {
        SolidColorBrush brush = new(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness, DashStyle? dashStyle = null)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        if (dashStyle?.CanFreeze == true)
        {
            dashStyle.Freeze();
        }

        Pen pen = new(brush, thickness)
        {
            DashStyle = dashStyle ?? DashStyles.Solid
        };
        pen.Freeze();
        return pen;
    }

    private sealed record SeriesVisual(Brush Brush, Pen Pen);

    private readonly record struct AxisRange(double Minimum, double Maximum, double Step);

    private readonly record struct ChartBounds(
        long StartTicks,
        long EndTicks,
        double MinimumValue,
        double MaximumValue,
        bool HasTimestamps,
        bool HasFiniteValues);
}
