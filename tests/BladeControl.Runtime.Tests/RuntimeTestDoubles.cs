using System.Diagnostics;
using BladeControl.Razer;
using BladeControl.Runtime;
using BladeControl.Telemetry;
using BladeControl.Thermal;

namespace BladeControl.Runtime.Tests;

internal sealed class VirtualRuntimeClock : IRuntimeClock
{
    private readonly DateTimeOffset _origin =
        new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

    internal List<TimeSpan> Delays { get; } = [];

    public DateTimeOffset UtcNow => _origin + MonotonicNow;

    public TimeSpan MonotonicNow { get; private set; }

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (delay > TimeSpan.Zero)
        {
            Delays.Add(delay);
            MonotonicNow += delay;
        }

        return ValueTask.CompletedTask;
    }

    internal void Advance(TimeSpan duration) => MonotonicNow += duration;
}

internal sealed class FakeRuntimeTelemetry : IControlTelemetryProvider, ITelemetryProvider
{
    private readonly VirtualRuntimeClock _clock;
    private long _sample;

    internal FakeRuntimeTelemetry(VirtualRuntimeClock clock)
    {
        _clock = clock;
    }

    internal int ControlReads { get; private set; }

    internal int CpuReads { get; private set; }

    internal int GpuReads { get; private set; }

    internal int DiagnosticReads { get; private set; }

    internal TimeSpan WorkDuration { get; set; }

    internal bool MissingCpu { get; set; }

    internal bool MissingGpu { get; set; }

    internal bool CpuProviderProvenanceSafe { get; set; } = true;

    internal bool GpuSelectionDeterministic { get; set; } = true;

    internal bool RazerHidAvailable { get; set; } = true;

    internal int QualificationReads { get; private set; }

    internal double? FixedCpuTemperature { get; set; }

    internal double? FixedGpuTemperature { get; set; }

    internal Action<long>? BeforeRead { get; set; }

    public string Name => "fake runtime telemetry";

    public TelemetryCapabilities Capabilities => CreateCapabilities();

    /// <summary>
    /// Reference RTX 4090 Laptop limits by default: max operating 75 C, hardware slowdown
    /// 77 C, hardware shutdown 80 C. Set to null to model a GPU that cannot report limits.
    /// </summary>
    internal GpuThermalLimits? GpuThermalLimits { get; set; } = ReferenceGpuLimits;

    /// <summary>What discovery concluded, carried alongside the result in either direction.</summary>
    internal string GpuThermalLimitDiagnostic { get; set; } =
        "max operating 75 C, hardware slowdown 77 C, hardware shutdown 80 C";

    internal static GpuThermalLimits ReferenceGpuLimits { get; } = Build(75, 77, 80);

    private static GpuThermalLimits Build(double maxOperating, double slowdown, double shutdown)
    {
        _ = GpuThermalLimits.TryCreate(
            maxOperating,
            slowdown,
            shutdown,
            GpuThermalLimitSource.NvmlTemperatureLimitSpecificationsCorroborated,
            out GpuThermalLimits? limits,
            out _);
        return limits!;
    }

    public ThermalTelemetrySample GetControlSample()
    {
        long sample = ++_sample;
        BeforeRead?.Invoke(sample);
        ControlReads++;
        CpuReads++;
        GpuReads++;
        _clock.Advance(WorkDuration);
        double cpuValue = FixedCpuTemperature ?? (55 + ((sample / 120) % 20));
        double gpuValue = FixedGpuTemperature ?? (50 + ((sample / 180) % 18));
        TelemetryMetric<double> cpu = MissingCpu
            ? TelemetryMetric<double>.Missing(
                _clock.UtcNow,
                TelemetrySources.CpuPackageTemperature,
                "injected CPU loss")
            : TelemetryMetric<double>.Available(
                cpuValue,
                _clock.UtcNow,
                TelemetrySources.CpuPackageTemperature);
        TelemetryMetric<double> gpu = MissingGpu
            ? TelemetryMetric<double>.Missing(
                _clock.UtcNow,
                TelemetrySources.GpuTemperature,
                "injected GPU loss")
            : TelemetryMetric<double>.Available(
                gpuValue,
                _clock.UtcNow,
                TelemetrySources.GpuTemperature);
        return new ThermalTelemetrySample(_clock.UtcNow, cpu, gpu);
    }

