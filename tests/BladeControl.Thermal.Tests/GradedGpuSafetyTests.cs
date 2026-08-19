using BladeControl.Razer;
using BladeControl.Telemetry;
using BladeControl.Thermal;

namespace BladeControl.Thermal.Tests;

/// <summary>
/// GPU thermal safety built from limits the device reports about itself, replacing a
/// hard-coded 80 C handoff.
/// </summary>
/// <remarks>
/// <para>Two live readings from the reference RTX 4090 Laptop GPU (driver 610.88), taken at
/// different operating points:</para>
/// <code>
///                                                  at 66 C     at 44 C
/// GPU Current T.Limit Temp        (live margin)      +9 C       +31 C
/// GPU Max Operating T.Limit Spec  (field 196)         0 C  ->  75 C  (both)
/// GPU Slowdown T.Limit Spec       (field 194)        -2 C  ->  77 C  (both)
/// GPU Shutdown T.Limit Spec       (field 193)        -5 C  ->  80 C  (both)
/// </code>
/// <para>The specifications are static offsets; the margin is live. Anchoring the offsets with
/// the margin gives the same absolute limits at both operating points, which is what makes them
/// safe to discover once and cache.</para>
/// <para>So the old fixed 80 C threshold was the temperature at which this GPU shuts itself
/// down. Handing off there meant never cooling first and never leaving margin — exactly the
/// live handoff that prompted this work.</para>
/// <para>All synthetic. No NVML call, no hardware, no protocol traffic.</para>
/// </remarks>
[TestClass]
public sealed class GradedGpuSafetyTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    // --- T.Limit conversion ------------------------------------------------------------------

    /// <summary>Both live reference-machine readings, converted.</summary>
    /// <remarks>
    /// Two operating points, one expected answer. A conversion anchored on the wrong quantity
    /// can match at a single point by coincidence; it cannot match at two.
    /// </remarks>
    [DataTestMethod]
    [DataRow(66.0, 9.0, "captured at 66 C")]
    [DataRow(44.0, 31.0, "captured at 44 C, driver 610.88")]
    public void ReferenceMachineSpecificationsConvertToAbsoluteTemperatures(
        double currentTemperature,
        double liveMargin,
        string capture)
    {
        bool converted = GpuThermalLimits.TryFromTemperatureLimitSpecifications(
            currentTemperatureCelsius: currentTemperature,
            liveMarginCelsius: liveMargin,
            maxOperatingSpecification: 0,
            slowdownSpecification: -2,
            shutdownSpecification: -5,
            out GpuThermalLimits? limits,
            out string? rejection);

        Assert.IsTrue(converted, rejection);
        Assert.AreEqual(75, limits!.MaxOperatingCelsius, capture);
        Assert.AreEqual(77, limits.HardwareSlowdownCelsius, capture);
        Assert.AreEqual(80, limits.HardwareShutdownCelsius, capture);
        Assert.AreEqual(GpuThermalLimitSource.NvmlTemperatureLimitSpecifications, limits.Source);
    }

    /// <summary>
    /// The live margin is the anchor, so it must be treated as such: holding it constant while
    /// the temperature moves would slide every derived limit with the reading.
    /// </summary>
    [TestMethod]
    public void TheMarginIsAnAnchorNotAConstant()
    {
        _ = GpuThermalLimits.TryFromTemperatureLimitSpecifications(
            currentTemperatureCelsius: 44,
            liveMarginCelsius: 9,
            maxOperatingSpecification: 0,
            slowdownSpecification: -2,
            shutdownSpecification: -5,
            out GpuThermalLimits? stale,
            out _);

        Assert.AreEqual(
            53,
            stale!.MaxOperatingCelsius,
            "A margin from a different operating point yields a different, wrong answer — " +
            "which is why the margin is read together with the temperature, never cached.");
    }

    [TestMethod]
    public void DerivedThresholdsMatchTheDocumentedPolicyOnTheReferencePart()
    {
        GpuThermalLimits limits = Reference();

        Assert.AreEqual(75, limits.CriticalCoolingCelsius, "max operating");
        Assert.AreEqual(72, limits.CriticalRecoveryCelsius, "3 C below max operating");
        Assert.AreEqual(77, limits.SustainedEmergencyCelsius, "hardware slowdown");
        Assert.AreEqual(79, limits.ImmediateEmergencyCelsius, "1 C below hardware shutdown");
    }

    /// <summary>The pre-shutdown margin is ours, and is not presented as an NVML reading.</summary>
    [TestMethod]
    public void PreShutdownMarginIsBladeControlPolicyNotADeviceSpecification()
    {
        Assert.AreEqual(1, GpuThermalLimits.PreShutdownPolicyMarginCelsius);
        Assert.AreEqual(
            Reference().HardwareShutdownCelsius - GpuThermalLimits.PreShutdownPolicyMarginCelsius,
            Reference().ImmediateEmergencyCelsius);

        // Provenance describes the device-reported limits only.
        StringAssert.Contains(Reference().Describe(), "NVML device thermal limits");
        StringAssert.Contains(Reference().Describe(), "hardware shutdown 80 C");
    }

    // --- Malformed data is refused, never guessed ---------------------------------------------

    [DataTestMethod]
    [DataRow(80.0, 77.0, 75.0, "reversed ordering")]
    [DataRow(75.0, 80.0, 77.0, "shutdown below slowdown")]
    [DataRow(75.0, 75.0, 75.0, "no separation at all")]
    [DataRow(10.0, 12.0, 15.0, "implausibly low for a discrete GPU")]
    [DataRow(75.0, 77.0, 140.0, "implausibly wide spread")]
    public void ImpossibleThresholdOrderingIsRejected(
        double maxOperating,
        double slowdown,
        double shutdown,
        string because)
    {
        bool created = GpuThermalLimits.TryCreate(
            maxOperating,
            slowdown,
            shutdown,
            GpuThermalLimitSource.NvmlTemperatureLimitSpecifications,
            out GpuThermalLimits? limits,
            out string? rejection);

        Assert.IsFalse(created, because);
        Assert.IsNull(limits);
        Assert.IsFalse(string.IsNullOrWhiteSpace(rejection), "A rejection must say why.");
    }

    [TestMethod]
    public void NonFiniteInputsAreRejected()
    {
        Assert.IsFalse(GpuThermalLimits.TryFromTemperatureLimitSpecifications(
            66,
            double.NaN,
            0,
            -2,
            -5,
            out _,
            out _));
    }

    /// <summary>
    /// A specification of -5 means five degrees <i>above</i> the reference point. Reading it as
    /// an absolute temperature would put the shutdown limit below freezing.
    /// </summary>
    [TestMethod]
    public void NegativeSpecificationsRaiseLimitsRatherThanLoweringThem()
    {
        _ = GpuThermalLimits.TryFromTemperatureLimitSpecifications(
            currentTemperatureCelsius: 44,
            liveMarginCelsius: 31,
            maxOperatingSpecification: 0,
            slowdownSpecification: -2,
            shutdownSpecification: -5,
            out GpuThermalLimits? limits,
            out _);

        Assert.IsTrue(limits!.HardwareShutdownCelsius > limits.MaxOperatingCelsius);
        Assert.IsTrue(limits.HardwareSlowdownCelsius > limits.MaxOperatingCelsius);
    }

    // --- The anchor needs an independent witness ----------------------------------------------

    /// <summary>
    /// Agreement between the relative derivation and the device's own absolute thresholds is
    /// what qualifies a limit set.
    /// </summary>
    [TestMethod]
    public void AgreeingSourcesProduceCorroboratedLimits()
    {
        bool created = GpuThermalLimits.TryFromCorroboratedNvmlSources(
            currentTemperatureCelsius: 44,
            liveMarginCelsius: 31,
            maxOperatingSpecification: 0,
            slowdownSpecification: -2,
            shutdownSpecification: -5,
            legacyMaxOperatingCelsius: 75,
            legacySlowdownCelsius: 77,
            legacyShutdownCelsius: 80,
            out GpuThermalLimits? limits,
            out string? rejection);

        Assert.IsTrue(created, rejection);
        Assert.AreEqual(75, limits!.MaxOperatingCelsius);
        Assert.AreEqual(77, limits.HardwareSlowdownCelsius);
        Assert.AreEqual(80, limits.HardwareShutdownCelsius);
        Assert.AreEqual(
            GpuThermalLimitSource.NvmlTemperatureLimitSpecificationsCorroborated,
            limits.Source);
    }

    /// <summary>
    /// The counterexample that makes the cross-check necessary. A device measuring its margin
    /// to the slowdown threshold, exactly as the NVML documentation describes, derives
    /// 77/79/82 from the same specifications.
    /// </summary>
    /// <remarks>
    /// Every threshold is two degrees hot. On a part that shuts down at 80 C the pre-shutdown
    /// handoff would sit at 81 C and never fire — the graded ladder would be silently disabled
    /// at exactly the point it exists to act.
    /// </remarks>
    [TestMethod]
    public void SlowdownAnchoredMarginIsRejectedAgainstAbsoluteThresholds()
    {
        bool created = GpuThermalLimits.TryFromCorroboratedNvmlSources(
            currentTemperatureCelsius: 44,
            liveMarginCelsius: 33,
            maxOperatingSpecification: 0,
            slowdownSpecification: -2,
            shutdownSpecification: -5,
            legacyMaxOperatingCelsius: 75,
            legacySlowdownCelsius: 77,
            legacyShutdownCelsius: 80,
            out GpuThermalLimits? limits,
            out string? rejection);

        Assert.IsFalse(created);
        Assert.IsNull(limits);
        StringAssert.Contains(rejection!, "77 C");
        StringAssert.Contains(rejection!, "75 C");
    }

    /// <summary>
    /// The heart of the matter: the shifted set is perfectly well formed. Ordering and
    /// plausibility accept it, so they can never be the thing that catches a wrong anchor.
    /// </summary>
    [TestMethod]
    public void OrderingAndPlausibilityCannotDetectAShiftedAnchor()
    {
        bool wellFormed = GpuThermalLimits.TryCreate(
            77,
            79,
            82,
            GpuThermalLimitSource.NvmlTemperatureLimitSpecifications,
            out GpuThermalLimits? shifted,
            out _);

        Assert.IsTrue(
            wellFormed,
            "77/79/82 is ordered and plausible; validation alone has no objection to it.");
        Assert.AreEqual(81, shifted!.ImmediateEmergencyCelsius);
        Assert.IsTrue(
            shifted.ImmediateEmergencyCelsius > 80,
            "Which is why it is dangerous: the handoff would sit above the real 80 C " +
            "shutdown point and never fire.");

        // Only the independent absolute measurement rejects it.
        Assert.IsFalse(GpuThermalLimits.TryFromCorroboratedNvmlSources(
            44,
            33,
            0,
            -2,
            -5,
            75,
            77,
            80,
            out _,
            out _));
    }

    /// <summary>
    /// A uniform shift of a single degree is still refused: the tolerance is zero because both
    /// sides are integral static values, so any disagreement means the two interfaces are
    /// describing different quantities.
    /// </summary>
    [TestMethod]
    public void EvenAOneDegreeDisagreementIsRefused()
    {
        Assert.AreEqual(0, GpuThermalLimits.CorroborationToleranceCelsius);
        Assert.IsFalse(GpuThermalLimits.TryFromCorroboratedNvmlSources(
            44,
            32,
            0,
            -2,
            -5,
            75,
            77,
            80,
            out GpuThermalLimits? limits,
            out _));
        Assert.IsNull(limits);
    }

    [TestMethod]
    public void NonFiniteAbsoluteThresholdsFailClosed()
    {
        Assert.IsFalse(GpuThermalLimits.TryFromCorroboratedNvmlSources(
            44,
            31,
            0,
            -2,
            -5,
            double.NaN,
            77,
            80,
            out GpuThermalLimits? limits,
            out string? rejection));
        Assert.IsNull(limits);
        StringAssert.Contains(rejection!, "corroborated");
    }

    /// <summary>
    /// An uncorroborated derivation is still expressible — the probe prints one — but it names
    /// itself as uncorroborated so nothing can mistake it for a qualified limit set.
    /// </summary>
    [TestMethod]
    public void UncorroboratedLimitsDescribeThemselvesAsSuch()
    {
        _ = GpuThermalLimits.TryFromTemperatureLimitSpecifications(
            44,
            31,
            0,
            -2,
            -5,
            out GpuThermalLimits? derived,
            out _);

        Assert.AreEqual(
            GpuThermalLimitSource.NvmlTemperatureLimitSpecifications,
            derived!.Source);
        StringAssert.Contains(derived.DescribeSource(), "uncorroborated");
    }

    // --- Qualification is per validated GPU thermal signature ---------------------------------

    private const string ReferenceGpuName = "NVIDIA GeForce RTX 4090 Laptop GPU";

    [TestMethod]
    public void ValidatedThermalSignatureQualifies()
    {
        bool created = GpuThermalLimits.TryFromValidatedSignature(
            ReferenceGpuName,
            currentTemperatureCelsius: 47,
            liveMarginCelsius: 28,
            maxOperatingSpecification: 0,
            slowdownSpecification: -2,
            shutdownSpecification: -5,
            out GpuThermalLimits? limits,
            out string? rejection);

        Assert.IsTrue(created, rejection);
        Assert.AreEqual(75, limits!.MaxOperatingCelsius);
        Assert.AreEqual(77, limits.HardwareSlowdownCelsius);
        Assert.AreEqual(80, limits.HardwareShutdownCelsius);
        Assert.AreEqual(
            GpuThermalLimitSource.NvmlTemperatureLimitSpecificationsOnValidatedSignature,
            limits.Source);
    }

    /// <summary>
    /// The slowdown-anchored reading, rejected on the validated configuration. 77/79/82 is
    /// well ordered and plausible; only the match against observed values catches it.
    /// </summary>
    [TestMethod]
    public void SlowdownAnchoredMarginIsRejectedOnAValidatedSignature()
    {
        bool created = GpuThermalLimits.TryFromValidatedSignature(
            ReferenceGpuName,
            currentTemperatureCelsius: 44,
            liveMarginCelsius: 33,
            maxOperatingSpecification: 0,
            slowdownSpecification: -2,
            shutdownSpecification: -5,
            out GpuThermalLimits? limits,
            out string? rejection);

        Assert.IsFalse(created);
        Assert.IsNull(limits);
        StringAssert.Contains(rejection!, "77/79/82");
        StringAssert.Contains(rejection!, "75/77/80");
    }

    /// <summary>
    /// A GPU with no validated signature gets nothing — not a fallback, not the old fixed
    /// 80 C, and not another GPU's numbers.
    /// </summary>
    [DataTestMethod]
    [DataRow("NVIDIA GeForce RTX 5090 Laptop GPU")]
    [DataRow("NVIDIA GeForce RTX 4090")]
    [DataRow("RTX 4090 Laptop GPU")]
    [DataRow("")]
    [DataRow(null)]
    public void UnvalidatedGpuIdentitiesAreRefused(string? deviceName)
    {
        bool created = GpuThermalLimits.TryFromValidatedSignature(
            deviceName,
            currentTemperatureCelsius: 47,
            liveMarginCelsius: 28,
            maxOperatingSpecification: 0,
            slowdownSpecification: -2,
            shutdownSpecification: -5,
            out GpuThermalLimits? limits,
            out string? rejection);

        Assert.IsFalse(created, deviceName ?? "null");
        Assert.IsNull(limits);
        Assert.IsFalse(string.IsNullOrWhiteSpace(rejection));
    }

    /// <summary>Matching is exact: a near-miss on the name is a miss.</summary>
    [TestMethod]
    public void DeviceNameMatchingIsOrdinalAndExact()
    {
        Assert.IsFalse(GpuThermalLimits.TryFromValidatedSignature(
            ReferenceGpuName.ToUpperInvariant(),
            47,
            28,
            0,
            -2,
            -5,
            out _,
            out _));
    }

    /// <summary>
    /// Both halves of the match are load-bearing. The right device reporting different limits
    /// is refused, because its T.Limit data no longer decodes to the validated signature.
    /// </summary>
    [TestMethod]
    public void ValidatedDeviceReportingDifferentLimitsIsRefused()
    {
        // Same name, but the specifications changed under it.
        bool created = GpuThermalLimits.TryFromValidatedSignature(
            ReferenceGpuName,
            currentTemperatureCelsius: 47,
            liveMarginCelsius: 28,
            maxOperatingSpecification: 0,
            slowdownSpecification: -4,
            shutdownSpecification: -8,
            out GpuThermalLimits? limits,
            out string? rejection);

        Assert.IsFalse(created);
        Assert.IsNull(limits);
        StringAssert.Contains(rejection!, "no longer behaving");
    }

    /// <summary>Every live anchor observation on the reference part resolves identically.</summary>
    [DataTestMethod]
    [DataRow(66.0, 9.0)]
    [DataRow(46.0, 29.0)]
    [DataRow(47.0, 28.0)]
    [DataRow(44.0, 31.0)]
    public void EveryObservedOperatingPointQualifies(double core, double margin)
    {
        Assert.IsTrue(GpuThermalLimits.TryFromValidatedSignature(
            ReferenceGpuName,
            core,
            margin,
            0,
            -2,
            -5,
            out GpuThermalLimits? limits,
            out string? rejection), rejection);
        Assert.AreEqual(75, limits!.MaxOperatingCelsius);
    }

    /// <summary>
    /// The evidence names where it was collected. Qualification does not check the machine —
    /// the signature is a GPU identity plus its limits — but a signature whose provenance is
    /// not recorded is a guess with a comment attached.
    /// </summary>
    [TestMethod]
    public void TheValidatedSignatureRecordsHowItWasEstablished()
    {
        ValidatedGpuThermalSignature reference = ValidatedGpuThermalSignatures.Rtx4090Laptop;

        Assert.AreEqual(ReferenceGpuName, reference.DeviceName);
        Assert.AreEqual(75, reference.MaxOperatingCelsius);
        Assert.AreEqual(77, reference.HardwareSlowdownCelsius);
        Assert.AreEqual(80, reference.HardwareShutdownCelsius);
        Assert.IsFalse(string.IsNullOrWhiteSpace(reference.Evidence));
        StringAssert.Contains(reference.Evidence, "610.88", "The driver it was validated on.");
        StringAssert.Contains(reference.Evidence, "RZ09-0483", "And the machine it came from.");
    }

    /// <summary>
    /// The gate is GPU identity plus thermal signature, with no machine-model component. Said
    /// out loud because the type used to be named as though it identified a laptop.
    /// </summary>
    [TestMethod]
    public void QualificationDependsOnGpuIdentityAndSignatureAlone()
    {
        Assert.IsTrue(GpuThermalLimits.TryFromValidatedSignature(
            ReferenceGpuName,
            47,
            28,
            0,
            -2,
            -5,
            out GpuThermalLimits? limits,
            out _));

        // No chassis, firmware or SMBIOS input reaches this decision.
        Assert.AreEqual(
            GpuThermalLimitSource.NvmlTemperatureLimitSpecificationsOnValidatedSignature,
            limits!.Source);
        StringAssert.Contains(limits.DescribeSource(), "validated thermal signature");
    }

    /// <summary>
    /// Nothing generalises to Ada as a family. Adding a signature is a deliberate act with the
    /// hardware in hand, so the list stays short and every entry is answerable for.
    /// </summary>
    [TestMethod]
    public void OnlyExplicitlyValidatedSignaturesAreListed()
    {
        Assert.AreEqual(1, ValidatedGpuThermalSignatures.All.Count);
        Assert.AreSame(
            ValidatedGpuThermalSignatures.Rtx4090Laptop,
            ValidatedGpuThermalSignatures.All[0]);
    }

    // --- The ladder, on reference thresholds ---------------------------------------------------

    [DataTestMethod]
    [DataRow(74.0, GpuThermalSeverity.Normal)]
    [DataRow(75.0, GpuThermalSeverity.CriticalCooling)]
    [DataRow(76.0, GpuThermalSeverity.CriticalCooling)]
    [DataRow(77.0, GpuThermalSeverity.SustainedEmergency)]
    [DataRow(78.9, GpuThermalSeverity.SustainedEmergency)]
    [DataRow(79.0, GpuThermalSeverity.ImmediateEmergency)]
    [DataRow(80.0, GpuThermalSeverity.ImmediateEmergency)]
    public void SeverityLadderClassifiesEachBand(double celsius, GpuThermalSeverity expected) =>
        Assert.AreEqual(
            expected,
            TelemetryHealthEvaluator.ClassifyGpuThermalSeverity(celsius, Reference()));

    [TestMethod]
    public void Gpu74IsNormalAndLeavesTheCurveInCharge()
    {
        ThermalDecisionEngine engine = NewEngine();

        ThermalDecision decision = engine.Evaluate(At(50, 74, 0), Moment(0));

        Assert.IsFalse(decision.EmergencyAuto);
        Assert.IsFalse(engine.IsGpuCriticalCoolingActive);
        Assert.AreEqual(decision.GpuCurveTarget!.Value.Value, decision.EffectiveTarget.Value);
    }

    [DataTestMethod]
    [DataRow(75.0)]
    [DataRow(76.0)]
    public void GpuAtOrAboveMaxOperatingDemandsMaximumCoolingWithoutHandingOff(double celsius)
    {
        ThermalDecisionEngine engine = NewEngine();

        ThermalDecision decision = engine.Evaluate(At(50, celsius, 0), Moment(0));

        Assert.IsFalse(decision.EmergencyAuto, "Cooling first, not handoff.");
        Assert.IsTrue(engine.IsGpuCriticalCoolingActive);
        Assert.AreEqual(FanRpm.MaximumValue, decision.EffectiveTarget.Value);
    }

    [TestMethod]
    public void GpuAtSlowdownNeedsThreeConsecutiveSamplesToHandOff()
    {
        ThermalDecisionEngine engine = NewEngine();

        Assert.IsFalse(engine.Evaluate(At(50, 77, 0), Moment(0)).EmergencyAuto);
        Assert.IsFalse(engine.Evaluate(At(50, 77, 1), Moment(1)).EmergencyAuto);
        ThermalDecision third = engine.Evaluate(At(50, 77, 2), Moment(2));

        Assert.IsTrue(third.EmergencyAuto);
        StringAssert.Contains(third.Reason, "hardware slowdown");
    }

    [TestMethod]
    public void ALowerInterveningSampleResetsGpuSustainedQualification()
    {
        ThermalDecisionEngine engine = NewEngine();

        engine.Evaluate(At(50, 77, 0), Moment(0));
        engine.Evaluate(At(50, 77, 1), Moment(1));
        engine.Evaluate(At(50, 76, 2), Moment(2));

        Assert.IsFalse(engine.Evaluate(At(50, 77, 3), Moment(3)).EmergencyAuto);
        Assert.IsFalse(engine.Evaluate(At(50, 77, 4), Moment(4)).EmergencyAuto);
        Assert.IsFalse(engine.IsEmergencyStopped);
    }

    [DataTestMethod]
    [DataRow(79.0)]
    [DataRow(80.0)]
    public void GpuWithinADegreeOfShutdownHandsOffFromOneSample(double celsius)
    {
        ThermalDecisionEngine engine = NewEngine();

        ThermalDecision decision = engine.Evaluate(At(50, celsius, 0), Moment(0));

        Assert.IsTrue(decision.EmergencyAuto);
        Assert.IsTrue(engine.IsEmergencyStopped);
        StringAssert.Contains(decision.Reason, "hardware shutdown");
    }

    // --- GPU critical recovery -------------------------------------------------------------

    [TestMethod]
    public void GpuCriticalRecoveryRequiresThreeSamplesAtOrBelow72()
    {
        ThermalDecisionEngine engine = NewEngine();
        engine.Evaluate(At(50, 75, 0), Moment(0));
        Assert.IsTrue(engine.IsGpuCriticalCoolingActive);

        // 73 is below entry but above recovery: the band that would otherwise chatter.
        engine.Evaluate(At(50, 73, 1), Moment(1));
        Assert.IsTrue(engine.IsGpuCriticalCoolingActive);

        engine.Evaluate(At(50, 72, 2), Moment(2));
        Assert.IsTrue(engine.IsGpuCriticalCoolingActive, "One qualifying sample is not enough.");
        engine.Evaluate(At(50, 71, 3), Moment(3));
        Assert.IsTrue(engine.IsGpuCriticalCoolingActive);
        engine.Evaluate(At(50, 70, 4), Moment(4));
        Assert.IsFalse(engine.IsGpuCriticalCoolingActive, "Third qualifying sample releases it.");
    }

    [TestMethod]
    public void AHotSampleRestartsGpuRecoveryQualification()
    {
        ThermalDecisionEngine engine = NewEngine();
        engine.Evaluate(At(50, 75, 0), Moment(0));

        engine.Evaluate(At(50, 71, 1), Moment(1));
        engine.Evaluate(At(50, 76, 2), Moment(2));
        engine.Evaluate(At(50, 71, 3), Moment(3));

        Assert.IsTrue(engine.IsGpuCriticalCoolingActive);
    }

    // --- CPU and GPU composition ---------------------------------------------------------------

    [TestMethod]
    public void EitherSensorAloneDemandsMaximumCooling()
    {
        ThermalDecisionEngine cpuOnly = NewEngine();
        Assert.AreEqual(
            FanRpm.MaximumValue,
            cpuOnly.Evaluate(At(90, 50, 0), Moment(0)).EffectiveTarget.Value);
        Assert.IsTrue(cpuOnly.IsCpuCriticalCoolingActive);
        Assert.IsFalse(cpuOnly.IsGpuCriticalCoolingActive);

        ThermalDecisionEngine gpuOnly = NewEngine();
        Assert.AreEqual(
            FanRpm.MaximumValue,
            gpuOnly.Evaluate(At(50, 75, 0), Moment(0)).EffectiveTarget.Value);
        Assert.IsFalse(gpuOnly.IsCpuCriticalCoolingActive);
        Assert.IsTrue(gpuOnly.IsGpuCriticalCoolingActive);
    }

    /// <summary>
    /// The composition rule that matters: one sensor cooling down must not withdraw cooling
    /// the other still needs.
    /// </summary>
    [TestMethod]
    public void OneSensorRecoveringDoesNotReleaseCoolingTheOtherStillNeeds()
    {
        ThermalDecisionEngine engine = NewEngine();

        // Both go critical.
        engine.Evaluate(At(90, 75, 0), Moment(0));
        Assert.IsTrue(engine.IsCpuCriticalCoolingActive);
        Assert.IsTrue(engine.IsGpuCriticalCoolingActive);

        // CPU recovers fully; GPU stays hot.
        for (int index = 1; index <= 4; index++)
        {
            engine.Evaluate(At(80, 76, index), Moment(index));
        }

        Assert.IsFalse(engine.IsCpuCriticalCoolingActive, "CPU recovered.");
        Assert.IsTrue(engine.IsGpuCriticalCoolingActive, "GPU has not.");
        Assert.IsTrue(
            engine.IsCriticalCoolingActive,
            "Maximum cooling is held while any sensor remains critical.");
        Assert.AreEqual(
            FanRpm.MaximumValue,
            engine.Evaluate(At(80, 76, 5), Moment(5)).EffectiveTarget.Value);
    }

    [TestMethod]
    public void CoolingIsReleasedOnlyAfterBothSensorsRecover()
    {
        ThermalDecisionEngine engine = NewEngine();
        engine.Evaluate(At(90, 75, 0), Moment(0));

        int index = 1;
        for (; index <= 4; index++)
        {
            engine.Evaluate(At(80, 70, index), Moment(index));
        }

        Assert.IsFalse(engine.IsCpuCriticalCoolingActive);
        Assert.IsFalse(engine.IsGpuCriticalCoolingActive);
        Assert.IsFalse(engine.IsCriticalCoolingActive);
    }

    [TestMethod]
    public void EitherSensorReachingItsEmergencyHandsOff()
    {
        ThermalDecisionEngine cpu = NewEngine();
        Assert.IsTrue(cpu.Evaluate(At(100, 50, 0), Moment(0)).EmergencyAuto, "CPU Tjunction.");

        ThermalDecisionEngine gpu = NewEngine();
        Assert.IsTrue(gpu.Evaluate(At(50, 79, 0), Moment(0)).EmergencyAuto, "GPU pre-shutdown.");
    }

    /// <summary>The CPU ladder validated in the field run is untouched by the GPU work.</summary>
    [TestMethod]
    public void CpuLadderThresholdsAreUnchanged()
    {
        ThermalDecisionEngine engine = NewEngine();
        Assert.IsFalse(engine.Evaluate(At(89, 50, 0), Moment(0)).EmergencyAuto);
        Assert.IsFalse(engine.IsCpuCriticalCoolingActive);

        ThermalDecisionEngine critical = NewEngine();
        Assert.AreEqual(
            FanRpm.MaximumValue,
            critical.Evaluate(At(90, 50, 0), Moment(0)).EffectiveTarget.Value);

        ThermalDecisionEngine sustained = NewEngine();
        Assert.IsFalse(sustained.Evaluate(At(95, 50, 0), Moment(0)).EmergencyAuto);
        Assert.IsFalse(sustained.Evaluate(At(95, 50, 1), Moment(1)).EmergencyAuto);
        Assert.IsTrue(sustained.Evaluate(At(95, 50, 2), Moment(2)).EmergencyAuto);

        ThermalDecisionEngine immediate = NewEngine();
        Assert.IsTrue(immediate.Evaluate(At(100, 50, 0), Moment(0)).EmergencyAuto);
    }

    // --- No limits, no ladder -------------------------------------------------------------------

    /// <summary>
    /// Without device limits there is nothing safe to assume, so the engine takes no GPU
    /// action at all — and thermal ownership is refused at qualification instead.
    /// </summary>
    [TestMethod]
    public void WithoutDeviceLimitsTheGpuLadderIsInertRatherThanGuessing()
    {
        var engine = new ThermalDecisionEngine(Profile(), new ThermalPolicy());
        engine.InitializeBaseline(Start - TimeSpan.FromSeconds(1));

        ThermalDecision decision = engine.Evaluate(At(50, 85, 0), Moment(0));

        Assert.IsFalse(decision.EmergencyAuto);
        Assert.IsFalse(engine.IsGpuCriticalCoolingActive);
    }

    private static GpuThermalLimits Reference()
    {
        _ = GpuThermalLimits.TryCreate(
            75,
            77,
            80,
            GpuThermalLimitSource.NvmlTemperatureLimitSpecifications,
            out GpuThermalLimits? limits,
            out _);
        return limits!;
    }

    private static ThermalDecisionEngine NewEngine()
    {
        var engine = new ThermalDecisionEngine(
            Profile(),
            new ThermalPolicy { GpuLimits = Reference() });
        engine.InitializeBaseline(Start - TimeSpan.FromSeconds(1));
        return engine;
    }

    private static ThermalProfile Profile() => new(
        "graded-gpu-test",
        new ThermalCurve([Point(40, 3000), Point(80, 4000)]),
        new ThermalCurve([Point(40, 3000), Point(80, 4000)]));

    private static ThermalCurvePoint Point(double temperature, int rpm) =>
        new(temperature, new FanRpm(rpm));

    private static DateTimeOffset Moment(int index) => Start.AddMilliseconds(500 * index);

    private static TelemetrySnapshot At(double cpu, double gpu, int index)
    {
        DateTimeOffset stamp = Moment(index);
        return new TelemetrySnapshot(
            stamp,
            TelemetryMetric<double>.Available(cpu, stamp, TelemetrySources.CpuPackageTemperature),
            TelemetryMetric<double>.Available(gpu, stamp, TelemetrySources.GpuTemperature));
    }
}
