using BladeControl.Telemetry;

namespace BladeControl.Thermal.Tests;

[TestClass]
public sealed class TelemetryModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AvailableMetricPreservesValueTimestampAndSource()
    {
        TelemetryMetric<double> metric = TelemetryMetric<double>.Available(
            52.5,
            Now,
            TelemetrySources.CpuPackageTemperature);

        Assert.AreEqual(52.5, metric.Value);
        Assert.AreEqual(Now, metric.Timestamp);
        Assert.IsTrue(metric.IsSupported);
        Assert.IsTrue(metric.IsValid);
        Assert.AreEqual(TelemetryFreshness.Fresh, metric.Freshness(Now, TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public void UnsupportedMetricNeverSilentlyBecomesZero()
    {
        TelemetryMetric<double> metric = TelemetryMetric<double>.Unsupported(
            TelemetrySources.GpuOptional);

        Assert.IsFalse(metric.HasValue);
        Assert.IsFalse(metric.IsSupported);
        Assert.IsFalse(metric.IsValid);
    }

    [TestMethod]
    public void OldRequiredMetricIsStale()
    {
        TelemetrySnapshot snapshot = Snapshot(50, 45, Now - TimeSpan.FromMilliseconds(2001), Now);

        TelemetryHealth health = TelemetryHealthEvaluator.Evaluate(snapshot, Now);

        Assert.AreEqual(TelemetryHealthKind.Stale, health.Kind);
        Assert.IsTrue(health.RequiresImmediateAuto);
    }

    [TestMethod]
    public void NonFiniteRequiredMetricIsInvalid()
    {
        TelemetrySnapshot snapshot = Snapshot(double.NaN, 45, Now, Now);

        TelemetryHealth health = TelemetryHealthEvaluator.Evaluate(snapshot, Now);

        Assert.AreEqual(TelemetryHealthKind.Invalid, health.Kind);
    }

    [TestMethod]
    public void MissingCpuTemperatureIsReported()
    {
        var snapshot = new TelemetrySnapshot(
            Now,
            TelemetryMetric<double>.Missing(
                Now,
                TelemetrySources.CpuPackageTemperature,
                "missing"),
            TelemetryMetric<double>.Available(45, Now, TelemetrySources.GpuTemperature));

        Assert.AreEqual(TelemetryHealthKind.Missing,
            TelemetryHealthEvaluator.Evaluate(snapshot, Now).Kind);
    }

    [TestMethod]
    public void MissingGpuTemperatureIsReported()
    {
        var snapshot = new TelemetrySnapshot(
            Now,
            TelemetryMetric<double>.Available(50, Now, TelemetrySources.CpuPackageTemperature),
            TelemetryMetric<double>.Unsupported(TelemetrySources.GpuTemperature));

        Assert.AreEqual(TelemetryHealthKind.Missing,
            TelemetryHealthEvaluator.Evaluate(snapshot, Now).Kind);
    }

    [TestMethod]
    [DataRow(0d)]
    [DataRow(120d)]
    [DataRow(-10d)]
    [DataRow(150d)]
    public void ImplausibleTemperatureIsInvalid(double temperature)
    {
        Assert.AreEqual(
            TelemetryHealthKind.Invalid,
            TelemetryHealthEvaluator.Evaluate(Snapshot(temperature, 45, Now, Now), Now).Kind);
    }

    private static TelemetrySnapshot Snapshot(
        double cpu,
        double gpu,
        DateTimeOffset cpuTime,
        DateTimeOffset gpuTime) => new(
            Now,
            TelemetryMetric<double>.Available(cpu, cpuTime, TelemetrySources.CpuPackageTemperature),
            TelemetryMetric<double>.Available(gpu, gpuTime, TelemetrySources.GpuTemperature));
}
