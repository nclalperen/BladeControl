using System.Reflection;
using BladeControl.Hardware.Windows.Telemetry.Nvml;
using BladeControl.Telemetry;

namespace BladeControl.Thermal.Tests;

[TestClass]
public sealed class NvmlTelemetryProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Exactly as NVML names the reference part. Discovery matches on it, so a placeholder
    /// here would test a device that cannot qualify.
    /// </summary>
    private const string ReferenceGpuName = "NVIDIA GeForce RTX 4090 Laptop GPU";

    [TestMethod]
    public void SuccessfulCapabilityProbeReturnsRequiredAndOptionalMetrics()
    {
        var api = new FakeNvmlApi();
        using NvmlTelemetryProvider provider = Open(api);

        NvmlGpuReading reading = provider.Read(Now);

        Assert.AreEqual(55d, reading.TemperatureCelsius.Value);
        Assert.AreEqual(25d, reading.PowerWatts.Value);
        Assert.AreEqual(10d, reading.GpuUtilizationPercent.Value);
        Assert.AreEqual(20d, reading.MemoryUtilizationPercent.Value);
    }

    [TestMethod]
    public void UnsupportedOptionalMetricDoesNotInvalidateTemperature()
    {
        var api = new FakeNvmlApi { PowerResult = NvmlResult.NotSupported };
        using NvmlTelemetryProvider provider = Open(api);

        NvmlGpuReading reading = provider.Read(Now);

        Assert.IsTrue(reading.TemperatureCelsius.IsValid);
        Assert.IsFalse(reading.PowerWatts.IsSupported);
    }

    [TestMethod]
    public void LostGpuInvalidatesRequiredTemperature()
    {
        var api = new FakeNvmlApi { TemperatureResult = NvmlResult.GpuIsLost };
        using NvmlTelemetryProvider provider = Open(api);

        NvmlGpuReading reading = provider.Read(Now);

        Assert.IsFalse(reading.TemperatureCelsius.IsValid);
        StringAssert.Contains(reading.TemperatureCelsius.Diagnostic!, "GpuIsLost");
    }

    [TestMethod]
    public void CurrentTemperatureFallsBackToLegacyWhenEntryPointUnavailable()
    {
        var api = new FakeNvmlApi
        {
            TemperatureResult = NvmlResult.EntryPointUnavailable,
            LegacyTemperature = 57
        };
        using NvmlTelemetryProvider provider = Open(api);

        NvmlGpuReading reading = provider.Read(Now);

        Assert.AreEqual(57d, reading.TemperatureCelsius.Value);
        Assert.AreEqual(1, api.LegacyTemperatureCalls);
    }

    [TestMethod]
    public void MultipleGpuAmbiguityRefusesAutomaticSelection()
    {
        var api = new FakeNvmlApi
        {
            Devices =
            [
                Device("GPU A", "GPU-a", "00000000:01:00.0", 1),
                Device("GPU B", "GPU-b", "00000000:02:00.0", 2)
            ]
        };

        bool opened = NvmlTelemetryProvider.TryOpen(
            api,
            null,
            out NvmlTelemetryProvider? provider,
            out bool ambiguous,
            out _,
            out _);

        Assert.IsFalse(opened);
        Assert.IsNull(provider);
        Assert.IsTrue(ambiguous);
        Assert.AreEqual(1, api.ShutdownCalls);
    }

    [TestMethod]
    public void SingleGpuIsSelectedWithoutAssumingIndexZero()
    {
        var api = new FakeNvmlApi
        {
            Devices = [Device("RTX 4090 Laptop GPU", "GPU-only", "00000000:01:00.0", 9)]
        };
        using NvmlTelemetryProvider provider = Open(api);

        Assert.AreEqual("GPU-only", provider.SelectedGpu.Uuid);
        Assert.AreEqual(new IntPtr(9), api.Devices.Single().Handle);
    }

    [TestMethod]
    public void ExactPciIdentitySelectsOneOfMultipleDevicesDeterministically()
    {
        var api = new FakeNvmlApi
        {
            Devices =
            [
                Device("GPU A", "GPU-a", "00000000:01:00.0", 1),
                Device("GPU B", "GPU-b", "00000000:02:00.0", 2)
            ]
        };

        Assert.IsTrue(NvmlTelemetryProvider.TryOpen(
            api,
            "00000000:02:00.0",
            out NvmlTelemetryProvider? provider,
            out bool ambiguous,
            out _,
            out _));
        using (provider)
        {
            Assert.IsFalse(ambiguous);
            Assert.AreEqual("GPU-b", provider!.SelectedGpu.Uuid);
        }
    }

    [TestMethod]
    public void NvmlInteropExposesNoMutationApi()
    {
        string[] methodNames = typeof(NvmlNativeMethods)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();

        Assert.IsFalse(methodNames.Any(name =>
            name.StartsWith("Set", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Reset", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Clear", StringComparison.OrdinalIgnoreCase)));
    }

    private static NvmlTelemetryProvider Open(FakeNvmlApi api)
    {
        Assert.IsTrue(NvmlTelemetryProvider.TryOpen(
            api,
            null,
            out NvmlTelemetryProvider? provider,
            out bool ambiguous,
            out _,
            out string diagnostic), diagnostic);
        Assert.IsFalse(ambiguous);
        return provider!;
    }

    // --- Thermal limit discovery ---------------------------------------------------------

    [TestMethod]
    public void ThermalLimitDiscoveryConvertsSpecificationsToAbsoluteTemperatures()
    {
        var api = new FakeNvmlApi();
        using NvmlTelemetryProvider provider = Open(api);

        bool discovered = provider.TryDiscoverThermalLimits(
            out GpuThermalLimits? limits,
            out string diagnostic);

        Assert.IsTrue(discovered, diagnostic);
        Assert.AreEqual(75, limits!.MaxOperatingCelsius, "55 + 20 - 0");
        Assert.AreEqual(77, limits.HardwareSlowdownCelsius, "55 + 20 - (-2)");
        Assert.AreEqual(80, limits.HardwareShutdownCelsius, "55 + 20 - (-5)");
        StringAssert.Contains(diagnostic, "NVML device thermal limits");
    }

    /// <summary>
    /// The specifications are static and the margin is live, so the same device at a different
    /// temperature must still resolve to the same absolute limits. Confirmed on hardware at two
    /// operating points: 66 C with a 9 C margin and 44 C with a 31 C margin both give 75 C.
    /// </summary>
    [TestMethod]
    public void ThermalLimitDiscoveryIsInvariantAcrossOperatingPoints()
    {
        var api = new FakeNvmlApi
        {
            TemperatureResult = NvmlResult.NotSupported,
            LegacyTemperature = 44,
            MarginCelsius = 31
        };
        using NvmlTelemetryProvider provider = Open(api);

        Assert.IsTrue(provider.TryDiscoverThermalLimits(out GpuThermalLimits? limits, out _));
        Assert.AreEqual(75, limits!.MaxOperatingCelsius);
        Assert.AreEqual(77, limits.HardwareSlowdownCelsius);
        Assert.AreEqual(80, limits.HardwareShutdownCelsius);
    }

    [TestMethod]
    public void UnsupportedFieldValueCallIsReportedRatherThanGuessed()
    {
        var api = new FakeNvmlApi { ThermalLimitCallResult = NvmlResult.NotSupported };
        using NvmlTelemetryProvider provider = Open(api);

        bool discovered = provider.TryDiscoverThermalLimits(
            out GpuThermalLimits? limits,
            out string diagnostic);

        Assert.IsFalse(discovered);
        Assert.IsNull(limits, "No threshold may be invented for a GPU that will not report one.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(diagnostic));
    }

    /// <summary>
    /// The call can succeed while one field is refused. A partially populated set must not be
    /// mistaken for limits — an unpopulated entry would otherwise read as a plausible 0 C.
    /// </summary>
    [TestMethod]
    public void OneRefusedFieldRejectsTheWholeSet()
    {
        var api = new FakeNvmlApi { ShutdownFieldResult = NvmlResult.NotSupported };
        using NvmlTelemetryProvider provider = Open(api);

        Assert.IsFalse(
            provider.TryDiscoverThermalLimits(out GpuThermalLimits? limits, out string diagnostic));
        Assert.IsNull(limits);
        StringAssert.Contains(diagnostic, "193", "The diagnostic must name the failing field.");
    }

    /// <summary>
    /// A value type outside the documented enumeration means the payload is not what this code
    /// believes it is, so nothing is derived from it.
    /// </summary>
    [TestMethod]
    public void UndecodableFieldValueTypeRejectsTheSet()
    {
        var api = new FakeNvmlApi
        {
            FieldsDecodable = false,
            FieldValueType = (NvmlValueType)99
        };
        using NvmlTelemetryProvider provider = Open(api);

        Assert.IsFalse(provider.TryDiscoverThermalLimits(out GpuThermalLimits? limits, out _));
        Assert.IsNull(limits);
    }

    /// <summary>
    /// The specifications alone cannot locate the reference point they are relative to, so
    /// without the margin API there is nothing to anchor them against.
    /// </summary>
    [TestMethod]
    public void MissingMarginApiRejectsDiscovery()
    {
        var api = new FakeNvmlApi { MarginResult = NvmlResult.FunctionNotFound };
        using NvmlTelemetryProvider provider = Open(api);

        Assert.IsFalse(provider.TryDiscoverThermalLimits(out GpuThermalLimits? limits, out _));
        Assert.IsNull(limits);
    }

    [TestMethod]
    public void ImplausibleSpecificationOrderingIsRefused()
    {
        // A positive slowdown offset would put slowdown below maximum operating, which cannot
        // be true; refusing beats acting on a misread encoding. Caught by the ordering check
        // before the configuration is even consulted.
        var api = new FakeNvmlApi { SlowdownSpecification = 5 };
        using NvmlTelemetryProvider provider = Open(api);

        Assert.IsFalse(provider.TryDiscoverThermalLimits(out GpuThermalLimits? limits, out _));
        Assert.IsNull(limits);
    }

    /// <summary>
    /// The specifications are per-device constants; the 500 ms telemetry path must never pay
    /// for them, nor for the margin read that anchors them.
    /// </summary>
    [TestMethod]
    public void OrdinaryTelemetryReadsDoNotQueryThermalLimits()
    {
        var api = new FakeNvmlApi();
        using NvmlTelemetryProvider provider = Open(api);

        for (int index = 0; index < 20; index++)
        {
            provider.Read(Now.AddMilliseconds(500 * index));
        }

        Assert.AreEqual(0, api.ThermalLimitReads);
        Assert.AreEqual(0, api.MarginReads);
    }

    // --- The anchor is established per configuration ---------------------------------------

    [TestMethod]
    public void ValidatedThermalSignatureQualifies()
    {
        var api = new FakeNvmlApi();
        using NvmlTelemetryProvider provider = Open(api);

        Assert.IsTrue(
            provider.TryDiscoverThermalLimits(out GpuThermalLimits? limits, out string diagnostic),
            diagnostic);
        Assert.AreEqual(75, limits!.MaxOperatingCelsius);
        Assert.AreEqual(
            GpuThermalLimitSource.NvmlTemperatureLimitSpecificationsOnValidatedSignature,
            limits.Source);
    }

    /// <summary>
    /// The counterexample that ordering and plausibility cannot catch. A margin anchored to
    /// slowdown, as NVML documents it, derives 77/79/82 — well formed, and two degrees hot
    /// everywhere. Matching against the validated values is what rejects it.
    /// </summary>
    [TestMethod]
    public void SlowdownAnchoredMarginIsRejected()
    {
        var api = new FakeNvmlApi
        {
            TemperatureResult = NvmlResult.NotSupported,
            LegacyTemperature = 44,
            MarginCelsius = 33
        };
        using NvmlTelemetryProvider provider = Open(api);

        Assert.IsFalse(
            provider.TryDiscoverThermalLimits(out GpuThermalLimits? limits, out string diagnostic));
        Assert.IsNull(limits);
        StringAssert.Contains(diagnostic, "77/79/82", "The derived values must be named.");
        StringAssert.Contains(diagnostic, "75/77/80", "So must the validated ones.");
    }

    /// <summary>
    /// Hardware whose T.Limit anchor has not been established by hand gets no limits, and so
    /// no thermal ownership. It does not get a guess.
    /// </summary>
    [TestMethod]
    public void UnvalidatedDeviceIsRefused()
    {
        var api = new FakeNvmlApi
        {
            Devices = [Device("NVIDIA GeForce RTX 5080 Laptop GPU", "GPU-x", "00000000:01:00.0", 9)]
        };
        using NvmlTelemetryProvider provider = Open(api);

        Assert.IsFalse(
            provider.TryDiscoverThermalLimits(out GpuThermalLimits? limits, out string diagnostic));
        Assert.IsNull(limits);
        StringAssert.Contains(diagnostic, "RTX 5080");
        StringAssert.Contains(diagnostic, "validated");
    }

    /// <summary>
    /// The legacy absolute thresholds proved not to describe the Ada operating limits at all
    /// (105/97/100 on the reference part), so qualification must not depend on them. They stay
    /// in the probe as diagnostics only.
    /// </summary>
    [TestMethod]
    public void LegacyAbsoluteThresholdsAreNotRequiredToQualify()
    {
        var api = new FakeNvmlApi { ThresholdResult = NvmlResult.FunctionNotFound };
        using NvmlTelemetryProvider provider = Open(api);

        Assert.IsTrue(
            provider.TryDiscoverThermalLimits(out GpuThermalLimits? limits, out string diagnostic),
            diagnostic);
        Assert.AreEqual(75, limits!.MaxOperatingCelsius);
    }

    /// <summary>
    /// The reference part's actual legacy values. They are not merely different from the
    /// derived set — they are not ordered as an operating set at all, with the GPU maximum
    /// above the shutdown point, which is what disqualified them as a witness.
    /// </summary>
    [TestMethod]
    public void ReferenceLegacyThresholdsAreReportedButNotUsed()
    {
        var api = new FakeNvmlApi
        {
            LegacyGpuMax = 105,
            LegacySlowdown = 97,
            LegacyShutdown = 100
        };
        using NvmlTelemetryProvider provider = Open(api);

        NvmlThermalLimitProbe probe = provider.ProbeThermalLimits();
        Assert.AreEqual(105, probe.LegacyGpuMax.Celsius);
        Assert.IsTrue(
            probe.LegacyGpuMax.Celsius > probe.LegacyShutdown.Celsius,
            "Not an operating set: the maximum sits above the shutdown point.");

        Assert.IsTrue(
            provider.TryDiscoverThermalLimits(out GpuThermalLimits? limits, out _),
            "Qualification must not be blocked by values it does not use.");
        Assert.AreEqual(80, limits!.HardwareShutdownCelsius);
    }

    /// <summary>The raw probe reports the driver's own answers without interpreting them.</summary>
    [TestMethod]
    public void ProbeReportsPerFieldStatusWithoutInterpretation()
    {
        var api = new FakeNvmlApi { ShutdownFieldResult = NvmlResult.NotSupported };
        using NvmlTelemetryProvider provider = Open(api);

        NvmlThermalLimitProbe probe = provider.ProbeThermalLimits();

        Assert.AreEqual(NvmlResult.Success, probe.FieldCallResult, "The call itself succeeded.");
        Assert.AreEqual(NvmlResult.NotSupported, probe.Shutdown.Result);
        Assert.AreEqual(NvmlResult.Success, probe.Slowdown.Result);
        Assert.AreEqual(-2, probe.Slowdown.Celsius);
        Assert.IsNull(probe.Shutdown.Celsius);
        Assert.AreEqual(20, probe.MarginCelsius);
    }

    private static NvmlDevice Device(
        string name,
        string uuid,
        string pci,
        int handle) => new(
            new IntPtr(handle),
            new TelemetryGpuIdentity(name, uuid, pci));

    private sealed class FakeNvmlApi : INvmlApi
    {
        /// <summary>
        /// Reference RTX 4090 Laptop T.Limit specifications unless a test overrides them,
        /// exactly as read from the device: 193 -> -5, 194 -> -2, 196 -> 0.
        /// </summary>
        internal double ShutdownSpecification { get; set; } = -5;

        internal double SlowdownSpecification { get; set; } = -2;

        internal double GpuMaxSpecification { get; set; }

        /// <summary>Result of the nvmlDeviceGetFieldValues call itself.</summary>
        internal NvmlResult ThermalLimitCallResult { get; set; } = NvmlResult.Success;

        /// <summary>Per-field status, which a driver can fail independently of the call.</summary>
        internal NvmlResult ShutdownFieldResult { get; set; } = NvmlResult.Success;

        /// <summary>
        /// False models a field whose declared valueType is not one this code understands, so
        /// the payload cannot be decoded even though the driver reported success.
        /// </summary>
        internal bool FieldsDecodable { get; set; } = true;

        internal NvmlValueType FieldValueType { get; set; } = NvmlValueType.SignedInt;

        internal int ThermalLimitReads { get; private set; }

        /// <summary>
        /// The live margin. At the fake's 55 C reading, 20 puts maximum operating at 75 C so
        /// the derived limits match the reference part.
        /// </summary>
        internal int MarginCelsius { get; set; } = 20;

        internal NvmlResult MarginResult { get; set; } = NvmlResult.Success;

        internal int MarginReads { get; private set; }

        /// <summary>
        /// Absolute thresholds from the legacy API. Defaults agree with the T.Limit
        /// derivation, which is what a device has to do to qualify.
        /// </summary>
        internal double LegacyGpuMax { get; set; } = 75;

        internal double LegacySlowdown { get; set; } = 77;

        internal double LegacyShutdown { get; set; } = 80;

        internal NvmlResult ThresholdResult { get; set; } = NvmlResult.Success;

        /// <summary>Overrides the result of the GPU-maximum query alone.</summary>
        internal NvmlResult? GpuMaxThresholdResult { get; set; }

        internal int ThresholdReads { get; private set; }

        /// <summary>Diagnostic-only API; the fake reports it unsupported unless a test cares.</summary>
        internal NvmlResult ThermalSettingsResult { get; set; } = NvmlResult.NotSupported;

        internal IReadOnlyList<NvmlThermalSensor> ThermalSettingsSensors { get; set; } = [];

        public NvmlResult GetThermalSettings(
            NvmlDevice device,
            uint sensorIndex,
            out uint count,
            out IReadOnlyList<NvmlThermalSensor> sensors)
        {
            if (ThermalSettingsResult != NvmlResult.Success)
            {
                count = 0;
                sensors = [];
                return ThermalSettingsResult;
            }

            sensors = ThermalSettingsSensors;
            count = (uint)sensors.Count;
            return NvmlResult.Success;
        }

        public NvmlResult GetTemperatureThreshold(
            NvmlDevice device,
            NvmlTemperatureThreshold threshold,
            out double celsius)
        {
            ThresholdReads++;
            NvmlResult result = threshold == NvmlTemperatureThreshold.GpuMax
                ? GpuMaxThresholdResult ?? ThresholdResult
                : ThresholdResult;
            if (result != NvmlResult.Success)
            {
                celsius = double.NaN;
                return result;
            }

            celsius = threshold switch
            {
                NvmlTemperatureThreshold.GpuMax => LegacyGpuMax,
                NvmlTemperatureThreshold.Slowdown => LegacySlowdown,
                NvmlTemperatureThreshold.Shutdown => LegacyShutdown,
                _ => double.NaN
            };
            return double.IsFinite(celsius) ? NvmlResult.Success : NvmlResult.NotSupported;
        }

        public NvmlResult GetThermalLimitSpecifications(
            NvmlDevice device,
            out NvmlFieldReading shutdown,
            out NvmlFieldReading slowdown,
            out NvmlFieldReading gpuMax)
        {
            ThermalLimitReads++;
            shutdown = Reading(
                NvmlFieldId.TemperatureShutdownTLimit,
                ShutdownSpecification,
                ShutdownFieldResult);
            slowdown = Reading(
                NvmlFieldId.TemperatureSlowdownTLimit,
                SlowdownSpecification,
                NvmlResult.Success);
            gpuMax = Reading(
                NvmlFieldId.TemperatureGpuMaxTLimit,
                GpuMaxSpecification,
                NvmlResult.Success);
            return ThermalLimitCallResult;
        }

        public NvmlResult GetMarginTemperature(NvmlDevice device, out int marginCelsius)
        {
            MarginReads++;
            marginCelsius = MarginResult == NvmlResult.Success ? MarginCelsius : 0;
            return MarginResult;
        }

        private NvmlFieldReading Reading(uint fieldId, double celsius, NvmlResult result)
        {
            bool usable = result == NvmlResult.Success &&
                ThermalLimitCallResult == NvmlResult.Success &&
                FieldsDecodable;
            return new NvmlFieldReading(
                fieldId,
                ThermalLimitCallResult == NvmlResult.Success ? result : ThermalLimitCallResult,
                FieldValueType,
                (long)celsius,
                usable ? celsius : null);
        }

        internal IReadOnlyList<NvmlDevice> Devices { get; set; } =
            [Device(ReferenceGpuName, "GPU-test", "00000000:01:00.0", 7)];

        internal NvmlResult TemperatureResult { get; set; } = NvmlResult.Success;

        internal NvmlResult PowerResult { get; set; } = NvmlResult.Success;

        internal double LegacyTemperature { get; set; } = 55;

        internal int LegacyTemperatureCalls { get; private set; }

        internal int ShutdownCalls { get; private set; }

        public NvmlResult Initialize() => NvmlResult.Success;

        public NvmlResult Shutdown()
        {
            ShutdownCalls++;
            return NvmlResult.Success;
        }

        public NvmlResult GetDevices(out IReadOnlyList<NvmlDevice> devices)
        {
            devices = Devices;
            return NvmlResult.Success;
        }

        public NvmlResult GetTemperatureCurrent(NvmlDevice device, out double temperature)
        {
            temperature = 55;
            return TemperatureResult;
        }

        public NvmlResult GetTemperatureLegacy(NvmlDevice device, out double temperature)
        {
            LegacyTemperatureCalls++;
            temperature = LegacyTemperature;
            return NvmlResult.Success;
        }

        public NvmlResult GetPowerWatts(NvmlDevice device, out double watts)
        {
            watts = 25;
            return PowerResult;
        }

        public NvmlResult GetUtilization(
            NvmlDevice device,
            out double gpuPercent,
            out double memoryPercent)
        {
            gpuPercent = 10;
            memoryPercent = 20;
            return NvmlResult.Success;
        }

        public NvmlResult GetClockMegahertz(
            NvmlDevice device,
            NvmlClockType type,
            out double megahertz)
        {
            megahertz = type == NvmlClockType.Graphics ? 1500 : 8000;
            return NvmlResult.Success;
        }

        public NvmlResult GetMemory(
            NvmlDevice device,
            out ulong usedBytes,
            out ulong totalBytes)
        {
            usedBytes = 1024;
            totalBytes = 4096;
            return NvmlResult.Success;
        }
    }
}
