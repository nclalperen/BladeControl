using System.Runtime.InteropServices;
using BladeControl.Hardware.Windows.Telemetry.Nvml;

namespace BladeControl.Thermal.Tests;

/// <summary>
/// The NVML interop layer itself: field identifiers, struct layout, and union decoding.
/// </summary>
/// <remarks>
/// <para>An earlier revision used field identifiers 191/192/194 and called 194 the GPU maximum
/// T.Limit. The real R610 assignments are 193 shutdown, 194 slowdown, 195 memory maximum, 196
/// GPU maximum, and 192 is NVML_FI_DEV_POWER_REQUESTED_LIMIT — a power field in milliwatts.</para>
/// <para>Nothing in the system would have complained. The driver answers the wrong field
/// successfully, the wrong value decodes cleanly, and a plausible number reaches the thermal
/// ladder. These tests exist because the failure mode is a silent wrong answer, not an
/// exception, and the only defence is asserting the constants and the wire format directly.</para>
/// <para>Raw payloads below were captured from the reference RTX 4090 Laptop GPU (driver
/// 610.88) by the read-only <c>telemetry gpu-thermal-probe</c> command.</para>
/// </remarks>
[TestClass]
public sealed class NvmlInteropTests
{
    // --- Field identifiers -----------------------------------------------------------------

    [TestMethod]
    public void CoreThermalFieldIdentifiersMatchTheR610Header()
    {
        Assert.AreEqual(193u, NvmlFieldId.TemperatureShutdownTLimit, "NVML_FI_DEV_TEMPERATURE_SHUTDOWN_TLIMIT");
        Assert.AreEqual(194u, NvmlFieldId.TemperatureSlowdownTLimit, "NVML_FI_DEV_TEMPERATURE_SLOWDOWN_TLIMIT");
        Assert.AreEqual(195u, NvmlFieldId.TemperatureMemoryMaxTLimit, "NVML_FI_DEV_TEMPERATURE_MEM_MAX_TLIMIT");
        Assert.AreEqual(196u, NvmlFieldId.TemperatureGpuMaxTLimit, "NVML_FI_DEV_TEMPERATURE_GPU_MAX_TLIMIT");
    }

    /// <summary>194 is the slowdown specification. Labelling it GPU maximum was the original bug.</summary>
    [TestMethod]
    public void SlowdownAndGpuMaximumAreDistinctIdentifiers()
    {
        Assert.AreNotEqual(
            NvmlFieldId.TemperatureSlowdownTLimit,
            NvmlFieldId.TemperatureGpuMaxTLimit);
        Assert.AreEqual(
            NvmlFieldId.TemperatureGpuMaxTLimit,
            NvmlFieldId.TemperatureSlowdownTLimit + 2,
            "GPU maximum sits two past slowdown, with memory maximum between them.");
    }

    /// <summary>
    /// 192 is a power limit in milliwatts. Reading it as a temperature would produce a large
    /// positive number that passes every plausibility check further down.
    /// </summary>
    [TestMethod]
    public void NoThermalIdentifierCollidesWithThePowerLimitField()
    {
        Assert.AreEqual(192u, NvmlFieldId.PowerRequestedLimit);
        CollectionAssert.DoesNotContain(
            NvmlFieldId.CoreThermalLimitRequest,
            NvmlFieldId.PowerRequestedLimit);
    }

    [TestMethod]
    public void ThermalLimitRequestAsksForExactlyTheThreeCoreFields()
    {
        CollectionAssert.AreEqual(
            new uint[] { 193, 194, 196 },
            NvmlFieldId.CoreThermalLimitRequest,
            "Memory maximum (195) is not a core limit and must not be requested.");
    }

    // --- Struct layout ---------------------------------------------------------------------

