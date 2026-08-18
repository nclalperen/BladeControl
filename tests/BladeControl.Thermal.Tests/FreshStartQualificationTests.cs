using BladeControl.Razer;
using BladeControl.Telemetry;
using BladeControl.Thermal;

namespace BladeControl.Thermal.Tests;

/// <summary>
/// The Auto prerequisite for taking thermal ownership must be decided on a fresh firmware
/// read taken immediately before acquisition — never on a cached or historical picture.
/// </summary>
/// <remarks>
/// <para>Field incident: Start Dynamic was rejected with "Thermal control must start from a
/// consistent Auto fan mode", yet firmware reads taken moments later reported zone 1 and
/// zone 2 both Custom / Auto, zones agreeing, known Auto, and thermal ownership ready. No SET
/// was sent, and the runtime nevertheless went to Faulted, which required a service restart to
/// clear.</para>
/// <para>Two things follow. The decision must rest on a live observation adjacent to the
/// transition, and it must be the cheap one — two GET 0x0D82 exchanges. Fan RPM (0x0D81) says
/// nothing about who owns the fans and must not gate ownership.</para>
/// </remarks>
[TestClass]
public sealed class FreshStartQualificationTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 18, 21, 40, 0, TimeSpan.Zero);

    // --- The decision comes from the fresh read, not the captured snapshot -----------------

    /// <summary>
    /// The decisive case: the six-GET capture says Manual, the fresh two-GET observation says
    /// Auto — and the start is allowed.
    /// </summary>
    /// <remarks>
    /// This fails against an implementation with two Auto gates in series. The capture used to
    /// hold a veto through CreateRestorationProfile's IsAuto check, which ran before the fresh
    /// read and so decided ownership from the wrong instrument. The capture answers "what
    /// should be restored later"; only the fresh read answers "may ownership be taken now".
    /// </remarks>
    [TestMethod]
    public void CapturedManualDoesNotBlockAStartWhenTheFreshObservationSaysAuto()
    {
        var control = new ScriptedControlDevice
        {
            CapturedFanMode = RazerFanMode.Manual,
            FreshFanMode = RazerFanMode.Auto
        };
        ThermalRuntimeController runtime = NewRuntime(control);

        runtime.Start();

        Assert.AreEqual(
            ThermalControllerStateKind.Manual,
            runtime.State,
            "The fresh observation authorised the start; the capture has no veto.");
        Assert.AreEqual(1, control.FanModeObservations);
    }

    [TestMethod]
    public void StaleAutoSnapshotDoesNotAuthoriseAStartWhenFirmwareIsNowManual()
    {
        // The dangerous inverse: something else took Manual after the snapshot. Taking
        // ownership on the strength of the stale value would fight it for the controller.
        var control = new ScriptedControlDevice
        {
            CapturedFanMode = RazerFanMode.Auto,
            FreshFanMode = RazerFanMode.Manual
        };
        ThermalRuntimeController runtime = NewRuntime(control);

        ThermalPreflightException rejected =
            Assert.ThrowsException<ThermalPreflightException>(runtime.Start);

        StringAssert.Contains(rejected.Message, "both zones in Auto");
        StringAssert.Contains(rejected.Message, "No SET was sent.");
        Assert.AreEqual(0, control.WriteOperations, "No SET may precede a failed qualification.");
    }

    [TestMethod]
    public void FreshZoneDisagreementRejectsEvenWhenTheSnapshotAgreed()
    {
        var control = new ScriptedControlDevice
        {
            CapturedFanMode = RazerFanMode.Auto,
            FreshFanMode = RazerFanMode.Auto,
            FreshZone2FanMode = RazerFanMode.Manual
        };
        ThermalRuntimeController runtime = NewRuntime(control);

        ThermalPreflightException rejected =
            Assert.ThrowsException<ThermalPreflightException>(runtime.Start);

        StringAssert.Contains(rejected.Message, "same mode");
        Assert.AreEqual(0, control.WriteOperations);
    }

    /// <summary>
    /// The rejection names the zone modes the fresh observation actually saw. Had the original
    /// message done this, the field incident would have explained itself instead of needing a
    /// source trace.
    /// </summary>
    [TestMethod]
    public void RejectionMessageIdentifiesTheFreshObservedZoneModes()
    {
        var control = new ScriptedControlDevice
        {
            // Capture disagrees with the live read on purpose: the message must quote the
            // live read, since that is what the decision was made on.
            CapturedFanMode = RazerFanMode.Auto,
            FreshFanMode = RazerFanMode.Manual,
            FreshPerformanceMode = RazerPerformanceMode.Custom
        };
        ThermalRuntimeController runtime = NewRuntime(control);

        ThermalPreflightException rejected =
            Assert.ThrowsException<ThermalPreflightException>(runtime.Start);

        StringAssert.Contains(rejected.Message, "zone 1 Custom / Manual");
        StringAssert.Contains(rejected.Message, "zone 2 Custom / Manual");
        StringAssert.Contains(rejected.Message, "No SET was sent.");
    }

    /// <summary>
    /// Restoration can still refuse, but only on performance grounds — never on fan mode, or
    /// it would be a second gate wearing a different name.
    /// </summary>
    [TestMethod]
    public void RestorationProfileNoLongerInspectsFanMode()
    {
        ThermalMachineState capturedInManual = new(
            new RazerDeviceInfo("fake", 0x1532, 0x029F, 0, 0, 91),
            RazerPerformanceMode.Balanced,
            RazerPerformanceMode.Balanced,
            RazerFanMode.Manual,
            RazerFanMode.Manual,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low,
            3000,
            3000,
            []);

        // Would have thrown "not a consistent Auto-mode state" before.
        PerformanceProfile profile =
            RazerThermalControlDevice.CreateRestorationProfile(capturedInManual);

        Assert.IsNotNull(profile);
    }

    [TestMethod]
    public void RestorationProfileStillRefusesZonesThatDisagreeOnPerformanceMode()
    {
        ThermalMachineState mixed = new(
            new RazerDeviceInfo("fake", 0x1532, 0x029F, 0, 0, 91),
            RazerPerformanceMode.Balanced,
            RazerPerformanceMode.Silent,
            RazerFanMode.Auto,
            RazerFanMode.Auto,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low,
            3000,
            3000,
            []);

        // The profile is built from zone 1 alone, so disagreeing zones cannot be restored
        // coherently. That is a restoration-data concern, not a fan-ownership one.
        InvalidOperationException refused = Assert.ThrowsException<InvalidOperationException>(
            () => RazerThermalControlDevice.CreateRestorationProfile(mixed));

        StringAssert.Contains(refused.Message, "performance modes");
    }

    // --- The read is minimal and correctly placed -------------------------------------------

    [TestMethod]
    public void QualificationUsesExactlyTwoModeReadsAndNoFanRpmRead()
    {
        var control = new ScriptedControlDevice
        {
            CapturedFanMode = RazerFanMode.Auto,
            FreshFanMode = RazerFanMode.Auto
        };
        ThermalRuntimeController runtime = NewRuntime(control);

        runtime.Start();

        Assert.AreEqual(
            1,
            control.FanModeObservations,
            "One observation, which is two GET 0x0D82 exchanges — not a six-command snapshot.");
        Assert.AreEqual(
            1,
            control.CaptureCalls,
            "Capture stays a single call for restoration data; it is not the gate.");
    }

    [TestMethod]
    public void FreshReadHappensImmediatelyBeforeTheFirstSet()
    {
        var control = new ScriptedControlDevice
        {
            CapturedFanMode = RazerFanMode.Auto,
            FreshFanMode = RazerFanMode.Auto
        };
        ThermalRuntimeController runtime = NewRuntime(control);

        runtime.Start();

        int observation = control.Operations.IndexOf("ReadFanMode");
        int firstSet = control.Operations.FindIndex(entry => entry.StartsWith("Enter", StringComparison.Ordinal));
        Assert.IsTrue(observation >= 0 && firstSet > observation);
        Assert.AreEqual(
            firstSet - 1,
            observation,
            "No further firmware interaction may sit between the qualifying read and the " +
            "transition it authorises.");
        Assert.AreEqual(
            control.Operations.Count - 1,
            firstSet,
            "The SET is the last thing the start sequence does.");
    }

    [TestMethod]
    public void AFailedFreshReadRejectsSafelyWithoutWriting()
    {
        var control = new ScriptedControlDevice
        {
            CapturedFanMode = RazerFanMode.Auto,
            ObservationThrows = true
        };
        ThermalRuntimeController runtime = NewRuntime(control);

        ThermalPreflightException rejected =
            Assert.ThrowsException<ThermalPreflightException>(runtime.Start);

        StringAssert.Contains(rejected.Message, "could not be read");
        StringAssert.Contains(rejected.Message, "No SET was sent.");
        Assert.AreEqual(0, control.WriteOperations);
    }

    [TestMethod]
    public void BothZonesAutoAllowsTheExistingStartSequence()
    {
        var control = new ScriptedControlDevice
        {
            CapturedFanMode = RazerFanMode.Auto,
            FreshFanMode = RazerFanMode.Auto
        };
        ThermalRuntimeController runtime = NewRuntime(control);

        runtime.Start();

        Assert.AreEqual(ThermalControllerStateKind.Manual, runtime.State);
        CollectionAssert.AreEqual(
            new[] { "Capture", "ReadFanMode", "Enter 3000" },
            control.Operations);
    }

    // --- Restoration validation precedes the gate and costs no ownership read ---------------

    /// <summary>
    /// Invalid restoration data is rejected before the ownership read is spent, and without a
    /// single SET.
    /// </summary>
    /// <remarks>
    /// Ordering matters twice over. Validating the capture first means a machine that cannot
    /// be restored never pays for the two-GET ownership read; and it keeps the gate as the
    /// last thing before the transition, so nothing can reject between deciding and acting.
    /// </remarks>
    [TestMethod]
    public void CapturedPerformanceDisagreementRejectsBeforeAnyOwnershipRead()
    {
        var control = new ScriptedControlDevice
        {
            CapturedFanMode = RazerFanMode.Auto,
            CapturedZone2PerformanceMode = RazerPerformanceMode.Silent,
            FreshFanMode = RazerFanMode.Auto
        };
        ThermalRuntimeController runtime = NewRuntime(control);

        ThermalPreflightException rejected =
            Assert.ThrowsException<ThermalPreflightException>(runtime.Start);

        StringAssert.Contains(rejected.Message, "different performance modes");
        StringAssert.Contains(rejected.Message, "No SET was sent.");
        Assert.AreEqual(0, control.WriteOperations);
        Assert.AreEqual(
            0,
            control.FanModeObservations,
            "Invalid restoration data must not cost an ownership read.");
        CollectionAssert.AreEqual(new[] { "Capture" }, control.Operations);
    }

    /// <summary>
    /// On a valid start the last two operations are the gate and then the transition, with
    /// nothing in between.
    /// </summary>
    [TestMethod]
    public void FinalOperationsBeforeOwnershipAreExactlyTheGateThenTheSet()
    {
        var control = new ScriptedControlDevice
        {
            CapturedFanMode = RazerFanMode.Auto,
            FreshFanMode = RazerFanMode.Auto
        };
        ThermalRuntimeController runtime = NewRuntime(control);

        runtime.Start();

        CollectionAssert.AreEqual(
            new[] { "ReadFanMode", "Enter 3000" },
            control.Operations.TakeLast(2).ToArray(),
            "The gate must be the last meaningful operation before the first SET.");
    }

    private static ThermalRuntimeController NewRuntime(IThermalControlDevice control)
    {
        var clock = new FakeThermalClock(Start);
        return new ThermalRuntimeController(
            new HealthyTelemetryProvider(Start),
            control,
            BuiltInThermalProfiles.Default,
            clock: clock);
    }

    private sealed class FakeThermalClock(DateTimeOffset now) : IThermalClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    /// <summary>Healthy CPU 51 C / GPU 40 C, matching the field machine at the failed start.</summary>
    private sealed class HealthyTelemetryProvider(DateTimeOffset now) : ITelemetryProvider
    {
        public string Name => "fresh-start-qualification-test";

        public TelemetryCapabilities Capabilities { get; } = new();

        public TelemetrySnapshot GetSnapshot() => new(
            now,
            TelemetryMetric<double>.Available(51, now, TelemetrySources.CpuPackageTemperature),
            TelemetryMetric<double>.Available(40, now, TelemetrySources.GpuTemperature));

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Lets the captured snapshot and the fresh observation disagree, which is the only way to
    /// prove which one the decision actually rests on.
    /// </summary>
    private sealed class ScriptedControlDevice : IThermalControlDevice
    {
        internal RazerFanMode CapturedFanMode { get; init; } = RazerFanMode.Auto;

        /// <summary>Makes the captured zones disagree on performance mode.</summary>
        internal RazerPerformanceMode? CapturedZone2PerformanceMode { get; init; }

        internal RazerFanMode FreshFanMode { get; init; } = RazerFanMode.Auto;

        internal RazerFanMode? FreshZone2FanMode { get; init; }

        internal RazerPerformanceMode FreshPerformanceMode { get; init; } =
            RazerPerformanceMode.Balanced;

        internal bool ObservationThrows { get; init; }

        internal List<string> Operations { get; } = [];

        internal int WriteOperations { get; private set; }

        internal int FanModeObservations { get; private set; }

        internal int CaptureCalls { get; private set; }

        public ThermalMachineState CaptureState()
        {
            Operations.Add("Capture");
            CaptureCalls++;
            return new ThermalMachineState(
                new RazerDeviceInfo("fake", 0x1532, 0x029F, 0, 0, 91),
                RazerPerformanceMode.Balanced,
                CapturedZone2PerformanceMode ?? RazerPerformanceMode.Balanced,
                CapturedFanMode,
                CapturedFanMode,
                RazerCpuPerformanceLevel.Medium,
                RazerGpuPerformanceLevel.Low,
                3000,
                3000,
                []);
        }

        public ThermalFanModeObservation ReadFanModeObservation()
        {
            Operations.Add("ReadFanMode");
            FanModeObservations++;
            if (ObservationThrows)
            {
                throw new IOException("firmware read failed");
            }

            return new ThermalFanModeObservation(
                FreshPerformanceMode,
                FreshFanMode,
                FreshPerformanceMode,
                FreshZone2FanMode ?? FreshFanMode,
                []);
        }

        public ThermalControlOperationResult EnterManualBaseline(FanRpm baseline)
        {
            Operations.Add($"Enter {baseline.Value}");
            WriteOperations++;
            return Success(RazerPerformanceMode.Balanced, RazerFanMode.Manual);
        }

        public ThermalControlOperationResult SetBothFans(FanRpm target)
        {
            Operations.Add($"Set {target.Value}");
            WriteOperations++;
            return Success(RazerPerformanceMode.Balanced, RazerFanMode.Manual);
        }

        public ThermalControlOperationResult ReturnToBalancedAuto()
        {
            Operations.Add("Auto");
            WriteOperations++;
            return Success(RazerPerformanceMode.Balanced, RazerFanMode.Auto);
        }

        public ThermalControlOperationResult RestorePerformance(ThermalMachineState originalState)
        {
            Operations.Add("Restore");
            WriteOperations++;
            return Success(RazerPerformanceMode.Balanced, RazerFanMode.Auto);
        }

        private ThermalControlOperationResult Success(
            RazerPerformanceMode performance,
            RazerFanMode fan) => new(
            true,
            WriteOperations > 0,
            false,
            fan == RazerFanMode.Auto,
            "ok",
            new ThermalMachineState(
                new RazerDeviceInfo("fake", 0x1532, 0x029F, 0, 0, 91),
                performance,
                performance,
                fan,
                fan,
                RazerCpuPerformanceLevel.Medium,
                RazerGpuPerformanceLevel.Low,
                3000,
                3000,
                []),
            []);
    }
}