    public TelemetrySnapshot GetSnapshot()
    {
        DiagnosticReads++;
        return GetControlSample().ToDiagnosticSnapshot();
    }

    public ThermalOwnershipQualification QualifyThermalOwnership()
    {
        QualificationReads++;
        ThermalTelemetrySample sample = GetControlSample();
        return ThermalOwnershipQualifier.Evaluate(
            _clock.UtcNow,
            CpuProviderProvenanceSafe,
            CreateCapabilities(),
            sample);
    }

    public void Dispose()
    {
    }

    private TelemetryCapabilities CreateCapabilities() => new()
    {
        RazerHidAvailable = RazerHidAvailable,
        NvmlAvailable = GpuSelectionDeterministic,
        SelectedGpu = GpuSelectionDeterministic
            ? new TelemetryGpuIdentity("Fake GPU", "GPU-fake", "00000000:01:00.0")
            : null,
        GpuTemperatureSupported = !MissingGpu,
        GpuThermalLimits = GpuThermalLimits,
        GpuThermalLimitDiagnostic = GpuThermalLimitDiagnostic,
        PawnIoAvailable = CpuProviderProvenanceSafe,
        CpuPackageTemperatureAvailable = !MissingCpu,
        GpuSelectionAmbiguous = false
    };
}

internal sealed class FakeRuntimeHardware : IRuntimeHardwareController
{
    private static readonly RazerDeviceInfo Device = new(
        "fake",
        0x1532,
        0x029F,
        0,
        0,
        91);

    private ThermalMachineState _state = Machine(
        RazerPerformanceMode.Custom,
        RazerFanMode.Auto,
        RazerCpuPerformanceLevel.Medium,
        RazerGpuPerformanceLevel.Low,
        2000);
    private byte _transactionId;

    public event Action<RazerExchangeTrace>? ExchangeCompleted;

    internal List<string> Operations { get; } = [];

    internal int ModeReads { get; private set; }

    internal int AutoAttempts { get; private set; }

    internal int FanWrites { get; private set; }

    internal int PerformanceApplies { get; private set; }

    internal bool AutoSucceeds { get; set; } = true;

    internal bool RestoreSucceeds { get; set; } = true;

    /// <summary>
    /// When false, the Manual baseline SET fails. Lets a test distinguish a genuine control
    /// failure — which must still Fault — from a prerequisite that was simply not met.
    /// </summary>
    internal bool FanApplySucceeds { get; set; } = true;

    internal PerformanceProfile? LastPerformanceProfile { get; private set; }

    internal RuntimeRazerModeState ModeState => new(
        _state.Zone1PerformanceMode,
        _state.Zone1FanMode,
        _state.Zone2PerformanceMode,
        _state.Zone2FanMode,
        []);

    internal void SetMode(
        RazerPerformanceMode zone1Performance,
        RazerFanMode zone1Fan,
        RazerPerformanceMode? zone2Performance = null,
        RazerFanMode? zone2Fan = null)
    {
        _state = _state with
        {
            Zone1PerformanceMode = zone1Performance,
            Zone1FanMode = zone1Fan,
            Zone2PerformanceMode = zone2Performance ?? zone1Performance,
            Zone2FanMode = zone2Fan ?? zone1Fan
        };
    }

    internal void SetPerformanceLevels(
        RazerCpuPerformanceLevel cpu,
        RazerGpuPerformanceLevel gpu)
    {
        _state = _state with { CpuLevel = cpu, GpuLevel = gpu };
    }

    internal void SetFanRpm(int fan1Rpm, int fan2Rpm)
    {
        _state = _state with
        {
            FirmwareReportedFan1Rpm = fan1Rpm,
            FirmwareReportedFan2Rpm = fan2Rpm
        };
    }

    internal void EmitExchanges(int count)
    {
        for (int index = 0; index < count; index++)
        {
            Emit(0x0D, 0x81);
        }
    }

    /// <summary>
    /// Overrides what the fresh pre-ownership read reports, so a test can make the cached
    /// picture and the live firmware disagree — which is the whole point of reading fresh.
    /// </summary>
    internal ThermalFanModeObservation? FreshFanModeOverride { get; set; }

    internal int FanModeObservations { get; private set; }