    [TestMethod]
    public void FieldValueLayoutMatchesNvmlHeaderOnWindowsX64()
    {
        Assert.AreEqual(40, Marshal.SizeOf<NvmlFieldValue>());
        Assert.AreEqual(0, (int)Marshal.OffsetOf<NvmlFieldValue>(nameof(NvmlFieldValue.FieldId)));
        Assert.AreEqual(4, (int)Marshal.OffsetOf<NvmlFieldValue>(nameof(NvmlFieldValue.ScopeId)));
        Assert.AreEqual(8, (int)Marshal.OffsetOf<NvmlFieldValue>(nameof(NvmlFieldValue.Timestamp)));
        Assert.AreEqual(16, (int)Marshal.OffsetOf<NvmlFieldValue>(nameof(NvmlFieldValue.LatencyUsec)));
        Assert.AreEqual(24, (int)Marshal.OffsetOf<NvmlFieldValue>(nameof(NvmlFieldValue.ValueType)));
        Assert.AreEqual(28, (int)Marshal.OffsetOf<NvmlFieldValue>(nameof(NvmlFieldValue.Result)));
        Assert.AreEqual(32, (int)Marshal.OffsetOf<NvmlFieldValue>(nameof(NvmlFieldValue.Value)));
    }

    [TestMethod]
    public void MarginTemperatureStructVersionMatchesNvmlStructVersion()
    {
        Assert.AreEqual(8, Marshal.SizeOf<NvmlMarginTemperature>());

        // NVML_STRUCT_VERSION(MarginTemperature, 1) = sizeof | (1 << 24).
        Assert.AreEqual(8u | (1u << 24), NvmlNativeMethods.MarginTemperatureVersion);
    }

    /// <summary>
    /// nvmlGpuThermalSettings_t: a count followed by three 20-byte sensor entries, every field
    /// four bytes so nothing pads. Verified before the first live call, not after.
    /// </summary>
    [TestMethod]
    public void ThermalSettingsLayoutMatchesNvmlHeaderOnWindowsX64()
    {
        Assert.AreEqual(20, Marshal.SizeOf<NvmlThermalSensor>());
        Assert.AreEqual(0, (int)Marshal.OffsetOf<NvmlThermalSensor>(nameof(NvmlThermalSensor.Controller)));
        Assert.AreEqual(4, (int)Marshal.OffsetOf<NvmlThermalSensor>(nameof(NvmlThermalSensor.DefaultMinTemp)));
        Assert.AreEqual(8, (int)Marshal.OffsetOf<NvmlThermalSensor>(nameof(NvmlThermalSensor.DefaultMaxTemp)));
        Assert.AreEqual(12, (int)Marshal.OffsetOf<NvmlThermalSensor>(nameof(NvmlThermalSensor.CurrentTemp)));
        Assert.AreEqual(16, (int)Marshal.OffsetOf<NvmlThermalSensor>(nameof(NvmlThermalSensor.Target)));

        Assert.AreEqual(3, NvmlGpuThermalSettings.MaxSensors, "NVML_MAX_THERMAL_SENSORS_PER_GPU");
        Assert.AreEqual(4 + (3 * 20), Marshal.SizeOf<NvmlGpuThermalSettings>());
        Assert.AreEqual(0, (int)Marshal.OffsetOf<NvmlGpuThermalSettings>(nameof(NvmlGpuThermalSettings.Count)));
        Assert.AreEqual(4, (int)Marshal.OffsetOf<NvmlGpuThermalSettings>(nameof(NvmlGpuThermalSettings.Sensors)));
    }

    /// <summary>
    /// Both enumerations define UNKNOWN as -1, so their underlying type has to be signed.
    /// </summary>
    [TestMethod]
    public void ThermalEnumerationsAreSignedAndMatchTheHeader()
    {
        Assert.AreEqual(typeof(int), Enum.GetUnderlyingType(typeof(NvmlThermalTarget)));
        Assert.AreEqual(typeof(int), Enum.GetUnderlyingType(typeof(NvmlThermalController)));
        Assert.AreEqual(-1, (int)NvmlThermalTarget.Unknown);
        Assert.AreEqual(-1, (int)NvmlThermalController.Unknown);
        Assert.AreEqual(1, (int)NvmlThermalTarget.Gpu);
        Assert.AreEqual(15, (int)NvmlThermalTarget.All);
    }

    /// <summary>The by-value sensor array must exist before marshalling, or the call faults.</summary>
    [TestMethod]
    public void ThermalSettingsRequestAllocatesItsSensorArray()
    {
        NvmlGpuThermalSettings settings = NvmlGpuThermalSettings.Create();

        Assert.IsNotNull(settings.Sensors);
        Assert.AreEqual(NvmlGpuThermalSettings.MaxSensors, settings.Sensors.Length);
    }

