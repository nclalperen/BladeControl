using BladeControl.Razer;
using BladeControl.Telemetry;
using BladeControl.Thermal;

namespace BladeControl.Thermal.Tests;

/// <summary>
/// The graded CPU thermal-safety model that replaced "one sample at 90 C ends the session".
/// </summary>
/// <remarks>
/// <para>Field incident this exists to prevent: under a light desktop workload the CPU
/// produced 65, 64, 65, 77, 86, 65, 70, 90 C. The single 90 C boost spike handed control back
/// to firmware and left the runtime reporting a fault, even though nothing was wrong — the
/// machine was idle and the GPU was at 53 C.</para>
/// <para>The ladder is now: maximum cooling at 90 C, handoff only if heat persists at 95 C or
/// reaches Tjunction at 100 C. Everything here runs on synthetic telemetry; no hardware and
/// no protocol traffic.</para>
/// </remarks>
[TestClass]
public sealed class GradedThermalSafetyTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The idle-desktop trace that caused the incident, replayed sample by sample.</summary>
    [TestMethod]
    public void TheFieldIncidentTraceNoLongerEndsTheThermalSession()
    {
        double[] trace = [65, 64, 65, 77, 86, 65, 70, 90];
        ThermalDecisionEngine engine = NewEngine();

        ThermalDecision? last = null;
        for (int index = 0; index < trace.Length; index++)
        {
            last = engine.Evaluate(At(trace[index], index), Moment(index));
            Assert.IsFalse(
                last.EmergencyAuto,
                $"Sample {index} at {trace[index]} C must not end the session.");
        }

        Assert.IsFalse(engine.IsEmergencyStopped);
        Assert.IsTrue(
            engine.IsCriticalCoolingActive,
            "The closing 90 C sample demands maximum cooling.");
        Assert.AreEqual(FanRpm.MaximumValue, last!.EffectiveTarget.Value);
    }

    // --- Below the critical threshold, nothing changes -------------------------------------

    [TestMethod]
    public void CpuAt89BehavesNormally()
    {
        ThermalDecisionEngine engine = NewEngine();

        ThermalDecision decision = engine.Evaluate(At(89, 0), Moment(0));

        Assert.IsFalse(decision.EmergencyAuto);
        Assert.IsFalse(engine.IsCriticalCoolingActive);
        Assert.AreEqual(
            decision.CpuCurveTarget!.Value.Value,
            decision.EffectiveTarget.Value,
            "Below the critical threshold the curve still governs the target.");
    }

    // --- One 90 C sample: maximum fans, no handoff -----------------------------------------

    [TestMethod]
    public void OneSampleAt90DoesNotHandOffToFirmware()
    {
        ThermalDecisionEngine engine = NewEngine();

        ThermalDecision decision = engine.Evaluate(At(90, 0), Moment(0));

        Assert.IsFalse(decision.EmergencyAuto);
        Assert.IsFalse(engine.IsEmergencyStopped);
    }

    [TestMethod]
    public void OneSampleAt90ImmediatelyRequestsMaximumFanTarget()
    {
        ThermalDecisionEngine engine = NewEngine();

        ThermalDecision decision = engine.Evaluate(At(90, 0), Moment(0));

        Assert.IsTrue(engine.IsCriticalCoolingActive);
        Assert.AreEqual(FanRpm.MaximumValue, decision.EffectiveTarget.Value);
        Assert.IsTrue(decision.ShouldWrite, "The maximum target must be written, not deferred.");
        StringAssert.Contains(decision.Reason, "Critical cooling override");
    }

    // --- Write throttling is outranked -----------------------------------------------------

    [TestMethod]
    public void CriticalOverrideBypassesTheOneSecondWriteInterval()
    {
        ThermalDecisionEngine engine = NewEngine();

        // Establish a written target, then go critical well inside the one-second interval.
        ThermalDecision warm = engine.Evaluate(At(70, 0), Moment(0));
        Assert.IsTrue(warm.ShouldWrite);
        engine.RecordSuccessfulWrite(warm);

        ThermalDecision critical = engine.Evaluate(
            At(90, 0, Start.AddMilliseconds(100)),
            Start.AddMilliseconds(100));

        Assert.IsTrue(
            critical.ShouldWrite,
            "An upward safety response must not wait for the write-coalescing interval.");
        Assert.AreEqual(FanRpm.MaximumValue, critical.EffectiveTarget.Value);
    }

    // --- The curve may not lower the target while the override holds -----------------------

    [TestMethod]
    public void CriticalOverrideBlocksTheCurveFromLoweringTheTarget()
    {
        ThermalDecisionEngine engine = NewEngine();
        ThermalDecision critical = engine.Evaluate(At(90, 0), Moment(0));
        engine.RecordSuccessfulWrite(critical);

        // 87 C is below the entry threshold but above recovery: the curve would ask for far
        // less, and must not get it.
        ThermalDecision held = engine.Evaluate(At(87, 1), Moment(1));

        Assert.IsTrue(held.CpuCurveTarget!.Value.Value < FanRpm.MaximumValue);
        Assert.AreEqual(FanRpm.MaximumValue, held.EffectiveTarget.Value);
        Assert.IsTrue(engine.IsCriticalCoolingActive);
    }

    // --- The override holds, and the recovery band prevents chatter ------------------------

    [TestMethod]
    public void CriticalOverrideRemainsActiveWhileCpuStaysAboveRecovery()
    {
        ThermalDecisionEngine engine = NewEngine();
        engine.Evaluate(At(90, 0), Moment(0));

        int index = 1;
        foreach (double celsius in new[] { 89.0, 88.0, 87.0, 86.0, 85.5 })
        {
            engine.Evaluate(At(celsius, index), Moment(index));
            index++;
            Assert.IsTrue(
                engine.IsCriticalCoolingActive,
                $"{celsius} C is above the recovery threshold; cooling must be held.");
        }
    }

    [TestMethod]
    public void OscillatingAcross90DoesNotChatterTheOverride()
    {
        ThermalDecisionEngine engine = NewEngine();
        int index = 0;

        // The pattern a boost-happy CPU produces around the threshold. Without recovery
        // hysteresis this would toggle maximum fans on and off repeatedly.
        foreach (double celsius in new[] { 90.0, 89.0, 90.0, 88.0, 90.0, 89.0 })
        {
            ThermalDecision decision = engine.Evaluate(At(celsius, index), Moment(index));
            index++;
            Assert.IsTrue(engine.IsCriticalCoolingActive);
            Assert.AreEqual(FanRpm.MaximumValue, decision.EffectiveTarget.Value);
        }
    }

    // --- Recovery eventually returns control to the curve ----------------------------------

    [TestMethod]
    public void SustainedCoolingBelowRecoveryReturnsControlToTheCurve()
    {
        ThermalDecisionEngine engine = NewEngine();
        ThermalDecision critical = engine.Evaluate(At(90, 0), Moment(0));
        engine.RecordSuccessfulWrite(critical);

        // Two samples at recovery are not enough; the third releases the override.
        engine.Evaluate(At(85, 1), Moment(1));
        Assert.IsTrue(engine.IsCriticalCoolingActive);
        engine.Evaluate(At(84, 2), Moment(2));
        Assert.IsTrue(engine.IsCriticalCoolingActive);
        engine.Evaluate(At(83, 3), Moment(3));
        Assert.IsFalse(
            engine.IsCriticalCoolingActive,
            "Recovery qualified; the curve governs again.");

        // Control is back with the curve, which means the ordinary downward machinery applies
        // again: three qualifying samples and then the 300 RPM/s slew limit. That is the point
        // — lowering is once more gradual and hysteresis-governed rather than instant.
        ThermalDecision normal = engine.Evaluate(At(60, 4), Moment(4));
        Assert.AreEqual(
            FanRpm.MaximumValue,
            normal.EffectiveTarget.Value,
            "The first cool sample only starts downward qualification.");

        for (int index = 5; index <= 10; index++)
        {
            normal = engine.Evaluate(At(60, index), Moment(index));
        }

        Assert.IsFalse(engine.IsCriticalCoolingActive);
        Assert.IsTrue(
            normal.EffectiveTarget.Value < FanRpm.MaximumValue,
            "Once qualified, the curve lowers the target through the normal slew limit.");
        StringAssert.Contains(normal.Reason, "RPM/s");
    }

    [TestMethod]
    public void ALoneRecoverySampleDoesNotReleaseTheOverride()
    {
        ThermalDecisionEngine engine = NewEngine();
        engine.Evaluate(At(90, 0), Moment(0));

        engine.Evaluate(At(84, 1), Moment(1));
        engine.Evaluate(At(91, 2), Moment(2));
        engine.Evaluate(At(84, 3), Moment(3));

        Assert.IsTrue(
            engine.IsCriticalCoolingActive,
            "A hot sample restarts recovery qualification from zero.");
    }

    // --- Sustained emergency qualification -------------------------------------------------

    [TestMethod]
    public void OneSampleAt95DoesNotHandOff()
    {
        ThermalDecisionEngine engine = NewEngine();

        ThermalDecision decision = engine.Evaluate(At(95, 0), Moment(0));

        Assert.IsFalse(decision.EmergencyAuto);
        Assert.IsFalse(engine.IsEmergencyStopped);
        Assert.AreEqual(
            FanRpm.MaximumValue,
            decision.EffectiveTarget.Value,
            "It is still critical, so maximum cooling applies while qualification runs.");
    }

    [TestMethod]
    public void ThreeConsecutiveSamplesAt95HandOff()
    {
        ThermalDecisionEngine engine = NewEngine();

        Assert.IsFalse(engine.Evaluate(At(95, 0), Moment(0)).EmergencyAuto);
        Assert.IsFalse(engine.Evaluate(At(96, 1), Moment(1)).EmergencyAuto);
        ThermalDecision third = engine.Evaluate(At(95, 2), Moment(2));

        Assert.IsTrue(third.EmergencyAuto, "Heat that persists near Tjunction must hand off.");
        Assert.IsTrue(engine.IsEmergencyStopped);
        StringAssert.Contains(third.Reason, "consecutive samples");
    }

    [TestMethod]
    public void ALowerInterveningSampleResetsSustainedQualification()
    {
        ThermalDecisionEngine engine = NewEngine();

        engine.Evaluate(At(95, 0), Moment(0));
        engine.Evaluate(At(96, 1), Moment(1));

        // Below the sustained threshold: the heat did not persist.
        engine.Evaluate(At(88, 2), Moment(2));

        Assert.IsFalse(engine.Evaluate(At(95, 3), Moment(3)).EmergencyAuto);
        Assert.IsFalse(engine.Evaluate(At(95, 4), Moment(4)).EmergencyAuto);
        Assert.IsFalse(
            engine.IsEmergencyStopped,
            "Qualification restarts from zero rather than accumulating across a cool spell.");
    }

    // --- Tjunction hands off from one sample -----------------------------------------------

    [TestMethod]
    public void OneSampleAt100HandsOffImmediately()
    {
        ThermalDecisionEngine engine = NewEngine();

        ThermalDecision decision = engine.Evaluate(At(100, 0), Moment(0));

        Assert.IsTrue(decision.EmergencyAuto);
        Assert.IsTrue(engine.IsEmergencyStopped);
        StringAssert.Contains(decision.Reason, "immediate limit");
    }

    // --- Threshold classification ----------------------------------------------------------

    [DataTestMethod]
    [DataRow(60.0, CpuThermalSeverity.Normal)]
    [DataRow(89.9, CpuThermalSeverity.Normal)]
    [DataRow(90.0, CpuThermalSeverity.CriticalCooling)]
    [DataRow(94.9, CpuThermalSeverity.CriticalCooling)]
    [DataRow(95.0, CpuThermalSeverity.SustainedEmergency)]
    [DataRow(99.9, CpuThermalSeverity.SustainedEmergency)]
    [DataRow(100.0, CpuThermalSeverity.ImmediateEmergency)]
    [DataRow(110.0, CpuThermalSeverity.ImmediateEmergency)]
    public void SeverityLadderClassifiesEachBand(double celsius, CpuThermalSeverity expected) =>
        Assert.AreEqual(expected, TelemetryHealthEvaluator.ClassifyCpuThermalSeverity(celsius));

    /// <summary>
    /// Entering Manual while already critical is still refused. The graded ladder governs a
    /// session that is already running; it does not relax the qualification gate.
    /// </summary>
    [TestMethod]
    public void PreflightQualificationStillRefusesToStartAtCriticalTemperature()
    {
        TelemetryHealth health = TelemetryHealthEvaluator.EvaluateRequiredCpuTemperature(
            TelemetryMetric<double>.Available(90, Start, TelemetrySources.CpuPackageTemperature),
            Start);

        Assert.AreEqual(TelemetryHealthKind.Critical, health.Kind);
        Assert.IsTrue(health.RequiresImmediateAuto);
    }

    // --- Untouched emergency paths ---------------------------------------------------------

    /// <summary>
    /// GPU heat is now governed by the device's own limits, covered in
    /// GradedGpuSafetyTests. Here the point is only that the CPU ladder does not depend on it:
    /// with no GPU limits configured, GPU temperature cannot end the session.
    /// </summary>
    [TestMethod]
    public void CpuLadderIsIndependentOfGpuHeatWhenNoGpuLimitsAreConfigured()
    {
        ThermalDecisionEngine engine = NewEngine();

        ThermalDecision decision = engine.Evaluate(Snapshot(50, 85, Start), Start);

        Assert.IsFalse(decision.EmergencyAuto);
        Assert.IsFalse(engine.IsCriticalCoolingActive);
    }

    [TestMethod]
    public void InvalidCpuTelemetryStillHandsOff()
    {
        ThermalDecisionEngine engine = NewEngine();
        var snapshot = new TelemetrySnapshot(
            Start,
            TelemetryMetric<double>.Available(
                TelemetryHealthEvaluator.MaximumPlausibleTemperatureCelsius + 1,
                Start,
                TelemetrySources.CpuPackageTemperature),
            TelemetryMetric<double>.Available(40, Start, TelemetrySources.GpuTemperature));

        // Two invalid samples, per the unchanged invalid-sample policy.
        engine.Evaluate(snapshot, Start);
        ThermalDecision decision = engine.Evaluate(snapshot, Start.AddMilliseconds(500));

        Assert.IsTrue(decision.EmergencyAuto);
        Assert.AreEqual(TelemetryHealthKind.Invalid, decision.Health.Kind);
    }

    [TestMethod]
    public void StaleCpuTelemetryStillHandsOffImmediately()
    {
        ThermalDecisionEngine engine = NewEngine();
        DateTimeOffset now = Start + TelemetryHealthEvaluator.MaximumRequiredSampleAge +
            TimeSpan.FromSeconds(1);

        ThermalDecision decision = engine.Evaluate(Snapshot(70, 40, Start), now);

        Assert.IsTrue(decision.EmergencyAuto);
        Assert.AreEqual(TelemetryHealthKind.Stale, decision.Health.Kind);
    }

    private static ThermalDecisionEngine NewEngine()
    {
        var engine = new ThermalDecisionEngine(Profile());
        engine.InitializeBaseline(Start - TimeSpan.FromSeconds(1));
        return engine;
    }

    private static ThermalProfile Profile() => new(
        "graded-safety-test",
        new ThermalCurve([Point(40, 3000), Point(80, 4000)]),
        new ThermalCurve([Point(40, 3000), Point(80, 4000)]));

    private static ThermalCurvePoint Point(double temperature, int rpm) =>
        new(temperature, new FanRpm(rpm));

    /// <summary>Sample index to wall clock at the 500 ms control cadence.</summary>
    private static DateTimeOffset Moment(int index) => Start.AddMilliseconds(500 * index);

    private static TelemetrySnapshot At(double cpu, int index, DateTimeOffset? timestamp = null) =>
        Snapshot(cpu, 50, timestamp ?? Moment(index));

    private static TelemetrySnapshot Snapshot(double cpu, double gpu, DateTimeOffset timestamp) =>
        new(
            timestamp,
            TelemetryMetric<double>.Available(cpu, timestamp, TelemetrySources.CpuPackageTemperature),
            TelemetryMetric<double>.Available(gpu, timestamp, TelemetrySources.GpuTemperature));
}
