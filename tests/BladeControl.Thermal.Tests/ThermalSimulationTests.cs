using BladeControl.Thermal;

namespace BladeControl.Thermal.Tests;

[TestClass]
public sealed class ThermalSimulationTests
{
    [TestMethod]
    public void CsvReplayProducesHardwareFreeDecisions()
    {
        const string csv = """
            timestamp,cpu_temp,gpu_temp
            2026-08-16T12:00:00Z,50,45
            2026-08-16T12:00:01Z,60,55
            """;

        IReadOnlyList<TelemetryTraceSample> samples = ThermalSimulator.ParseCsv(csv);
        IReadOnlyList<ThermalSimulationStep> output = ThermalSimulator.Simulate(
            BuiltInThermalProfiles.Default,
            samples);

        Assert.AreEqual(2, output.Count);
        Assert.AreEqual(3000, output[0].Decision.EffectiveTarget.Value);
        Assert.AreEqual(3300, output[1].Decision.EffectiveTarget.Value);
    }

    [TestMethod]
    public void NonIncreasingTraceTimestampsAreRejected()
    {
        const string csv = """
            timestamp,cpu_temp,gpu_temp
            2026-08-16T12:00:00Z,50,45
            2026-08-16T12:00:00Z,60,55
            """;

        Assert.ThrowsException<FormatException>(() => ThermalSimulator.ParseCsv(csv));
    }

    [TestMethod]
    public void ProfileRoundTripsThroughTypedJson()
    {
        string json = ThermalProfileSerializer.Serialize(BuiltInThermalProfiles.Default);

        ThermalProfile profile = ThermalProfileSerializer.Parse(json);

        Assert.AreEqual("default", profile.Name);
        Assert.AreEqual(5, profile.CpuCurve.Points.Count);
        Assert.AreEqual(5, profile.GpuCurve.Points.Count);
    }
}