    // --- Union decoding --------------------------------------------------------------------

    /// <summary>
    /// The exact payloads the reference GPU returned. The upper four bytes are zero, so reading
    /// the full eight-byte union as a signed long would turn -5 into 4294967291 — a number that
    /// looks like a temperature and is not one.
    /// </summary>
    [DataTestMethod]
    [DataRow(0x00000000FFFFFFFBL, -5.0, "shutdown specification")]
    [DataRow(0x00000000FFFFFFFEL, -2.0, "slowdown specification")]
    [DataRow(0x0000000000000000L, 0.0, "GPU maximum specification")]
    public void SignedIntPayloadsFromTheReferenceGpuDecodeWithTheirSign(
        long raw,
        double expected,
        string field)
    {
        var value = new NvmlFieldValue
        {
            ValueType = NvmlValueType.SignedInt,
            Value = raw
        };

        Assert.IsTrue(value.TryReadCelsius(out double celsius), field);
        Assert.AreEqual(expected, celsius, field);
    }

    [TestMethod]
    public void NegativeSpecificationIsNotWidenedIntoAPositiveTemperature()
    {
        var value = new NvmlFieldValue
        {
            ValueType = NvmlValueType.SignedInt,
            Value = 0x00000000FFFFFFFBL
        };

        Assert.IsTrue(value.TryReadCelsius(out double celsius));
        Assert.AreNotEqual(
            4294967291d,
            celsius,
            "Reading all eight union bytes would produce this; the sign must survive.");
        Assert.IsTrue(celsius < 0);
    }

    /// <summary>
    /// Upper bytes of the union are not meaningful for a narrow member, so whatever occupies
    /// them must not reach the decoded value.
    /// </summary>
    [TestMethod]
    public void GarbageInTheUnusedUnionBytesDoesNotAffectASignedIntField()
    {
        var value = new NvmlFieldValue
        {
            ValueType = NvmlValueType.SignedInt,
            Value = unchecked((long)0xDEADBEEFFFFFFFFBUL)
        };

        Assert.IsTrue(value.TryReadCelsius(out double celsius));
        Assert.AreEqual(-5, celsius);
    }

    [TestMethod]
    public void UnsignedLongIsFourBytesInTheWindowsDataModel()
    {
        var value = new NvmlFieldValue
        {
            ValueType = NvmlValueType.UnsignedLong,
            Value = unchecked((long)0xDEADBEEF0000004BUL)
        };

        Assert.IsTrue(value.TryReadCelsius(out double celsius));
        Assert.AreEqual(75, celsius);
    }

    [TestMethod]
    public void DoublePayloadIsReinterpretedRatherThanCast()
    {
        var value = new NvmlFieldValue
        {
            ValueType = NvmlValueType.Double,
            Value = BitConverter.DoubleToInt64Bits(77.5)
        };

        Assert.IsTrue(value.TryReadCelsius(out double celsius));
        Assert.AreEqual(77.5, celsius);
    }

    [TestMethod]
    public void UnrecognisedValueTypeIsRejectedRatherThanGuessed()
    {
        var value = new NvmlFieldValue
        {
            ValueType = (NvmlValueType)99,
            Value = 75
        };

        Assert.IsFalse(
            value.TryReadCelsius(out _),
            "An unknown value type means the payload width is unknown; guessing it is not safe.");
    }

    [TestMethod]
    public void NonFiniteDoublePayloadIsRejected()
    {
        var value = new NvmlFieldValue
        {
            ValueType = NvmlValueType.Double,
            Value = BitConverter.DoubleToInt64Bits(double.NaN)
        };

        Assert.IsFalse(value.TryReadCelsius(out _));
    }

    // --- Result codes ----------------------------------------------------------------------

    /// <summary>
    /// A versioned call answering this means the struct version was not populated correctly,
    /// which is worth naming rather than printing as an unexplained 25.
    /// </summary>
    [TestMethod]
    public void ArgumentVersionMismatchIsNamed()
    {
        Assert.AreEqual(25, (int)NvmlResult.ArgumentVersionMismatch);
        Assert.AreEqual(3, (int)NvmlResult.NotSupported);
        Assert.AreEqual(13, (int)NvmlResult.FunctionNotFound);
    }
}
