using System.Reflection;
using BladeControl.Hardware.Windows.Telemetry.Nvml;
using BladeControl.Telemetry;

namespace BladeControl.Thermal.Tests;

[TestClass]
public sealed class NvmlTelemetryProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

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

    private static NvmlDevice Device(
        string name,
        string uuid,
        string pci,
        int handle) => new(
            new IntPtr(handle),
            new TelemetryGpuIdentity(name, uuid, pci));

    private sealed class FakeNvmlApi : INvmlApi
    {
        internal IReadOnlyList<NvmlDevice> Devices { get; set; } =
            [Device("RTX 4090 Laptop GPU", "GPU-test", "00000000:01:00.0", 7)];

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
