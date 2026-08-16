using BladeControl.Razer;
using BladeControl.Telemetry;
using BladeControl.Thermal;

namespace BladeControl.Thermal.Tests;

[TestClass]
public sealed class ThermalRuntimeTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void HealthyStartEntersManualBaseline()
    {
        var clock = new FakeClock(Start);
        var control = new FakeControlDevice();
        using var telemetry = new FakeTelemetryProvider(clock);
        var runtime = Runtime(telemetry, control, clock);

        runtime.Start();

        Assert.AreEqual(ThermalControllerStateKind.Manual, runtime.State);
        CollectionAssert.AreEqual(new[] { "Capture", "Enter 3000" }, control.Operations);
    }

    [TestMethod]
    public void PreflightSensorFailureCausesZeroWrites()
    {
        var clock = new FakeClock(Start);
        var control = new FakeControlDevice();
        using var telemetry = new FakeTelemetryProvider(clock) { MissingCpu = true };
        var runtime = Runtime(telemetry, control, clock);

        Assert.ThrowsException<ThermalPreflightException>(runtime.Start);

        Assert.AreEqual(0, control.WriteOperations);
        Assert.AreEqual(0, control.Operations.Count);
    }

    [TestMethod]
    public void NormalStopReturnsAutoBeforeRestoringPerformance()
    {
        var clock = new FakeClock(Start);
        var control = new FakeControlDevice();
        using var telemetry = new FakeTelemetryProvider(clock);
        var runtime = Runtime(telemetry, control, clock);
        runtime.Start();

        ThermalSessionResult result = runtime.Stop();

        CollectionAssert.AreEqual(
            new[] { "Capture", "Enter 3000", "Auto", "Restore" },
            control.Operations);
        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public void PerformanceRestorationFailureLeavesFirmwareInAuto()
    {
        var clock = new FakeClock(Start);
        var control = new FakeControlDevice { RestoreSucceeds = false };
        using var telemetry = new FakeTelemetryProvider(clock);
        var runtime = Runtime(telemetry, control, clock);
        runtime.Start();

        ThermalSessionResult result = runtime.Stop();

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.FinalState!.IsBalancedAuto);
        Assert.AreEqual(1, control.RestoreCalls);
    }

    [TestMethod]
    public void AutoRecoveryFailurePreventsAllFurtherWrites()
    {
        var clock = new FakeClock(Start);
        var control = new FakeControlDevice { AutoSucceeds = false };
        using var telemetry = new FakeTelemetryProvider(clock);
        var runtime = Runtime(telemetry, control, clock);
        runtime.Start();

        ThermalSessionResult result = runtime.Stop();

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(1, control.AutoCalls);
        Assert.AreEqual(0, control.RestoreCalls);
        StringAssert.Contains(result.Message, "FAN AUTO RESTORATION FAILED");
    }

    [TestMethod]
    public void FailedFanTargetIsNotRetriedAndAutoIsAttemptedOnce()
    {
        var clock = new FakeClock(Start);
        var control = new FakeControlDevice { SetSucceeds = false };
        using var telemetry = new FakeTelemetryProvider(clock)
        {
            CpuTemperature = 75,
            GpuTemperature = 50
        };
        var runtime = Runtime(telemetry, control, clock);
        runtime.Start();
        clock.Advance(TimeSpan.FromSeconds(1));

        _ = runtime.RunCycle();

        Assert.AreEqual(1, control.SetCalls);
        Assert.AreEqual(1, control.AutoCalls);
        Assert.AreEqual(1, control.RestoreCalls);
    }

    [TestMethod]
    public void TwoProviderFailuresTriggerOneEmergencyAutoAndNoReentry()
    {
        var clock = new FakeClock(Start);
        var control = new FakeControlDevice();
        using var telemetry = new FakeTelemetryProvider(clock);
        var runtime = Runtime(telemetry, control, clock);
        runtime.Start();
        telemetry.ThrowOnRead = true;
        clock.Advance(TimeSpan.FromMilliseconds(500));
        _ = runtime.RunCycle();
        clock.Advance(TimeSpan.FromMilliseconds(500));

        _ = runtime.RunCycle();

        Assert.AreEqual(ThermalControllerStateKind.EmergencyStopped, runtime.State);
        Assert.AreEqual(1, control.AutoCalls);
        Assert.ThrowsException<InvalidOperationException>(runtime.RunCycle);
    }

    [TestMethod]
    public void EmergencyAutoIsExemptFromNormalWriteCoalescing()
    {
        var clock = new FakeClock(Start);
        var control = new FakeControlDevice();
        using var telemetry = new FakeTelemetryProvider(clock)
        {
            CpuTemperature = 90
        };
        var runtime = Runtime(telemetry, control, clock);

        Assert.ThrowsException<ThermalPreflightException>(runtime.Start);
        Assert.AreEqual(0, control.AutoCalls);
        Assert.AreEqual(0, control.WriteOperations);
    }

    private static ThermalRuntimeController Runtime(
        ITelemetryProvider telemetry,
        IThermalControlDevice control,
        IThermalClock clock) => new(
            telemetry,
            control,
            BuiltInThermalProfiles.Default,
            clock: clock);

    private sealed class FakeClock : IThermalClock
    {
        internal FakeClock(DateTimeOffset now)
        {
            UtcNow = now;
        }

        public DateTimeOffset UtcNow { get; private set; }

        internal void Advance(TimeSpan value) => UtcNow += value;
    }

    private sealed class FakeTelemetryProvider : ITelemetryProvider
    {
        private readonly FakeClock _clock;

        internal FakeTelemetryProvider(FakeClock clock)
        {
            _clock = clock;
        }

        internal double CpuTemperature { get; set; } = 55;

        internal double GpuTemperature { get; set; } = 50;

        internal bool MissingCpu { get; set; }

        internal bool ThrowOnRead { get; set; }

        public string Name => "fake";

        public TelemetryCapabilities Capabilities { get; } = new();

        public TelemetrySnapshot GetSnapshot()
        {
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("injected provider failure");
            }

            TelemetryMetric<double> cpu = MissingCpu
                ? TelemetryMetric<double>.Missing(
                    _clock.UtcNow,
                    TelemetrySources.CpuPackageTemperature,
                    "missing")
                : TelemetryMetric<double>.Available(
                    CpuTemperature,
                    _clock.UtcNow,
                    TelemetrySources.CpuPackageTemperature);
            return new TelemetrySnapshot(
                _clock.UtcNow,
                cpu,
                TelemetryMetric<double>.Available(
                    GpuTemperature,
                    _clock.UtcNow,
                    TelemetrySources.GpuTemperature));
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeControlDevice : IThermalControlDevice
    {
        private readonly ThermalMachineState _original = Machine(
            RazerPerformanceMode.Custom,
            RazerFanMode.Auto,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);

        private ThermalMachineState _current;

        internal FakeControlDevice()
        {
            _current = _original;
        }

        internal List<string> Operations { get; } = [];

        internal int WriteOperations { get; private set; }

        internal int SetCalls { get; private set; }

        internal int AutoCalls { get; private set; }

        internal int RestoreCalls { get; private set; }

        internal bool SetSucceeds { get; set; } = true;

        internal bool AutoSucceeds { get; set; } = true;

        internal bool RestoreSucceeds { get; set; } = true;

        public ThermalMachineState CaptureState()
        {
            Operations.Add("Capture");
            return _current;
        }

        public ThermalControlOperationResult EnterManualBaseline(FanRpm baseline)
        {
            Operations.Add($"Enter {baseline.Value}");
            WriteOperations++;
            _current = Machine(
                RazerPerformanceMode.Balanced,
                RazerFanMode.Manual,
                _original.CpuLevel,
                _original.GpuLevel,
                baseline.Value);
            return Result(true, _current, anyWrite: true);
        }

        public ThermalControlOperationResult SetBothFans(FanRpm target)
        {
            Operations.Add($"Set {target.Value}");
            WriteOperations++;
            SetCalls++;
            if (!SetSucceeds)
            {
                return Result(false, _current, anyWrite: true, "set failed");
            }

            _current = _current with
            {
                FirmwareReportedFan1Rpm = target.Value,
                FirmwareReportedFan2Rpm = target.Value
            };
            return Result(true, _current, anyWrite: true);
        }

        public ThermalControlOperationResult ReturnToBalancedAuto()
        {
            Operations.Add("Auto");
            WriteOperations++;
            AutoCalls++;
            if (!AutoSucceeds)
            {
                return Result(false, _current, anyWrite: true, "auto failed");
            }

            _current = Machine(
                RazerPerformanceMode.Balanced,
                RazerFanMode.Auto,
                _current.CpuLevel,
                _current.GpuLevel);
            return Result(true, _current, anyWrite: true);
        }

        public ThermalControlOperationResult RestorePerformance(ThermalMachineState originalState)
        {
            Operations.Add("Restore");
            WriteOperations++;
            RestoreCalls++;
            if (!RestoreSucceeds)
            {
                return Result(false, _current, anyWrite: true, "restore failed");
            }

            _current = originalState;
            return Result(true, _current, anyWrite: true);
        }

        private static ThermalControlOperationResult Result(
            bool success,
            ThermalMachineState state,
            bool anyWrite,
            string message = "ok") => new(
                success,
                anyWrite,
                false,
                state.IsBalancedAuto,
                message,
                state,
                []);
    }

    private static ThermalMachineState Machine(
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