    public ThermalFanModeObservation ReadFanModeObservation()
    {
        Operations.Add("ReadFanMode");
        FanModeObservations++;
        if (FreshFanModeOverride is { } forced)
        {
            return forced;
        }

        // Two GET 0x0D82 exchanges, matching the production read exactly.
        RazerExchangeTrace zone1 = Emit(0x0D, 0x82);
        RazerExchangeTrace zone2 = Emit(0x0D, 0x82);
        return new ThermalFanModeObservation(
            _state.Zone1PerformanceMode,
            _state.Zone1FanMode,
            _state.Zone2PerformanceMode,
            _state.Zone2FanMode,
            [zone1, zone2]);
    }

    public RuntimeRazerModeState ReadModeState()
    {
        ModeReads++;
        RazerExchangeTrace first = Emit(0x0D, 0x82);
        RazerExchangeTrace second = Emit(0x0D, 0x82);
        RuntimeRazerModeState state = ModeState;
        return state with { Exchanges = [first, second] };
    }

    public ThermalMachineState CaptureState()
    {
        Operations.Add("Capture");
        EmitExchanges(6);
        return _state;
    }

    public ThermalControlOperationResult EnterManualBaseline(FanRpm baseline)
    {
        Operations.Add($"Enter {baseline.Value}");
        FanWrites++;
        if (!FanApplySucceeds)
        {
            // The SET was attempted and firmware refused it: state is unchanged and this is a
            // genuine control failure, not an unmet prerequisite.
            return Result(false, _state, "injected fan apply failure");
        }

        _state = Machine(
            RazerPerformanceMode.Balanced,
            RazerFanMode.Manual,
            _state.CpuLevel,
            _state.GpuLevel,
            baseline.Value);
        return Result(true, _state);
    }

    /// <summary>
    /// How old the post-write ownership observation claims to be. Zero models the real case,
    /// where the read happened microseconds ago.
    /// </summary>
    internal TimeSpan OwnershipObservationAge { get; set; } = TimeSpan.Zero;

    /// <summary>Replaces what the write reports observing, to model firmware moving.</summary>
    internal RazerOwnershipObservation? OwnershipObservationOverride { get; set; }

    public ThermalControlOperationResult SetBothFans(FanRpm target)
    {
        Operations.Add($"Set {target.Value}");
        FanWrites++;
        _state = _state with
        {
            FirmwareReportedFan1Rpm = target.Value,
            FirmwareReportedFan2Rpm = target.Value
        };

        // A real scoped write reads ownership last and reports no full machine state.
        return Result(true, _state) with
        {
            FinalState = null,
            Ownership = OwnershipObservationOverride ?? new RazerOwnershipObservation(
                _state.Zone1PerformanceMode,
                _state.Zone1FanMode,
                _state.Zone2PerformanceMode,
                _state.Zone2FanMode,
                Stopwatch.GetTimestamp() -
                    (long)(OwnershipObservationAge.TotalSeconds * Stopwatch.Frequency),
                [])
        };
    }

    public ThermalControlOperationResult ReturnToFirmwareAuto()
    {
        Operations.Add("Auto");
        AutoAttempts++;
        if (!AutoSucceeds)
        {
            return Result(false, _state, "injected Auto failure");
        }

        // Preserves the performance mode, as the real path now does: handing the fans back to
        // firmware is not an occasion to change the user's mode. A fake that forced Balanced
        // here would let every recovery test pass whether or not the code under test preserved
        // anything.
        _state = Machine(
            _state.Zone1PerformanceMode,
            RazerFanMode.Auto,
            _state.CpuLevel,
            _state.GpuLevel,
            _state.FirmwareReportedFan1Rpm);
        return Result(true, _state);
    }

    public ThermalControlOperationResult RestorePerformance(ThermalMachineState originalState)
    {
        Operations.Add("Restore");
        if (!RestoreSucceeds)
        {
            return Result(false, _state, "injected restore failure");
        }

        _state = originalState;
        return Result(true, _state);
    }

    public PerformanceState GetPerformanceState() => CreatePerformanceState();

    public PerformanceApplyResult ApplyPerformanceProfile(PerformanceProfile profile)
    {
        PerformanceApplies++;
        LastPerformanceProfile = profile;
        PerformanceState state = CreatePerformanceState();
        return new PerformanceApplyResult(
            state,
            profile,
            new PerformanceApplyPlan([]),
            [],
            state,
            new PerformanceVerificationResult(true, "fake applied"),
            PerformanceApplyOutcome.Applied,
            null);
    }

