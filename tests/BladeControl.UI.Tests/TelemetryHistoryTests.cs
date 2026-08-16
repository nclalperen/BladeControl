using BladeControl.UI.Ipc;
using BladeControl.UI.Services;
using BladeControl.UI.ViewModels;

namespace BladeControl.UI.Tests;

[TestClass]
public sealed class TelemetryHistoryTests
{
    [TestMethod]
    public void MetricSeriesOverwritesOldestSamplesAtFixedCapacity()
    {
        var series = new MetricSeries("cpu", "CPU", "C", capacity: 3);
        DateTimeOffset start = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

        series.Append(start, 10);
        series.Append(start.AddSeconds(1), 20);
        series.Append(start.AddSeconds(2), 30);
        series.Append(start.AddSeconds(3), 40);

        Assert.AreEqual(3, series.Count);
        Assert.AreEqual(20d, series[0]);
        Assert.AreEqual(40d, series[2]);
        Assert.AreEqual(start.AddSeconds(1), series.TimestampAt(0));
        Assert.AreEqual(20d, series.Minimum()!.Value);
        Assert.AreEqual(40d, series.Maximum()!.Value);
        Assert.AreEqual(40d, series.Latest!.Value);
    }

    [TestMethod]
    public void TelemetryHistoryRejectsDuplicateAndOutOfOrderSamplesAndTrimsWindow()
    {
        var history = new TelemetryHistory(TimeSpan.FromSeconds(1));
        DateTimeOffset firstTimestamp = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
        var first = RuntimeUiSampleData.Telemetry(timestamp: firstTimestamp);
        var duplicate = RuntimeUiSampleData.Telemetry(timestamp: firstTimestamp);
        var older = RuntimeUiSampleData.Telemetry(timestamp: firstTimestamp.AddMilliseconds(-1));
        var nextWindow = RuntimeUiSampleData.Telemetry(timestamp: firstTimestamp.AddSeconds(2));

        Assert.IsTrue(history.Append(first, 3_200));
        Assert.IsFalse(history.Append(duplicate, 3_300));
        Assert.IsFalse(history.Append(older, 3_400));
        Assert.IsTrue(history.Append(nextWindow, 3_500));

        Assert.AreEqual(1, history.SampleCount);
        Assert.AreEqual(firstTimestamp.AddSeconds(2), history.LatestTimestamp);
        Assert.AreEqual(3_500d, history[TelemetryHistory.FanTargetKey].Latest!.Value);
        Assert.AreEqual(
            firstTimestamp.AddSeconds(2),
            history[TelemetryHistory.CpuTemperatureKey].TimestampAt(0));
    }

    [TestMethod]
    public void MonitoringViewModelDoesNotInflateChartsForRepeatedSamples()
    {
        var client = new FakeRuntimeUiClient();
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        var monitoring = new MonitoringViewModel(
            connection,
            CancellationToken.None,
            windowSeconds: 60);
        DateTimeOffset timestamp = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
        var sample = RuntimeUiSampleData.Telemetry(timestamp: timestamp);

        monitoring.Append(sample);
        int revision = monitoring.Temperatures.Revision;
        monitoring.Append(sample);
        monitoring.Append(RuntimeUiSampleData.Telemetry(timestamp: timestamp.AddTicks(-1)));

        Assert.AreEqual(1, monitoring.SampleCount);
        Assert.AreEqual(revision, monitoring.Temperatures.Revision);
        Assert.IsTrue(monitoring.Temperatures.HasAnyData);
        Assert.AreEqual("Last 60 s", monitoring.WindowLabel);
    }
}
