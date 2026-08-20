using BladeControl.Hardware.Windows.Telemetry.Nvml;
using BladeControl.Telemetry;

namespace BladeControl.Thermal.Tests;

/// <summary>
/// One qualification result, consumed everywhere, carrying its own reasons.
/// </summary>
/// <remarks>
/// <para>An installed RC reported two contradictory things four lines apart:</para>
/// <code>
/// GPU thermal limits            unavailable (thermal control will not qualify)
/// Thermal-control qualification Healthy: Required telemetry is healthy.
/// </code>
/// <para>Both lines were doing what they were written to do. The second one was not a
/// qualification at all — it printed <see cref="TelemetryHealthEvaluator.Evaluate"/>, which only
/// asks whether the CPU and GPU temperatures are present, plausible and fresh. It has no
/// opinion on GPU thermal limits, PawnIO provenance, Razer HID or GPU ambiguity, so it could
/// never have disagreed with itself; it simply was not answering the question its heading
/// claimed.</para>
/// <para>These tests hold the line that there is exactly one authority,
/// <see cref="ThermalOwnershipQualifier"/>, and that every surface displays its result rather
/// than computing a nearby approximation.</para>
/// </remarks>
[TestClass]
public sealed class ThermalQualificationSourceOfTruthTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 18, 0, 0, TimeSpan.Zero);

    private const string ReferenceGpuName = "NVIDIA GeForce RTX 4090 Laptop GPU";

    // --- Limits are a prerequisite, and their absence is stated ------------------------------

    [TestMethod]
    public void MissingGpuThermalLimitsRefuseOwnership()
    {
        ThermalOwnershipQualification qualification = Qualify(limits: null);

        Assert.IsFalse(qualification.ThermalOwnershipReady);
        Assert.IsFalse(qualification.GpuThermalLimitsKnown);
        Assert.IsTrue(
            qualification.Reasons.Any(reason => reason.Contains("GPU thermal limits")),
            "The refusal must name the missing prerequisite.");
    }

    /// <summary>
    /// Everything else passing is not enough. Limits are load-bearing on their own, which is
    /// what makes "limits unavailable" plus "ready: yes" impossible to produce.
    /// </summary>
    [TestMethod]
    public void LimitsAreIndependentlySufficientToRefuse()
    {
        Assert.IsTrue(Qualify(limits: Reference()).ThermalOwnershipReady, "Baseline is ready.");
        Assert.IsFalse(
            Qualify(limits: null).ThermalOwnershipReady,
            "Only the limits changed.");
    }

    /// <summary>
    /// The concrete discovery outcome reaches the refusal, so it does not have to be
    /// reproduced by hand with a separate probe.
    /// </summary>
    [TestMethod]
    public void RefusalCarriesTheProductionDiscoveryReason()
    {
        const string Reason = "field 193 failed: NotSupported.";
        ThermalOwnershipQualification qualification = Qualify(limits: null, diagnostic: Reason);

        Assert.AreEqual(Reason, qualification.GpuThermalLimitDiagnostic);
        Assert.IsTrue(
            qualification.Reasons.Any(reason => reason.Contains(Reason, StringComparison.Ordinal)),
            "A bare 'unavailable' forces the reader to go and reproduce discovery themselves.");
        StringAssert.Contains(qualification.Summary, Reason);
    }

    [TestMethod]
    public void AvailableValidatedLimitsQualify()
    {
        ThermalOwnershipQualification qualification = Qualify(limits: Reference());

        Assert.IsTrue(qualification.ThermalOwnershipReady, string.Join(" ", qualification.Reasons));
        Assert.IsTrue(qualification.GpuThermalLimitsKnown);
        Assert.AreEqual(75, qualification.GpuThermalLimits!.MaxOperatingCelsius);
        Assert.AreEqual("QUALIFIED", qualification.Summary);
    }

    /// <summary>Every prerequisite is individually necessary; none of them is decorative.</summary>
    [DataTestMethod]
    [DataRow(false, true, true, true, true, "CPU provider provenance")]
    [DataRow(true, false, true, true, true, "CPU temperature")]
    [DataRow(true, true, false, true, true, "GPU temperature")]
    [DataRow(true, true, true, false, true, "GPU selection")]
    [DataRow(true, true, true, true, false, "Razer HID")]
    public void EveryPrerequisiteCanRefuseOnItsOwn(
        bool cpuProvenance,
        bool cpuHealthy,
        bool gpuHealthy,
        bool deterministicGpu,
        bool razer,
        string component)
    {
        ThermalOwnershipQualification qualification = Qualify(
            limits: Reference(),
            cpuProvenanceSafe: cpuProvenance,
            cpuHealthy: cpuHealthy,
            gpuHealthy: gpuHealthy,
            deterministicGpu: deterministicGpu,
            razerAvailable: razer);

        Assert.IsFalse(qualification.ThermalOwnershipReady, component);
    }

    // --- Telemetry health is not qualification ------------------------------------------------

    /// <summary>
    /// The exact contradiction from the RC, reproduced: healthy sensors, refused ownership.
    /// The old doctor printed the first as though it were the second.
    /// </summary>
    [TestMethod]
    public void HealthySensorsDoNotImplyQualification()
    {
        ThermalTelemetrySample sample = Sample();
        TelemetryHealth health = TelemetryHealthEvaluator.EvaluateForControlLoop(sample.ToDiagnosticSnapshot(), Now);
        ThermalOwnershipQualification qualification = Qualify(limits: null);

        Assert.IsTrue(health.IsHealthy, "Both temperatures are present, plausible and fresh.");
        Assert.IsFalse(
            qualification.ThermalOwnershipReady,
            "And the machine still may not take thermal ownership.");
    }

    // --- The same NVML inputs, one answer ------------------------------------------------------

    /// <summary>
    /// The standalone probe and the production session must not be able to disagree: both go
    /// through the same discovery on the same provider.
    /// </summary>
    [TestMethod]
    public void ProbeAndProductionDiscoveryAgreeOnIdenticalInputs()
    {
        var api = new ProbeParityNvmlApi();
        Assert.IsTrue(NvmlTelemetryProvider.TryOpen(
            api,
            null,
            out NvmlTelemetryProvider? provider,
            out _,
            out _,
            out string diagnostic), diagnostic);
        using NvmlTelemetryProvider gpu = provider!;

        NvmlThermalLimitProbe probe = gpu.ProbeThermalLimits();
        Assert.IsTrue(gpu.TryDiscoverThermalLimits(out GpuThermalLimits? production, out _));

        // The probe's raw inputs, converted independently, must land on the production answer.
        Assert.IsTrue(GpuThermalLimits.TryFromValidatedSignature(
            ReferenceGpuName,
            probe.CurrentTemperatureCelsius!.Value,
            probe.MarginCelsius!.Value,
            probe.GpuMax.Celsius!.Value,
            probe.Slowdown.Celsius!.Value,
            probe.Shutdown.Celsius!.Value,
            probe.LegacyShutdown.Celsius,
            out GpuThermalLimits? fromProbe,
            out _));

        Assert.AreEqual(fromProbe!.MaxOperatingCelsius, production!.MaxOperatingCelsius);
        Assert.AreEqual(fromProbe.HardwareSlowdownCelsius, production.HardwareSlowdownCelsius);
        Assert.AreEqual(fromProbe.HardwareShutdownCelsius, production.HardwareShutdownCelsius);
    }

    /// <summary>
    /// The reference machine's live reading at the moment the RC was captured: 43 C core with a
    /// 32 C margin. Production must produce 75/77/80 from it.
    /// </summary>
    [TestMethod]
    public void ReferenceMachineInputsProduceTheValidatedLimits()
    {
        var api = new ProbeParityNvmlApi { Temperature = 43, Margin = 32 };
        Assert.IsTrue(NvmlTelemetryProvider.TryOpen(api, null, out NvmlTelemetryProvider? provider, out _, out _, out _));
        using NvmlTelemetryProvider gpu = provider!;

        Assert.IsTrue(gpu.TryDiscoverThermalLimits(out GpuThermalLimits? limits, out string diagnostic), diagnostic);
        Assert.AreEqual(75, limits!.MaxOperatingCelsius);
        Assert.AreEqual(77, limits.HardwareSlowdownCelsius);
        Assert.AreEqual(80, limits.HardwareShutdownCelsius);
    }

    /// <summary>No generic fallback: an unvalidated GPU yields nothing, whatever it reports.</summary>
    [TestMethod]
    public void UnvalidatedGpuStillYieldsNoLimits()
    {
        var api = new ProbeParityNvmlApi { DeviceName = "NVIDIA GeForce RTX 5080 Laptop GPU" };
        Assert.IsTrue(NvmlTelemetryProvider.TryOpen(api, null, out NvmlTelemetryProvider? provider, out _, out _, out _));
        using NvmlTelemetryProvider gpu = provider!;

        Assert.IsFalse(gpu.TryDiscoverThermalLimits(out GpuThermalLimits? limits, out string diagnostic));
        Assert.IsNull(limits);
        Assert.AreNotEqual(
            TelemetryHealthEvaluator.GpuEmergencyTemperatureCelsius,
            80d - 80d,
            "Sanity: no fixed fallback is substituted.");
        StringAssert.Contains(diagnostic, "validated");
    }

    /// <summary>
    /// The GPU preflight bar is a fixed policy choice, not a reading of the device.
    /// </summary>
    /// <remarks>
    /// It was once justified as the reference part's hardware shutdown temperature. That turned
    /// out to be a statement about one performance mode: the same GPU, same driver and same
    /// T.Limit specifications derives a shutdown limit of 80 C in Silent and 92 C in Balanced.
    /// Anyone comparing the constant against a live Balanced reading will find it 12 C low and
    /// may be tempted to "align" it. Raising it would loosen an entry gate to match a value the
    /// driver is entitled to change, so the intent is pinned here rather than left to a comment.
    /// </remarks>
    [TestMethod]
    public void GpuPreflightBarStaysAtTheConservativeFixedValue()
    {
        Assert.AreEqual(
            80d,
            TelemetryHealthEvaluator.GpuEmergencyTemperatureCelsius,
            "The GPU entry gate is deliberately the lower of the mode-dependent shutdown "
                + "limits. Read the remarks on the constant before changing this.");

        // And it must stay an entry gate: the running loop's health check has no opinion
        // about heat, so this bar can never itself trigger a live handoff.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TelemetryMetric<double> hot = TelemetryMetric<double>.Available(
            95d,
            now,
            TelemetrySources.GpuTemperature);

        Assert.IsTrue(
            TelemetryHealthEvaluator.EvaluateGpuTemperatureIntegrity(hot, now).IsHealthy,
            "A hot but valid GPU reading is not a telemetry fault.");
    }

    private static GpuThermalLimits Reference()
    {
        _ = GpuThermalLimits.TryCreate(
            75,
            77,
            80,
            GpuThermalLimitSource.NvmlTemperatureLimitSpecificationsOnValidatedSignature,
            out GpuThermalLimits? limits,
            out _);
        return limits!;
    }

    private static ThermalOwnershipQualification Qualify(
        GpuThermalLimits? limits,
        string diagnostic = "discovery diagnostic",
        bool cpuProvenanceSafe = true,
        bool cpuHealthy = true,
        bool gpuHealthy = true,
        bool deterministicGpu = true,
        bool razerAvailable = true) => ThermalOwnershipQualifier.Evaluate(
            Now,
            cpuProvenanceSafe,
            new TelemetryCapabilities
            {
                RazerHidAvailable = razerAvailable,
                NvmlAvailable = deterministicGpu,
                SelectedGpu = deterministicGpu
                    ? new TelemetryGpuIdentity(ReferenceGpuName, "GPU-1", "00000000:01:00.0")
                    : null,
                GpuSelectionAmbiguous = false,
                GpuThermalLimits = limits,
                GpuThermalLimitDiagnostic = diagnostic
            },
            Sample(cpuHealthy, gpuHealthy));

    private static ThermalTelemetrySample Sample(bool cpuHealthy = true, bool gpuHealthy = true) =>
        new(
            Now,
            cpuHealthy
                ? TelemetryMetric<double>.Available(60, Now, TelemetrySources.CpuPackageTemperature)
                : TelemetryMetric<double>.Invalid(null, Now, TelemetrySources.CpuPackageTemperature, "unavailable"),
            gpuHealthy
                ? TelemetryMetric<double>.Available(50, Now, TelemetrySources.GpuTemperature)
                : TelemetryMetric<double>.Invalid(null, Now, TelemetrySources.GpuTemperature, "unavailable"));

    /// <summary>Minimal NVML fake reporting the reference part's live values.</summary>
    private sealed class ProbeParityNvmlApi : INvmlApi
    {
        internal string DeviceName { get; set; } = ReferenceGpuName;

        internal double Temperature { get; set; } = 43;

        internal int Margin { get; set; } = 32;

        public NvmlResult Initialize() => NvmlResult.Success;

        public NvmlResult Shutdown() => NvmlResult.Success;

        public NvmlResult GetDevices(out IReadOnlyList<NvmlDevice> devices)
        {
            devices =
            [
                new NvmlDevice(
                    new IntPtr(1),
                    new TelemetryGpuIdentity(DeviceName, "GPU-1", "00000000:01:00.0"))
            ];
            return NvmlResult.Success;
        }

        public NvmlResult GetTemperatureCurrent(NvmlDevice device, out double temperature)
        {
            temperature = Temperature;
            return NvmlResult.Success;
        }

        public NvmlResult GetTemperatureLegacy(NvmlDevice device, out double temperature)
        {
            temperature = Temperature;
            return NvmlResult.Success;
        }

        public NvmlResult GetPowerWatts(NvmlDevice device, out double watts)
        {
            watts = 25;
            return NvmlResult.Success;
        }

        public NvmlResult GetUtilization(NvmlDevice device, out double gpuPercent, out double memoryPercent)
        {
            gpuPercent = 5;
            memoryPercent = 5;
            return NvmlResult.Success;
        }

        public NvmlResult GetClockMegahertz(NvmlDevice device, NvmlClockType type, out double megahertz)
        {
            megahertz = 1000;
            return NvmlResult.Success;
        }

        public NvmlResult GetMemory(NvmlDevice device, out ulong usedBytes, out ulong totalBytes)
        {
            usedBytes = 1;
            totalBytes = 2;
            return NvmlResult.Success;
        }

        public NvmlResult GetThermalLimitSpecifications(
            NvmlDevice device,
            out NvmlFieldReading shutdown,
            out NvmlFieldReading slowdown,
            out NvmlFieldReading gpuMax)
        {
            shutdown = Reading(NvmlFieldId.TemperatureShutdownTLimit, -5);
            slowdown = Reading(NvmlFieldId.TemperatureSlowdownTLimit, -2);
            gpuMax = Reading(NvmlFieldId.TemperatureGpuMaxTLimit, 0);
            return NvmlResult.Success;
        }

        public NvmlResult GetMarginTemperature(NvmlDevice device, out int marginCelsius)
        {
            marginCelsius = Margin;
            return NvmlResult.Success;
        }

        public NvmlResult GetTemperatureThreshold(
            NvmlDevice device,
            NvmlTemperatureThreshold threshold,
            out double celsius)
        {
            // The reference part's actual answers: a different quantity, kept diagnostic only.
            celsius = threshold switch
            {
                NvmlTemperatureThreshold.GpuMax => 105,
                NvmlTemperatureThreshold.Slowdown => 97,
                NvmlTemperatureThreshold.Shutdown => 100,
                _ => double.NaN
            };
            return double.IsFinite(celsius) ? NvmlResult.Success : NvmlResult.NotSupported;
        }

        public NvmlResult GetThermalSettings(
            NvmlDevice device,
            uint sensorIndex,
            out uint count,
            out IReadOnlyList<NvmlThermalSensor> sensors)
        {
            count = 0;
            sensors = [];
            return NvmlResult.NotSupported;
        }

        private static NvmlFieldReading Reading(uint fieldId, double celsius) => new(
            fieldId,
            NvmlResult.Success,
            NvmlValueType.SignedInt,
            (long)celsius,
            celsius);
    }
}