    public FanControlState GetFanState() => new(CreatePerformanceState());

    public FanControlApplyResult ApplyFanProfile(FanControlProfile profile) =>
        ApplyFan(profile);

    private FanControlApplyResult ApplyFan(FanControlProfile profile)
    {
        FanControlState initial = new(CreatePerformanceState());
        if (!FanApplySucceeds)
        {
            // Firmware refused the write and the state is unchanged: a real control failure,
            // as distinct from a prerequisite that was simply not met.
            return new FanControlApplyResult(
                initial,
                profile,
                new FanControlPlan([]),
                [],
                initial,
                [],
                new FanControlVerification(false, "injected fan apply failure"),
                FanControlApplyOutcome.Failed,
                null);
        }

        if (profile.IsFixed)
        {
            _state = Machine(
                RazerPerformanceMode.Balanced,
                RazerFanMode.Manual,
                _state.CpuLevel,
                _state.GpuLevel,
                profile.Fan1Rpm!.Value.Value) with
            {
                FirmwareReportedFan2Rpm = profile.Fan2Rpm!.Value.Value
            };
        }
        else
        {
            _state = Machine(
                RazerPerformanceMode.Balanced,
                RazerFanMode.Auto,
                _state.CpuLevel,
                _state.GpuLevel,
                _state.FirmwareReportedFan1Rpm);
        }

        FanControlState final = new(CreatePerformanceState());
        return new FanControlApplyResult(
            initial,
            profile,
            new FanControlPlan([]),
            [],
            final,
            [],
            new FanControlVerification(true, "fake applied"),
            FanControlApplyOutcome.Applied,
            null);
    }

    private PerformanceState CreatePerformanceState()
    {
        RazerFanReading fan1 = new(RazerZone.Zone1, _state.FirmwareReportedFan1Rpm, Trace());
        RazerFanReading fan2 = new(RazerZone.Zone2, _state.FirmwareReportedFan2Rpm, Trace());
        RazerModeReading mode1 = new(
            RazerZone.Zone1,
            _state.Zone1PerformanceMode,
            _state.Zone1FanMode,
            Trace());
        RazerModeReading mode2 = new(
            RazerZone.Zone2,
            _state.Zone2PerformanceMode,
            _state.Zone2FanMode,
            Trace());
        return new PerformanceState(new RazerStatusSnapshot(
            Device,
            fan1,
            fan2,
            mode1,
            mode2,
            _state.CpuLevel,
            _state.GpuLevel,
            Trace(),
            Trace()));
    }

    private RazerExchangeTrace Emit(byte commandClass, byte commandId)
    {
        RazerExchangeTrace trace = Trace(commandClass, commandId);
        ExchangeCompleted?.Invoke(trace);
        return trace;
    }

    private RazerExchangeTrace Trace(byte commandClass = 0x0D, byte commandId = 0x82)
    {
        _transactionId = _transactionId == 0xFF ? (byte)1 : (byte)(_transactionId + 1);
        return new RazerExchangeTrace(
            _transactionId,
            commandClass,
            commandId,
            new byte[91],
            new byte[91]);
    }

    private static ThermalControlOperationResult Result(
        bool success,
        ThermalMachineState state,
        string message = "ok") => new(
            success,
            true,
            false,
            state.IsBalancedAuto,
            message,
            state,
            []);

    internal static ThermalMachineState Machine(
        RazerPerformanceMode performance,
        RazerFanMode fan,
        RazerCpuPerformanceLevel cpu,
        RazerGpuPerformanceLevel gpu,
        int rpm = 3000) => new(
            Device,
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

internal sealed class SharedTestOwnershipGate : IRuntimeOwnershipGate
{
    private bool _leased;

    public IRuntimeOwnershipLease? TryAcquire()
    {
        if (_leased)
        {
            return null;
        }

        _leased = true;
        return new Lease(this);
    }

    public void Dispose()
    {
    }

    private sealed class Lease : IRuntimeOwnershipLease
    {
        private SharedTestOwnershipGate? _owner;

        internal Lease(SharedTestOwnershipGate owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            SharedTestOwnershipGate? owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
            {
                owner._leased = false;
            }
        }
    }
}
