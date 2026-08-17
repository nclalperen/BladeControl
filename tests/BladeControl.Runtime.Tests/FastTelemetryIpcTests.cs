using System.Text;
using BladeControl.Telemetry;
using BladeControl.Thermal;

namespace BladeControl.Runtime.Tests;

[TestClass]
public sealed class FastTelemetryIpcTests
{
    [TestMethod]
    public async Task OneHundredStoppedIpcSamplesUseOnlyCpuAndGpuProviders()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);
        int controlBefore = rig.Telemetry.ControlReads;
        int cpuBefore = rig.Telemetry.CpuReads;
        int gpuBefore = rig.Telemetry.GpuReads;
        int diagnosticBefore = rig.Telemetry.DiagnosticReads;
        int modeReadsBefore = rig.Hardware.ModeReads;
        int exchangesBefore = ProtocolExchangeCount(runtime);

        for (int index = 0; index < 100; index++)
        {
            RuntimeIpcResponse response = await DispatchAsync(dispatcher);
            Assert.IsTrue(response.Succeeded, response.Error);
            Assert.IsInstanceOfType<ThermalTelemetrySampleDto>(response.Data);
        }

        Assert.AreEqual(100, rig.Telemetry.ControlReads - controlBefore);
        Assert.AreEqual(100, rig.Telemetry.CpuReads - cpuBefore);
        Assert.AreEqual(100, rig.Telemetry.GpuReads - gpuBefore);
        Assert.AreEqual(diagnosticBefore, rig.Telemetry.DiagnosticReads);
        Assert.AreEqual(modeReadsBefore, rig.Hardware.ModeReads);
        Assert.AreEqual(exchangesBefore, ProtocolExchangeCount(runtime));
        Assert.AreEqual(0, rig.Hardware.FanWrites);
        Assert.AreEqual(0, rig.Hardware.PerformanceApplies);
    }

    [TestMethod]
    public async Task FastIpcSamplePreservesValuesTimestampsAndProvenance()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        rig.Telemetry.FixedCpuTemperature = 63.25;
        rig.Telemetry.FixedGpuTemperature = 57.5;
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);

        RuntimeIpcResponse response = await DispatchAsync(dispatcher);

        Assert.IsTrue(response.Succeeded, response.Error);
        var sample = (ThermalTelemetrySampleDto)response.Data!;
        Assert.AreEqual(63.25, sample.CpuPackageTemperatureCelsius.Value);
        Assert.AreEqual(57.5, sample.GpuTemperatureCelsius.Value);
        Assert.AreEqual(sample.Timestamp, sample.CpuPackageTemperatureCelsius.Timestamp);
        Assert.AreEqual(sample.Timestamp, sample.GpuTemperatureCelsius.Timestamp);
        Assert.AreEqual(
            "LibreHardwareMonitor / PawnIO",
            sample.CpuPackageTemperatureCelsius.Provider);
        Assert.AreEqual("NVIDIA NVML", sample.GpuTemperatureCelsius.Provider);
        Assert.AreEqual("Authoritative", sample.CpuPackageTemperatureCelsius.Authority);
        Assert.AreEqual("Authoritative", sample.GpuTemperatureCelsius.Authority);
        Assert.IsTrue(sample.CpuPackageTemperatureCelsius.IsValid);
        Assert.IsTrue(sample.GpuTemperatureCelsius.IsValid);
    }

    [TestMethod]
    public async Task FastIpcResponseStaysWithinProtocolMessageLimit()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);

        RuntimeIpcResponse response = await DispatchAsync(dispatcher);
        string json = RuntimeIpcDispatcher.SerializeResponse(response);

        Assert.IsTrue(response.Succeeded, response.Error);
        Assert.IsTrue(
            Encoding.UTF8.GetByteCount(json) < RuntimeIpcDispatcher.MaximumMessageBytes,
            $"Fast telemetry response was {Encoding.UTF8.GetByteCount(json)} bytes.");
    }

    [TestMethod]
    public async Task RunningFastIpcReusesLatestAuthoritativeControlSample()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();
        await runtime.RunScheduledAsync(CancellationToken.None, maximumCycles: 1);
        ThermalTelemetrySample expected = runtime.GetStatus().LatestAuthoritativeTelemetry!;
        int controlBefore = rig.Telemetry.ControlReads;
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);

        RuntimeIpcResponse response = await DispatchAsync(dispatcher);

        Assert.IsTrue(response.Succeeded, response.Error);
        var actual = (ThermalTelemetrySampleDto)response.Data!;
        Assert.AreEqual(controlBefore, rig.Telemetry.ControlReads);
        Assert.AreEqual(expected.Timestamp, actual.Timestamp);
        Assert.AreEqual(
            expected.CpuPackageTemperatureCelsius.Value,
            actual.CpuPackageTemperatureCelsius.Value);
        Assert.AreEqual(
            expected.GpuTemperatureCelsius.Value,
            actual.GpuTemperatureCelsius.Value);
        _ = await runtime.StopThermalControlAsync();
    }

    [TestMethod]
    public async Task FastSampleNeverReplacesFreshStartQualification()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);
        RuntimeIpcResponse sample = await DispatchAsync(dispatcher);
        Assert.IsTrue(sample.Succeeded, sample.Error);
        rig.Telemetry.MissingCpu = true;

        Assert.ThrowsException<ThermalPreflightException>(runtime.StartThermalControl);

        Assert.AreEqual(1, rig.Telemetry.QualificationReads);
        Assert.AreEqual(2, rig.Telemetry.ControlReads);
        Assert.AreEqual(0, rig.Hardware.FanWrites);
        Assert.AreEqual(0, rig.Hardware.AutoAttempts);
        CollectionAssert.DoesNotContain(rig.Hardware.Operations, "Capture");
    }

    private static ValueTask<RuntimeIpcResponse> DispatchAsync(
        RuntimeIpcDispatcher dispatcher) => dispatcher.DispatchAsync(new RuntimeIpcRequest(
        RuntimeIpcDispatcher.ProtocolVersion,
        Guid.NewGuid(),
        RuntimeIpcOperation.GetTelemetrySample,
        null));

    private static int ProtocolExchangeCount(BladeRuntime runtime) => runtime
        .GetStatus()
        .RecentEvents
        .OfType<ProtocolExchangeEvent>()
        .Count();
}
