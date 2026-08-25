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

    /// <summary>
    /// A blank telemetry trace is malformed input, like every other malformed trace.
    /// </summary>
    /// <remarks>
    /// The third place the same shape appeared: emptiness guarded with
    /// <see cref="ArgumentException"/> while every other bad trace produced a
    /// <see cref="FormatException"/> naming what was wrong. An empty file printed "Thermal
    /// simulation failed: The value cannot be an empty string or composed entirely of
    /// whitespace", which names a parameter the person running the command has never seen.
    /// The first of the three cost the runtime service its life to a single blank line over
    /// IPC; these two cost only a confusing message, and are fixed because the shape is what
    /// keeps recurring.
    /// </remarks>
    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void BlankTelemetryTraceIsMalformedInputRatherThanAnArgumentFault(string csv)
    {
        Assert.ThrowsException<FormatException>(
            () => ThermalSimulator.ParseCsv(csv),
            "A blank trace must be reported like every other malformed trace.");
    }

    /// <summary>
    /// A blank profile document is malformed input, reported the same way as any other
    /// malformed input.
    /// </summary>
    /// <remarks>
    /// <para>This threw <see cref="ArgumentException"/>, so a user pointing
    /// <c>thermal curve validate</c> at an empty file was shown a raw "ArgumentException: The
    /// value cannot be an empty string or composed entirely of whitespace" while every other
    /// bad file produced a sentence about their curve. The line directly below the guard
    /// already reported a null-deserialising document as
    /// <see cref="FormatException"/>("The thermal profile document is empty.") — one concept
    /// with two exception types.</para>
    /// <para>The same shape, in a different file, cost the runtime service its life to a single
    /// blank line over IPC. This instance is reachable only from the CLI, so it was cosmetic;
    /// it is fixed because the pattern is what mattered, not the blast radius of this
    /// particular copy.</para>
    /// </remarks>
    [DataTestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t\n  ")]
    public void BlankProfileDocumentIsMalformedInputRatherThanAnArgumentFault(string json)
    {
        Assert.ThrowsException<FormatException>(
            () => ThermalProfileSerializer.Parse(json),
            "A blank document must be reported like every other malformed document.");
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
