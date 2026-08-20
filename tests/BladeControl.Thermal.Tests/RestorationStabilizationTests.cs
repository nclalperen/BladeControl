using BladeControl.Razer;
using BladeControl.Telemetry;

namespace BladeControl.Thermal.Tests;

/// <summary>
/// Bounded stabilization of the restoration state before thermal ownership is taken.
/// </summary>
/// <remarks>
/// <para>Field incident: a start was correctly refused because the captured zones reported
/// different performance modes, and a firmware read moments later showed both zones agreeing.
/// The refusal was right; the captured state simply was not persistent.</para>
/// <para>What the software can establish is only that: <b>the restoration state was not stable
/// across the read window</b>. A capture is six sequential GETs with no atomic firmware
/// snapshot behind it, so nothing here can distinguish a brief hardware transition from a read
/// sequence that straddled one, and nothing here claims to.</para>
/// <para>The invariant is therefore stated in terms of what is observable: the state a session
/// promises to put back must be seen twice in a row, symmetric both times. At most three
/// captures, no sleeps, no retry loop — Start is a one-shot ownership transition, and the
/// 500 ms telemetry path never runs any of this.</para>
/// </remarks>
[TestClass]
public sealed class RestorationStabilizationTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 19, 20, 6, 11, TimeSpan.Zero);

    private static readonly ThermalRestorationFingerprint Symmetric = new(
        RazerPerformanceMode.Custom,
        RazerPerformanceMode.Custom,
        RazerCpuPerformanceLevel.Medium,
        RazerGpuPerformanceLevel.Low);

    private static readonly ThermalRestorationFingerprint Asymmetric = Symmetric with
    {
        Zone2PerformanceMode = RazerPerformanceMode.Balanced
    };

    // --- Stable machines settle in two captures ----------------------------------------------

    [TestMethod]
    public void StableSymmetricCapturesAcceptWithoutAThirdRead()
    {
        var control = new ScriptedCaptureDevice(Symmetric, Symmetric);
        ThermalRuntimeController runtime = NewRuntime(control);

        runtime.Start();

        Assert.AreEqual(2, control.CaptureCalls, "A third capture is only for instability.");
        Assert.AreEqual(ThermalControllerStateKind.Manual, runtime.State);
        CollectionAssert.AreEqual(
            new[] { "Capture", "Capture", "ReadFanMode", "Enter 3000" },
            control.Operations);
    }

    [TestMethod]
    public void AcceptedRestorationStateIsTheCorroboratedCapture()
    {
        var control = new ScriptedCaptureDevice(Symmetric, Symmetric);
        ThermalRuntimeController runtime = NewRuntime(control);

        runtime.Start();

        Assert.AreEqual(2, runtime.RestorationCaptures.Count);
        Assert.AreEqual(
            Symmetric,
            runtime.CapturedRestorationState!.RestorationFingerprint);
    }

    // --- Instability resolved by a third capture -----------------------------------------------

    [TestMethod]
    public void AsymmetricFirstCaptureRecoversWhenBAndCAgree()
    {
        var control = new ScriptedCaptureDevice(Asymmetric, Symmetric, Symmetric);
        ThermalRuntimeController runtime = NewRuntime(control);

        runtime.Start();

        Assert.AreEqual(3, control.CaptureCalls);
        Assert.AreEqual(ThermalControllerStateKind.Manual, runtime.State);
        Assert.AreEqual(
            Symmetric,
            runtime.CapturedRestorationState!.RestorationFingerprint,
            "Restoration uses the corroborated B/C state, never the unstable A.");
    }

    /// <summary>
    /// Stabilization is not a zone-disagreement check. A symmetric first capture that is
    /// contradicted by the second is just as unusable.
    /// </summary>
    [TestMethod]
    public void SymmetricButChangingFirstCaptureStillStabilizes()
    {
        ThermalRestorationFingerprint other = Symmetric with
        {
            CpuLevel = RazerCpuPerformanceLevel.Low,
            Zone1PerformanceMode = RazerPerformanceMode.Custom
        };
        var control = new ScriptedCaptureDevice(other, Symmetric, Symmetric);
        ThermalRuntimeController runtime = NewRuntime(control);

        runtime.Start();

        Assert.AreEqual(3, control.CaptureCalls);
        Assert.AreEqual(Symmetric, runtime.CapturedRestorationState!.RestorationFingerprint);
    }

    // --- Refusals ------------------------------------------------------------------------------

    [TestMethod]
    public void NeverStabilizingRefusesAndSendsNothing()
    {
        var control = new ScriptedCaptureDevice(Asymmetric, Symmetric, Asymmetric);
        ThermalRuntimeController runtime = NewRuntime(control);

        ThermalPreflightException rejected =
            Assert.ThrowsException<ThermalPreflightException>(runtime.Start);

        StringAssert.Contains(rejected.Message, "did not stabilize safely");
        StringAssert.Contains(rejected.Message, "No SET was sent.");
        Assert.AreEqual(0, control.WriteOperations);
        Assert.AreEqual(
            0,
            control.FanModeObservations,
            "An unstable state must not cost an ownership read.");
    }

    /// <summary>
    /// Consistency is not sufficiency. Restoration writes one performance mode to both zones,
    /// so a stably asymmetric machine has no coherent restoration however often it is read.
    /// </summary>
    [TestMethod]
    public void StableButAsymmetricIsRefused()
    {
        var control = new ScriptedCaptureDevice(Asymmetric, Asymmetric, Asymmetric);
        ThermalRuntimeController runtime = NewRuntime(control);

        Assert.ThrowsException<ThermalPreflightException>(runtime.Start);

        Assert.AreEqual(3, control.CaptureCalls, "Bounded: it never reads a fourth time.");
        Assert.AreEqual(0, control.WriteOperations);
    }

    [TestMethod]
    public void UnstableThenAsymmetricPairIsRefused()
    {
        ThermalRestorationFingerprint otherAsymmetric = Asymmetric with
        {
            Zone2PerformanceMode = RazerPerformanceMode.Silent
        };
        var control = new ScriptedCaptureDevice(Symmetric, otherAsymmetric, otherAsymmetric);
        ThermalRuntimeController runtime = NewRuntime(control);

        Assert.ThrowsException<ThermalPreflightException>(runtime.Start);
        Assert.AreEqual(0, control.WriteOperations);
    }

    // --- What the fingerprint is made of -------------------------------------------------------

    [TestMethod]
    public void CpuLevelChangeAloneCountsAsInstability()
    {
        var control = new ScriptedCaptureDevice(
            Symmetric with { CpuLevel = RazerCpuPerformanceLevel.Low },
            Symmetric,
            Symmetric);
        ThermalRuntimeController runtime = NewRuntime(control);

        runtime.Start();

        Assert.AreEqual(3, control.CaptureCalls, "The CPU level is restored, so it must settle.");
    }

    [TestMethod]
    public void GpuLevelChangeAloneCountsAsInstability()
    {
        var control = new ScriptedCaptureDevice(
            Symmetric with { GpuLevel = RazerGpuPerformanceLevel.Medium },
            Symmetric,
            Symmetric);
        ThermalRuntimeController runtime = NewRuntime(control);

        runtime.Start();

        Assert.AreEqual(3, control.CaptureCalls);
    }

    /// <summary>
    /// Fan mode is excluded deliberately. It is never restored — the stop path establishes
    /// firmware Auto — so letting it destabilise an otherwise identical pair would refuse
    /// starts over a field the session does not promise to put back.
    /// </summary>
    [TestMethod]
    public void FanModeDifferenceDoesNotDestabilizeTheFingerprint()
    {
        var control = new ScriptedCaptureDevice(Symmetric, Symmetric)
        {
            CaptureFanModes = [RazerFanMode.Manual, RazerFanMode.Auto]
        };
        ThermalRuntimeController runtime = NewRuntime(control);

        runtime.Start();

        Assert.AreEqual(2, control.CaptureCalls, "Fan mode is not part of the fingerprint.");
        Assert.AreEqual(ThermalControllerStateKind.Manual, runtime.State);
    }

    [TestMethod]
    public void FingerprintIgnoresFanModeByConstruction()
    {
        ThermalMachineState auto = State(Symmetric, RazerFanMode.Auto);
        ThermalMachineState manual = State(Symmetric, RazerFanMode.Manual);

        Assert.AreEqual(auto.RestorationFingerprint, manual.RestorationFingerprint);
        Assert.AreNotEqual(auto.Zone1FanMode, manual.Zone1FanMode);
    }

    // --- Ordering around the ownership gate ---------------------------------------------------

    /// <summary>
    /// Stabilization sits entirely before the gate, and nothing separates the gate from the
    /// first SET.
    /// </summary>
    [TestMethod]
    public void OwnershipGateStillRunsLastBeforeTheFirstSet()
    {
        var control = new ScriptedCaptureDevice(Asymmetric, Symmetric, Symmetric);
        ThermalRuntimeController runtime = NewRuntime(control);

        runtime.Start();

        CollectionAssert.AreEqual(
            new[] { "Capture", "Capture", "Capture", "ReadFanMode", "Enter 3000" },
            control.Operations);
    }

    [TestMethod]
    public void FreshManualAfterStabilizationRefusesWithNoWrite()
    {
        var control = new ScriptedCaptureDevice(Symmetric, Symmetric)
        {
            FreshFanMode = RazerFanMode.Manual
        };
        ThermalRuntimeController runtime = NewRuntime(control);

        ThermalPreflightException rejected =
            Assert.ThrowsException<ThermalPreflightException>(runtime.Start);

        StringAssert.Contains(rejected.Message, "No SET was sent.");
        Assert.AreEqual(0, control.WriteOperations);
        Assert.AreEqual(
            1,
            control.FanModeObservations,
            "The gate ran once, after stabilization, and refused.");
    }

    // --- The final observation also proves the restoration state is still current -------------

    /// <summary>
    /// Stabilization establishes what to put back; between then and the first SET the machine
    /// could move to a different performance mode, leaving the session promising to restore a
    /// state that was already stale when it was adopted.
    /// </summary>
    /// <remarks>
    /// Closed with data already in hand: 0x0D82 returns performance mode alongside fan mode,
    /// so the same two GETs that authorise fan ownership also prove the captured performance
    /// mode has not gone stale. No extra read, and no widening of the gate-to-SET window.
    /// </remarks>
    [TestMethod]
    public void FinalObservationMatchingTheStabilizedModeStartsNormally()
    {
        var control = new ScriptedCaptureDevice(Symmetric, Symmetric);
        ThermalRuntimeController runtime = NewRuntime(control);

        runtime.Start();

        Assert.AreEqual(ThermalControllerStateKind.Manual, runtime.State);
        CollectionAssert.AreEqual(
            new[] { "Capture", "Capture", "ReadFanMode", "Enter 3000" },
            control.Operations);
    }

    [TestMethod]
    public void PerformanceModeChangedAfterStabilizationRejects()
    {
        var control = new ScriptedCaptureDevice(Symmetric, Symmetric)
        {
            FreshZone1PerformanceMode = RazerPerformanceMode.Balanced,
            FreshZone2PerformanceMode = RazerPerformanceMode.Balanced
        };
        ThermalRuntimeController runtime = NewRuntime(control);

        ThermalPreflightException rejected =
            Assert.ThrowsException<ThermalPreflightException>(runtime.Start);

        StringAssert.Contains(rejected.Message, "changed after restoration stabilization");
        StringAssert.Contains(rejected.Message, "No SET was sent.");
        Assert.AreEqual(0, control.WriteOperations);
    }

    /// <summary>The check is symmetric: a move in the other direction is refused too.</summary>
    [TestMethod]
    public void StabilizedBalancedWithFreshCustomRejects()
    {
        ThermalRestorationFingerprint balanced = Symmetric with
        {
            Zone1PerformanceMode = RazerPerformanceMode.Balanced,
            Zone2PerformanceMode = RazerPerformanceMode.Balanced
        };
        var control = new ScriptedCaptureDevice(balanced, balanced)
        {
            FreshZone1PerformanceMode = RazerPerformanceMode.Custom,
            FreshZone2PerformanceMode = RazerPerformanceMode.Custom
        };
        ThermalRuntimeController runtime = NewRuntime(control);

        Assert.ThrowsException<ThermalPreflightException>(runtime.Start);
        Assert.AreEqual(0, control.WriteOperations);
    }

    [TestMethod]
    public void StaleRejectionReportsBothStabilizedAndFinalModes()
    {
        var control = new ScriptedCaptureDevice(Symmetric, Symmetric)
        {
            FreshZone1PerformanceMode = RazerPerformanceMode.Balanced,
            FreshZone2PerformanceMode = RazerPerformanceMode.Balanced
        };
        ThermalRuntimeController runtime = NewRuntime(control);

        ThermalPreflightException rejected =
            Assert.ThrowsException<ThermalPreflightException>(runtime.Start);

        StringAssert.Contains(rejected.Message, "Stabilized: Z1 Custom, Z2 Custom");
        StringAssert.Contains(rejected.Message, "Final: Z1 Balanced, Z2 Balanced");
    }

    /// <summary>
    /// The freshness check costs nothing: two captures and one two-GET observation, exactly as
    /// before it existed.
    /// </summary>
    [TestMethod]
    public void FreshnessCheckIntroducesNoAdditionalReads()
    {
        var control = new ScriptedCaptureDevice(Symmetric, Symmetric);
        ThermalRuntimeController runtime = NewRuntime(control);

        runtime.Start();

        Assert.AreEqual(2, control.CaptureCalls, "12 capture GETs.");
        Assert.AreEqual(1, control.FanModeObservations, "2 final 0x0D82 GETs.");
    }

    /// <summary>Nothing separates the final validation from the first SET.</summary>
    [TestMethod]
    public void LastTwoOperationsAreTheGateThenTheBaseline()
    {
        var control = new ScriptedCaptureDevice(Symmetric, Symmetric);
        ThermalRuntimeController runtime = NewRuntime(control);

        runtime.Start();

        CollectionAssert.AreEqual(
            new[] { "ReadFanMode", "Enter 3000" },
            control.Operations.TakeLast(2).ToArray());
    }

    // --- Restoration on stop --------------------------------------------------------------------

    [TestMethod]
    public void StopRestoresTheStabilizedStateAfterEstablishingAuto()
    {
        var control = new ScriptedCaptureDevice(Asymmetric, Symmetric, Symmetric);
        ThermalRuntimeController runtime = NewRuntime(control);
        runtime.Start();

        runtime.Stop();

        int auto = control.Operations.LastIndexOf("Auto");
        int restore = control.Operations.LastIndexOf("Restore");
        Assert.IsTrue(auto >= 0, "Firmware Auto is established first.");
        Assert.IsTrue(restore > auto, "Then the stabilized performance state is restored.");
        Assert.AreEqual(
            Symmetric,
            control.RestoredState!.RestorationFingerprint,
            "The corroborated state is what gets restored, not the unstable first read.");
    }

    /// <summary>The captured fan mode is never used to put the device back into Manual.</summary>
    [TestMethod]
    public void CapturedManualFanModeIsNeverRestored()
    {
        var control = new ScriptedCaptureDevice(Symmetric, Symmetric)
        {
            CaptureFanModes = [RazerFanMode.Manual, RazerFanMode.Manual]
        };
        ThermalRuntimeController runtime = NewRuntime(control);
        runtime.Start();

        runtime.Stop();

        Assert.IsTrue(
            control.Operations.LastIndexOf("Auto") <
                control.Operations.LastIndexOf("Restore"),
            "Auto is established before restoration, so Manual is never re-established.");
        Assert.AreEqual(RazerFanMode.Manual, control.RestoredState!.Zone1FanMode);
        Assert.AreEqual(
            0,
            control.Operations.Count(operation => operation == "Enter 3000") - 1,
            "The only Manual entry was the session's own baseline.");
    }

    private static ThermalRuntimeController NewRuntime(IThermalControlDevice control) =>
        new(
            new StableTelemetryProvider(Start),
            control,
            BuiltInThermalProfiles.Default,
            clock: new FixedClock(Start));

    private static ThermalMachineState State(
        ThermalRestorationFingerprint fingerprint,
        RazerFanMode fanMode) => new(
            new RazerDeviceInfo("fake", 0x1532, 0x029F, 0, 0, 91),
            fingerprint.Zone1PerformanceMode,
            fingerprint.Zone2PerformanceMode,
            fanMode,
            fanMode,
            fingerprint.CpuLevel,
            fingerprint.GpuLevel,
            3000,
            3000,
            []);

    private sealed class FixedClock(DateTimeOffset now) : IThermalClock
    {
        public DateTimeOffset UtcNow { get; } = now;

        public TimeSpan MonotonicNow => TimeSpan.Zero;

        public void Sleep(TimeSpan duration)
        {
        }
    }

    private sealed class StableTelemetryProvider(DateTimeOffset now) : ITelemetryProvider
    {
        public string Name => "stable telemetry";

        public TelemetryCapabilities Capabilities => new();

        public TelemetrySnapshot GetSnapshot() => new(
            now,
            TelemetryMetric<double>.Available(60, now, TelemetrySources.CpuPackageTemperature),
            TelemetryMetric<double>.Available(50, now, TelemetrySources.GpuTemperature));

        public void Dispose()
        {
        }
    }

    /// <summary>Replays a scripted sequence of captures, one per CaptureState call.</summary>
    private sealed class ScriptedCaptureDevice : IThermalControlDevice
    {
        private readonly ThermalRestorationFingerprint[] _captures;

        internal ScriptedCaptureDevice(params ThermalRestorationFingerprint[] captures)
        {
            _captures = captures;
        }

        /// <summary>Per-capture fan modes, so fan mode can vary independently of the fingerprint.</summary>
        internal RazerFanMode[] CaptureFanModes { get; init; } = [];

        internal RazerFanMode FreshFanMode { get; init; } = RazerFanMode.Auto;

        /// <summary>
        /// Performance mode reported by the final 0x0D82 pair. Defaults to the last scripted
        /// capture's zone 1 mode, so a device that never changed agrees with itself.
        /// </summary>
        internal RazerPerformanceMode? FreshZone1PerformanceMode { get; init; }

        internal RazerPerformanceMode? FreshZone2PerformanceMode { get; init; }

        internal List<string> Operations { get; } = [];

        internal int CaptureCalls { get; private set; }

        internal int WriteOperations { get; private set; }

        internal int FanModeObservations { get; private set; }

        internal ThermalMachineState? RestoredState { get; private set; }

        public ThermalMachineState CaptureState()
        {
            Operations.Add("Capture");
            int index = CaptureCalls++;

            // Past the end means the script expected fewer reads than were taken: repeating the
            // last entry keeps the failure about the count rather than an index exception.
            ThermalRestorationFingerprint fingerprint =
                _captures[Math.Min(index, _captures.Length - 1)];
            RazerFanMode fanMode = index < CaptureFanModes.Length
                ? CaptureFanModes[index]
                : RazerFanMode.Auto;
            return State(fingerprint, fanMode);
        }

        public ThermalFanModeObservation ReadFanModeObservation()
        {
            Operations.Add("ReadFanMode");
            FanModeObservations++;
            ThermalRestorationFingerprint last = _captures[^1];
            return new ThermalFanModeObservation(
                FreshZone1PerformanceMode ?? last.Zone1PerformanceMode,
                FreshFanMode,
                FreshZone2PerformanceMode ?? last.Zone2PerformanceMode,
                FreshFanMode,
                []);
        }

        public ThermalControlOperationResult EnterManualBaseline(FanRpm baseline)
        {
            Operations.Add($"Enter {baseline.Value}");
            WriteOperations++;
            return Result(RazerFanMode.Manual);
        }

        public ThermalControlOperationResult SetBothFans(FanRpm target)
        {
            Operations.Add($"Set {target.Value}");
            WriteOperations++;
            return Result(RazerFanMode.Manual);
        }

        public ThermalControlOperationResult ReturnToFirmwareAuto()
        {
            Operations.Add("Auto");
            WriteOperations++;
            return Result(RazerFanMode.Auto);
        }

        public ThermalControlOperationResult RestorePerformance(ThermalMachineState originalState)
        {
            Operations.Add("Restore");
            WriteOperations++;
            RestoredState = originalState;
            return Result(RazerFanMode.Auto);
        }

        /// <summary>
        /// A write's final state is what the runtime verifies against, so it must report the
        /// mode that write establishes — Balanced/Manual for the baseline, Balanced/Auto for
        /// the handback — not the captured performance state.
        /// </summary>
        private static ThermalControlOperationResult Result(RazerFanMode fanMode) => new(
            true,
            true,
            false,
            fanMode == RazerFanMode.Auto,
            "ok",
            State(
                Symmetric with
                {
                    Zone1PerformanceMode = RazerPerformanceMode.Balanced,
                    Zone2PerformanceMode = RazerPerformanceMode.Balanced
                },
                fanMode),
            []);
    }
}
