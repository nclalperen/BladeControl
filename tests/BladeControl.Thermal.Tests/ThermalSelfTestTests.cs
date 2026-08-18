using BladeControl.Razer;
using BladeControl.Telemetry;
using BladeControl.Thermal;

namespace BladeControl.Thermal.Tests;

[TestClass]
public sealed class ThermalSelfTestTests
{
    [TestMethod]
    public void FullSelfTestSuccessRestoresInitialState()
    {
        TestRig rig = new();

        ThermalSelfTestResult result = rig.Runner.Run();

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(5, result.Stages.Count);
        Assert.AreEqual(RazerPerformanceMode.Custom, result.FinalState!.Zone1PerformanceMode);
        Assert.AreEqual(RazerFanMode.Auto, result.FinalState.Zone1FanMode);
    }

    [TestMethod]
    public void QualificationFailureAbortsBeforeSet()
    {
        TestRig rig = new();
        rig.Telemetry.MissingCpu = true;

        ThermalSelfTestResult result = rig.Runner.Run();

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, rig.Control.WriteCount);
    }

    [TestMethod]
    public void FaultInjectionUsesProductionEmergencyAutoPath()
    {
        TestRig rig = new();

        ThermalSelfTestResult result = rig.Runner.Run();

        Assert.IsTrue(result.Stages.Single(stage =>
            stage.Stage.StartsWith("D -", StringComparison.Ordinal)).Succeeded);
        Assert.AreEqual(1, rig.Control.AutoCount);
    }

    [TestMethod]
    public void SelfTestRestoresOriginalPerformanceExactlyOnce()
    {
        TestRig rig = new();

        ThermalSelfTestResult result = rig.Runner.Run();

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, rig.Control.RestoreCount);
        CollectionAssert.Contains(rig.Control.Operations, "Auto");
        Assert.IsTrue(rig.Control.Operations.IndexOf("Auto") <
            rig.Control.Operations.IndexOf("Restore"));
    }

    private sealed class TestRig
    {
        internal TestRig()
        {
            Clock = new FakeClock();
            Telemetry = new FakeTelemetry(Clock);
            Control = new FakeControl();
            Runner = new ThermalSelfTestRunner(
                Telemetry,
                Control,
                Clock,
                new AdvancingDelay(Clock));
        }

        internal FakeClock Clock { get; }

        internal FakeTelemetry Telemetry { get; }

        internal FakeControl Control { get; }

        internal ThermalSelfTestRunner Runner { get; }
    }

    private sealed class FakeClock : IThermalClock
    {
        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

        internal void Advance(TimeSpan delay) => UtcNow += delay;
    }

    private sealed class AdvancingDelay : IThermalDelay
    {
        private readonly FakeClock _clock;

        internal AdvancingDelay(FakeClock clock)
        {
            _clock = clock;
        }

        public void Wait(TimeSpan delay) => _clock.Advance(delay);
    }

    private sealed class FakeTelemetry : ITelemetryProvider
    {
        private readonly FakeClock _clock;

        internal FakeTelemetry(FakeClock clock)
        {
            _clock = clock;
        }

        internal bool MissingCpu { get; set; }

        public string Name => "fake telemetry";

        public TelemetryCapabilities Capabilities { get; } = new();

        public TelemetrySnapshot GetSnapshot()
        {
            TelemetryMetric<double> cpu = MissingCpu
                ? TelemetryMetric<double>.Missing(
                    _clock.UtcNow,
                    TelemetrySources.CpuPackageTemperature,
                    "missing")
                : TelemetryMetric<double>.Available(
                    60,
                    _clock.UtcNow,
                    TelemetrySources.CpuPackageTemperature);
            return new TelemetrySnapshot(
                _clock.UtcNow,
                cpu,
                TelemetryMetric<double>.Available(
                    55,
                    _clock.UtcNow,
                    TelemetrySources.GpuTemperature));
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeControl : IThermalControlDevice
    {
        private readonly ThermalMachineState _initial = State(
            RazerPerformanceMode.Custom,
            RazerFanMode.Auto,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);

        private ThermalMachineState _current;

        internal FakeControl()
        {
            _current = _initial;
        }

        internal List<string> Operations { get; } = [];

        internal int WriteCount { get; private set; }

        internal int AutoCount { get; private set; }

        internal int RestoreCount { get; private set; }

        public ThermalFanModeObservation ReadFanModeObservation() =>
            new(
                CaptureState().Zone1PerformanceMode,
                CaptureState().Zone1FanMode,
                CaptureState().Zone2PerformanceMode,
                CaptureState().Zone2FanMode,
                []);

        public ThermalMachineState CaptureState() => _current;

        public ThermalControlOperationResult EnterManualBaseline(FanRpm baseline)
        {
            Operations.Add("Enter");
            WriteCount++;
            _current = State(
                RazerPerformanceMode.Balanced,
                RazerFanMode.Manual,
                _initial.CpuLevel,
                _initial.GpuLevel,
                baseline.Value);
            return Result(_current);
        }

        public ThermalControlOperationResult SetBothFans(FanRpm target)
        {
            Operations.Add($"Set {target.Value}");
            WriteCount++;
            _current = _current with
            {
                FirmwareReportedFan1Rpm = target.Value,
                FirmwareReportedFan2Rpm = target.Value
            };
            return Result(_current);
        }

        public ThermalControlOperationResult ReturnToBalancedAuto()
        {
            Operations.Add("Auto");
            WriteCount++;
            AutoCount++;
            _current = State(
                RazerPerformanceMode.Balanced,
                RazerFanMode.Auto,
                _current.CpuLevel,
                _current.GpuLevel);
            return Result(_current);
        }

        public ThermalControlOperationResult RestorePerformance(ThermalMachineState originalState)
        {
            Operations.Add("Restore");
            WriteCount++;
            RestoreCount++;
            _current = originalState;
            return Result(_current);
        }

        private static ThermalControlOperationResult Result(ThermalMachineState state) => new(
            true,
            true,
            false,
            state.IsBalancedAuto,
            "ok",
            state,
            []);
    }

    private static ThermalMachineState State(
        RazerPerformanceMode performance,
        RazerFanMode fan,
        RazerCpuPerformanceLevel cpu,
        RazerGpuPerformanceLevel gpu,
        int rpm = 3000) => new(
            new RazerDeviceInfo("fake", 0x1532, 0x029F, 0, 0, 91),
            performance,
            performance,
            fan,
            fan,
            cpu,
            gpu,
            rpm,
            rpm,
            []);
}
