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
        Assert.AreEqual(
            "Latest sample: source unavailable",
            monitoring.SourceLabel,
            "An accepted public Append is data with unknown provenance, not a history still " +
                "waiting for its first sample.");
    }

    /// <summary>The source label describes the latest point, not an already mixed chart.</summary>
    /// <remarks>
    /// The rolling history crosses session boundaries, but SourceLabel used the origin of only
    /// the newest sample and worded it as the source of all plural "samples". After one provider
    /// point and one session point, the old label falsely described both as authoritative
    /// thermal-session data.
    /// </remarks>
    [TestMethod]
    public async Task MonitoringNamesTheLatestSourceWithoutRelabellingRetainedHistory()
    {
        DateTimeOffset first = new(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);
        var client = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(state: "Stopped"),
            TelemetrySample = RuntimeUiSampleData.Telemetry(timestamp: first)
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        var monitoring = new MonitoringViewModel(connection, CancellationToken.None);

        await connection.PollOnceAsync(CancellationToken.None);
        client.Status = RuntimeUiSampleData.Status(
            state: "Running",
            telemetry: RuntimeUiSampleData.Telemetry(timestamp: first.AddSeconds(1)));
        await connection.PollOnceAsync(CancellationToken.None);

        Assert.AreEqual(2, monitoring.SampleCount);
        StringAssert.StartsWith(monitoring.SourceLabel, "Latest sample:");
        StringAssert.Contains(monitoring.SourceLabel, "authoritative thermal-session sample");
        Assert.IsFalse(
            monitoring.SourceLabel.Contains("samples", StringComparison.Ordinal),
            "One latest-origin enum cannot make a provenance claim about the whole history.");
    }

    /// <summary>A rejected point cannot replace the provenance of the retained latest point.</summary>
    /// <remarks>
    /// RuntimeConnection updates its origin for every response, including an equal timestamp
    /// that raises no observation and an older timestamp that TelemetryHistory rejects. Reading
    /// that connection-wide origin directly relabelled the retained provider point as a thermal
    /// session point even though the chart had accepted no such point.
    /// </remarks>
    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task MonitoringPreservesAcceptedSourceWhenLaterOriginIsRejected(
        int timestampOffsetSeconds)
    {
        DateTimeOffset retained = new(2026, 8, 24, 10, 0, 1, TimeSpan.Zero);
        var client = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(state: "Stopped"),
            TelemetrySample = RuntimeUiSampleData.Telemetry(timestamp: retained)
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        var monitoring = new MonitoringViewModel(connection, CancellationToken.None);

        await connection.PollOnceAsync(CancellationToken.None);
        StringAssert.Contains(monitoring.SourceLabel, "provider-only Runtime Core sample");

        client.Status = RuntimeUiSampleData.Status(
            state: "Running",
            telemetry: RuntimeUiSampleData.Telemetry(
                timestamp: retained.AddSeconds(timestampOffsetSeconds)));
        await connection.PollOnceAsync(CancellationToken.None);

        Assert.AreEqual(TelemetryOrigin.ThermalSession, connection.TelemetryOrigin);
        Assert.AreEqual(1, monitoring.SampleCount);
        Assert.AreEqual(retained, monitoring.History.LatestTimestamp);
        StringAssert.Contains(monitoring.SourceLabel, "provider-only Runtime Core sample");
        Assert.IsFalse(
            monitoring.SourceLabel.Contains("thermal-session", StringComparison.Ordinal),
            "A response rejected by history cannot become the retained point's provenance.");
    }

    [TestMethod]
    public async Task ClearingMonitoringHistoryAlsoClearsItsProvenance()
    {
        var client = new FakeRuntimeUiClient();
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        var monitoring = new MonitoringViewModel(connection, CancellationToken.None);
        await connection.PollOnceAsync(CancellationToken.None);

        Assert.AreEqual(1, monitoring.SampleCount);
        Assert.IsFalse(monitoring.SourceLabel.Contains("waiting", StringComparison.Ordinal));

        monitoring.Clear();

        Assert.AreEqual(0, monitoring.SampleCount);
        StringAssert.Contains(monitoring.SourceLabel, "waiting for the first runtime sample");
    }
}
