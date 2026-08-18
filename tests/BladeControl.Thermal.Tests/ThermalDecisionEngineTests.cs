using BladeControl.Razer;
using BladeControl.Telemetry;
using BladeControl.Thermal;

namespace BladeControl.Thermal.Tests;

[TestClass]
public sealed class ThermalDecisionEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void CpuDemandDominates()
    {
        ThermalDecision decision = NewEngine().Evaluate(Snapshot(70, 50, Start), Start);

        Assert.AreEqual(ThermalDemandSource.Cpu, decision.DemandSource);
        Assert.IsTrue(decision.CpuCurveTarget!.Value.Value > decision.GpuCurveTarget!.Value.Value);
    }

    [TestMethod]
    public void GpuDemandDominates()
    {
        ThermalDecision decision = NewEngine().Evaluate(Snapshot(50, 70, Start), Start);

        Assert.AreEqual(ThermalDemandSource.Gpu, decision.DemandSource);
    }

    [TestMethod]
    public void EqualDemandIsPreserved()
    {
        ThermalDecision decision = NewEngine().Evaluate(Snapshot(60, 60, Start), Start);

        Assert.AreEqual(ThermalDemandSource.Equal, decision.DemandSource);
        Assert.AreEqual(decision.CpuCurveTarget, decision.GpuCurveTarget);
    }

    [TestMethod]
    public void FanIncreaseIsImmediate()
    {
        ThermalDecision decision = NewEngine().Evaluate(Snapshot(70, 50, Start), Start);

        Assert.AreEqual(4500, decision.EffectiveTarget.Value);
        Assert.IsTrue(decision.ShouldWrite);
    }

    [TestMethod]
    public void FanDecreaseRequiresThreeDegreesCooling()
    {
        ThermalDecisionEngine engine = NewEngine();
        ThermalDecision high = engine.Evaluate(Snapshot(70, 40, Start), Start);
        engine.RecordSuccessfulWrite(high);

        ThermalDecision held = engine.Evaluate(
            Snapshot(68, 40, Start + TimeSpan.FromSeconds(1)),
            Start + TimeSpan.FromSeconds(1));

        Assert.AreEqual(4500, held.EffectiveTarget.Value);
        Assert.IsFalse(held.ShouldWrite);
        StringAssert.Contains(held.Reason, "hysteresis");
    }

    [TestMethod]
    public void FanDecreaseRequiresThreeConsecutiveSamples()
    {
        ThermalDecisionEngine engine = NewEngine();
        ThermalDecision high = engine.Evaluate(Snapshot(70, 40, Start), Start);
        engine.RecordSuccessfulWrite(high);

        ThermalDecision first = engine.Evaluate(Snapshot(60, 40, Start.AddSeconds(1)), Start.AddSeconds(1));
        ThermalDecision second = engine.Evaluate(Snapshot(60, 40, Start.AddSeconds(2)), Start.AddSeconds(2));
        ThermalDecision third = engine.Evaluate(Snapshot(60, 40, Start.AddSeconds(3)), Start.AddSeconds(3));

        Assert.AreEqual(4500, first.EffectiveTarget.Value);
        Assert.AreEqual(4500, second.EffectiveTarget.Value);
        Assert.AreEqual(4200, third.EffectiveTarget.Value);
    }

    [TestMethod]
    public void DownwardRampIsLimitedTo300RpmPerSecond()
    {
        ThermalDecisionEngine engine = NewEngine();
        ThermalDecision high = engine.Evaluate(Snapshot(80, 40, Start), Start);
        engine.RecordSuccessfulWrite(high);
        _ = engine.Evaluate(Snapshot(50, 40, Start.AddSeconds(1)), Start.AddSeconds(1));
        _ = engine.Evaluate(Snapshot(50, 40, Start.AddSeconds(2)), Start.AddSeconds(2));

        ThermalDecision lower = engine.Evaluate(
            Snapshot(50, 40, Start.AddSeconds(3)),
            Start.AddSeconds(3));

        Assert.AreEqual(4700, lower.EffectiveTarget.Value);
    }

    [TestMethod]
    public void UnchangedTargetIsCoalesced()
    {
        ThermalDecisionEngine engine = NewEngine();
        ThermalDecision first = engine.Evaluate(Snapshot(60, 60, Start), Start);
        engine.RecordSuccessfulWrite(first);

        ThermalDecision unchanged = engine.Evaluate(
            Snapshot(60, 60, Start.AddSeconds(1)),
            Start.AddSeconds(1));

        Assert.IsFalse(unchanged.ShouldWrite);
        StringAssert.Contains(unchanged.Reason, "coalesced");
    }

    /// <summary>
    /// Superseded policy. A single 90 C sample used to end the thermal session; a light
    /// desktop boost spike could therefore abandon control. It now demands maximum cooling
    /// and keeps control. Handoff at 90 C is covered by the graded-ladder tests.
    /// </summary>
    [TestMethod]
    public void CpuAt90DemandsMaximumCoolingInsteadOfHandingOff()
    {
        ThermalDecision decision = NewEngine().Evaluate(Snapshot(90, 40, Start), Start);

        Assert.IsFalse(decision.EmergencyAuto);
        Assert.AreEqual(FanRpm.MaximumValue, decision.EffectiveTarget.Value);
    }

    [TestMethod]
    public void GpuAt80TriggersImmediateAuto()
    {
        ThermalDecision decision = NewEngine().Evaluate(Snapshot(50, 80, Start), Start);

        Assert.IsTrue(decision.EmergencyAuto);
    }

    [TestMethod]
    public void StaleCpuTriggersImmediateAuto()
    {
        ThermalDecision decision = NewEngine().Evaluate(
            Snapshot(50, 40, Start - TimeSpan.FromSeconds(3)),
            Start);

        Assert.IsTrue(decision.EmergencyAuto);
    }

    [TestMethod]
    public void StaleGpuTriggersImmediateAuto()
    {
        TelemetrySnapshot snapshot = Snapshot(50, 40, Start);
        snapshot = new TelemetrySnapshot(
            Start,
            snapshot.CpuPackageTemperatureCelsius,
            TelemetryMetric<double>.Available(
                40,
                Start - TimeSpan.FromSeconds(3),
                TelemetrySources.GpuTemperature));

        Assert.IsTrue(NewEngine().Evaluate(snapshot, Start).EmergencyAuto);
    }

    [TestMethod]
    public void MissingCpuTriggersAutoAfterSecondConsecutiveSample()
    {
        ThermalDecisionEngine engine = NewEngine();
        TelemetrySnapshot missing = MissingCpu(Start);

        Assert.IsFalse(engine.Evaluate(missing, Start).EmergencyAuto);
        Assert.IsTrue(engine.Evaluate(missing, Start.AddMilliseconds(500)).EmergencyAuto);
    }

    [TestMethod]
    public void MissingGpuTriggersAutoAfterSecondConsecutiveSample()
    {
        ThermalDecisionEngine engine = NewEngine();
        var missing = new TelemetrySnapshot(
            Start,
            TelemetryMetric<double>.Available(
                50,
                Start,
                TelemetrySources.CpuPackageTemperature),
            TelemetryMetric<double>.Missing(
                Start,
                TelemetrySources.GpuTemperature,
                "missing"));

        Assert.IsFalse(engine.Evaluate(missing, Start).EmergencyAuto);
        Assert.IsTrue(engine.Evaluate(missing, Start.AddMilliseconds(500)).EmergencyAuto);
    }

    [TestMethod]
    public void ProviderFailureTriggersAutoAfterSecondConsecutiveFailure()
    {
        ThermalDecisionEngine engine = NewEngine();

        Assert.IsFalse(engine.EvaluateProviderFailure("one", Start).EmergencyAuto);
        Assert.IsTrue(engine.EvaluateProviderFailure("two", Start.AddMilliseconds(500)).EmergencyAuto);
    }

    [TestMethod]
    public void EmergencyStateCannotReenter()
    {
        ThermalDecisionEngine engine = NewEngine();

        // 100 C rather than 90: reaching Tjunction is what hands off from a single sample now.
        _ = engine.Evaluate(Snapshot(100, 40, Start), Start);

        Assert.ThrowsException<InvalidOperationException>(() =>
            engine.Evaluate(Snapshot(50, 40, Start.AddSeconds(1)), Start.AddSeconds(1)));
    }

    private static ThermalDecisionEngine NewEngine()
    {
        var engine = new ThermalDecisionEngine(Profile());
        engine.InitializeBaseline(Start - TimeSpan.FromSeconds(1));
        return engine;
    }

    private static ThermalProfile Profile() => new(
        "test",
        new ThermalCurve([Point(40, 3000), Point(80, 5000)]),
        new ThermalCurve([Point(40, 3000), Point(80, 5000)]));

    private static ThermalCurvePoint Point(double temperature, int rpm) =>
        new(temperature, new FanRpm(rpm));

    private static TelemetrySnapshot Snapshot(
        double cpu,
        double gpu,
        DateTimeOffset timestamp) => new(
            timestamp,
            TelemetryMetric<double>.Available(cpu, timestamp, TelemetrySources.CpuPackageTemperature),
            TelemetryMetric<double>.Available(gpu, timestamp, TelemetrySources.GpuTemperature));

    private static TelemetrySnapshot MissingCpu(DateTimeOffset timestamp) => new(
        timestamp,
        TelemetryMetric<double>.Missing(
            timestamp,
            TelemetrySources.CpuPackageTemperature,
            "missing"),
        TelemetryMetric<double>.Available(40, timestamp, TelemetrySources.GpuTemperature));
}
