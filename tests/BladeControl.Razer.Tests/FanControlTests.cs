using BladeControl.Razer.Protocol;

namespace BladeControl.Razer.Tests;

[TestClass]
public sealed class FanControlTests
{
    [TestMethod]
    public void ThermalTargetWritesBothFansExactlyDespiteFanV1Tolerance()
    {
        using var transport = CreateManualTransport(3000, 3000);
        var client = new RazerClient(transport);

        ThermalFanTargetResult result = client.ApplyThermalFanTarget(new FanRpm(3100));

        Assert.IsTrue(result.Succeeded, result.Message);
        AssertWriteSequence(
            transport,
            (0x01, 0x01, 0x1F, 0x00),
            (0x01, 0x02, 0x1F, 0x00));
    }

    /// <summary>
    /// The exchange sequence a target change is allowed to cost, asserted exactly.
    /// </summary>
    /// <remarks>
    /// <para>This path used to call the generic six-GET status helper twice, for sixteen HID
    /// exchanges per change. Ten of those reads fed no predicate. With ~390 ms of a 500 ms
    /// control period already spent acquiring telemetry, that surplus is what produced the
    /// observed cycle overruns.</para>
    /// <para>Asserted as an ordered sequence rather than a count, because the order is itself
    /// load-bearing: ownership is verified last so the observation the operation returns is the
    /// freshest thing in it.</para>
    /// </remarks>
    [TestMethod]
    public void ThermalTargetUsesOnlyTheExchangesItValidates()
    {
        using var transport = CreateManualTransport(3000, 3000);
        var client = new RazerClient(transport);

        ThermalFanTargetResult result = client.ApplyThermalFanTarget(new FanRpm(3100));

        Assert.IsTrue(result.Succeeded, result.Message);
        CollectionAssert.AreEqual(
            new ushort[]
            {
                0x0D82, 0x0D82,   // precondition: ownership only
                0x0D01, 0x0D01,   // the write
                0x0D81, 0x0D81,   // verification: firmware-reported fan state
                0x0D82, 0x0D82    // verification: ownership last, so it is the freshest read
            },
            transport.Requests.Select(Combined).ToArray());
        Assert.AreEqual(8, transport.Requests.Count);
    }

    /// <summary>
    /// CPU and GPU performance levels have no bearing on whether a fan target was written.
    /// </summary>
    [TestMethod]
    public void ThermalTargetNeverReadsPerformanceLevels()
    {
        using var transport = CreateManualTransport(3000, 3000);
        var client = new RazerClient(transport);

        client.ApplyThermalFanTarget(new FanRpm(3100));

        Assert.AreEqual(
            0,
            transport.Requests.Count(packet => Combined(packet) == 0x0D87),
            "0x0D87 is restoration data, collected once at start — not per fan write.");
    }

    /// <summary>
    /// The post-write ownership read is stamped when it happened, so a caller can judge its
    /// age rather than assuming it is current.
    /// </summary>
    [TestMethod]
    public void SuccessfulThermalTargetReturnsAFreshOwnershipObservation()
    {
        using var transport = CreateManualTransport(3000, 3000);
        var client = new RazerClient(transport);

        ThermalFanTargetResult result = client.ApplyThermalFanTarget(new FanRpm(3100));

        Assert.IsNotNull(result.OwnershipAfterWrite);
        Assert.IsTrue(result.OwnershipAfterWrite!.IsBalancedManual);
        Assert.IsTrue(result.OwnershipAfterWrite.ZonesAgree);
        Assert.IsTrue(
            result.OwnershipAfterWrite.Age < TimeSpan.FromSeconds(1),
            "An observation taken during this call cannot already be seconds old.");
    }

    /// <summary>A failed write returns no ownership observation to lean on.</summary>
    [TestMethod]
    public void FailedThermalTargetOffersNoOwnershipObservation()
    {
        using var transport = CreateManualTransport(3000, 3000);
        transport.FailWriteNumbers.Add(1);
        var client = new RazerClient(transport);

        ThermalFanTargetResult result = client.ApplyThermalFanTarget(new FanRpm(3400));

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(
            result.OwnershipAfterWrite,
            "Nothing was verified, so nothing may be offered as a verified observation.");
    }

    /// <summary>Losing ownership across the write fails and hands back to firmware.</summary>
    [TestMethod]
    public void OwnershipLostDuringTheWriteFailsAndRestoresAuto()
    {
        using var transport = CreateManualTransport(3000, 3000);
        var client = new RazerClient(transport);
        transport.AfterRequest = packet =>
        {
            // Something else takes the fans back between the write and its verification.
            if (Combined(packet) == 0x0D01)
            {
                transport.Zone1FanMode = RazerFanMode.Auto;
                transport.Zone2FanMode = RazerFanMode.Auto;
            }
        };

        ThermalFanTargetResult result = client.ApplyThermalFanTarget(new FanRpm(3100));

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Message, "ownership was lost");
        Assert.IsTrue(result.AutoRecoveryAttempted);
        Assert.IsNull(result.OwnershipAfterWrite);
    }

    [TestMethod]
    public void ThermalTargetFailureDoesNotRetryAndAttemptsAutoOnce()
    {
        using var transport = CreateManualTransport(3000, 3000);
        transport.FailWriteNumbers.Add(1);
        var client = new RazerClient(transport);

        ThermalFanTargetResult result = client.ApplyThermalFanTarget(new FanRpm(3400));

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.AutoRecoveryAttempted);
        Assert.AreEqual(1, transport.WriteRequests.Count(IsFanRpmSet), "No retry.");
    }

    [TestMethod]
    public void ThermalTargetExactReadbackMismatchHandsBackToAuto()
    {
        using var transport = CreateManualTransport(3000, 3000);

        // The precondition no longer reads fan state, so the verification is the first and
        // only fan read: one queued value each.
        transport.Fan1ReadSequence.Enqueue(3300);
        transport.Fan2ReadSequence.Enqueue(3400);
        var client = new RazerClient(transport);

        ThermalFanTargetResult result = client.ApplyThermalFanTarget(new FanRpm(3400));

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.AutoRecoveryAttempted);
        StringAssert.Contains(result.Message, "Exact thermal target validation failed");
        Assert.AreEqual(2, transport.WriteRequests.Count(IsFanRpmSet));
    }

    [TestMethod]
    public void ThermalTargetBelow3000IsRejectedBeforeHardwareRead()
    {
        using var transport = CreateManualTransport(3000, 3000);
        var client = new RazerClient(transport);

        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            client.ApplyThermalFanTarget(new FanRpm(2900)));
        Assert.AreEqual(0, transport.Requests.Count);
    }

    [TestMethod]
    public void AutoToFixedOrdersModeThenFanWrites()
    {
        using var transport = CreateAutoTransport(RazerPerformanceMode.Custom);
        var client = new RazerClient(transport);

        FanControlApplyResult result = client.ApplyFanControlProfile(
            FanControlProfile.Fixed(new FanRpm(3000), new FanRpm(3500)));

        Assert.IsTrue(result.Succeeded);
        AssertWriteSequence(
            transport,
            (0x02, 0x01, 0x00, 0x01),
            (0x02, 0x02, 0x00, 0x01),
            (0x01, 0x01, 0x1E, 0x00),
            (0x01, 0x02, 0x23, 0x00));
    }

    [TestMethod]
    public void FixedToAutoWritesOnlyOrderedAutoZones()
    {
        using var transport = CreateManualTransport(3000, 3500);
        var client = new RazerClient(transport);

        FanControlApplyResult result = client.ApplyFanControlProfile(
            FanControlProfile.Auto);

        Assert.IsTrue(result.Succeeded);
        AssertWriteSequence(
            transport,
            (0x02, 0x01, 0x00, 0x00),
            (0x02, 0x02, 0x00, 0x00));
    }

    [TestMethod]
    public void CustomAutoToFanAutoExplicitlyAppliesBalancedAuto()
    {
        using var transport = CreateAutoTransport(RazerPerformanceMode.Custom);
        var client = new RazerClient(transport);

        FanControlApplyResult result = client.ApplyFanControlProfile(
            FanControlProfile.Auto);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(RazerPerformanceMode.Balanced, transport.Zone1Mode);
        Assert.AreEqual(RazerPerformanceMode.Balanced, transport.Zone2Mode);
        AssertWriteSequence(
            transport,
            (0x02, 0x01, 0x00, 0x00),
            (0x02, 0x02, 0x00, 0x00));
    }

    [TestMethod]
    public void BalancedAutoToAutoIsNoOp()
    {
        using var transport = CreateAutoTransport(RazerPerformanceMode.Balanced);
        var client = new RazerClient(transport);

        FanControlApplyResult result = client.ApplyFanControlProfile(
            FanControlProfile.Auto);

        Assert.AreEqual(FanControlApplyOutcome.NoChangesRequired, result.Outcome);
        Assert.AreEqual(0, transport.WriteCount);
    }

    [TestMethod]
    public void FixedToDifferentFixedWritesOnlyRpmInFanOrder()
    {
        using var transport = CreateManualTransport(3000, 3000);
        var client = new RazerClient(transport);

        FanControlApplyResult result = client.ApplyFanControlProfile(
            FanControlProfile.Fixed(new FanRpm(4000), new FanRpm(3500)));

        Assert.IsTrue(result.Succeeded);
        AssertWriteSequence(
            transport,
            (0x01, 0x01, 0x28, 0x00),
            (0x01, 0x02, 0x23, 0x00));
    }

    [TestMethod]
    public void OnlyFanOneDifferenceWritesOnlyFanOne()
    {
        using var transport = CreateManualTransport(3000, 3500);
        var client = new RazerClient(transport);

        FanControlApplyResult result = client.ApplyFanControlProfile(
            FanControlProfile.Fixed(new FanRpm(4000), new FanRpm(3500)));

        Assert.IsTrue(result.Succeeded);
        AssertWriteSequence(transport, (0x01, 0x01, 0x28, 0x00));
    }

    [TestMethod]
    public void OnlyFanTwoDifferenceWritesOnlyFanTwo()
    {
        using var transport = CreateManualTransport(3000, 3500);
        var client = new RazerClient(transport);

        FanControlApplyResult result = client.ApplyFanControlProfile(
            FanControlProfile.Fixed(new FanRpm(3000), new FanRpm(4000)));

        Assert.IsTrue(result.Succeeded);
        AssertWriteSequence(transport, (0x01, 0x02, 0x28, 0x00));
    }

    [TestMethod]
    public void TargetWithinToleranceIsANoOp()
    {
        using var transport = CreateManualTransport(2900, 3100);
        var client = new RazerClient(transport);

        FanControlApplyResult result = client.ApplyFanControlProfile(
            FanControlProfile.Fixed(new FanRpm(3000), new FanRpm(3000)));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(FanControlApplyOutcome.NoChangesRequired, result.Outcome);
        Assert.AreEqual(0, transport.WriteCount);
    }

    [TestMethod]
    public void ZoneOneManualFailurePreventsZoneTwoManualAndRpmWrites()
    {
        using var transport = CreateAutoTransport(RazerPerformanceMode.Custom);
        transport.FailWriteNumbers.Add(1);
        var client = new RazerClient(transport);

        FanControlApplyResult result = client.ApplyFanControlProfile(
            FanControlProfile.Fixed(new FanRpm(3000), new FanRpm(3000)));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(FanControlOperationKind.SetBalancedManualZone1,
            result.FailedOperation!.Operation.Kind);
        Assert.AreEqual(0, transport.WriteRequests.Count(IsManualZone2));
        Assert.AreEqual(0, transport.WriteRequests.Count(IsFanRpmSet));
    }

    [TestMethod]
    public void ZoneTwoManualFailurePreventsAllRpmWrites()
    {
        using var transport = CreateAutoTransport(RazerPerformanceMode.Custom);
        transport.FailWriteNumbers.Add(2);
        var client = new RazerClient(transport);

        FanControlApplyResult result = client.ApplyFanControlProfile(
            FanControlProfile.Fixed(new FanRpm(3000), new FanRpm(3000)));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(FanControlOperationKind.SetBalancedManualZone2,
            result.FailedOperation!.Operation.Kind);
        Assert.AreEqual(0, transport.WriteRequests.Count(IsFanRpmSet));
    }

    [TestMethod]
    public void FanOneFailurePreventsFanTwoAndRestoresAutoOnce()
    {
        using var transport = CreateManualTransport(2000, 2000);
        transport.FailWriteNumbers.Add(1);
        var client = new RazerClient(transport);

        FanControlApplyResult result = client.ApplyFanControlProfile(
            FanControlProfile.Fixed(new FanRpm(3000), new FanRpm(3000)));

        Assert.AreEqual(FanControlApplyOutcome.AutoRestored, result.Outcome);
        Assert.AreEqual(1, transport.WriteRequests.Count(IsFanRpmSet));
        Assert.AreEqual(2, result.AutoRecovery!.Operations.Count);
        Assert.AreEqual(3, transport.WriteCount);
    }

    [TestMethod]
    public void FanTwoFailureTriggersExactlyOneAutoRecovery()
    {
        using var transport = CreateManualTransport(2000, 2000);
        transport.FailWriteNumbers.Add(2);
        var client = new RazerClient(transport);

        FanControlApplyResult result = client.ApplyFanControlProfile(
            FanControlProfile.Fixed(new FanRpm(3000), new FanRpm(3000)));

        Assert.AreEqual(FanControlApplyOutcome.AutoRestored, result.Outcome);
        Assert.AreEqual(2, transport.WriteRequests.Count(IsFanRpmSet));
        Assert.AreEqual(4, transport.WriteCount);
        Assert.IsTrue(transport.Zone1FanMode == RazerFanMode.Auto);
        Assert.IsTrue(transport.Zone2FanMode == RazerFanMode.Auto);
    }

    [TestMethod]
    public void FailedAutoRecoveryStopsAfterFirstFailedRecoveryWrite()
    {
        using var transport = CreateManualTransport(2000, 2000);
        transport.FailWriteNumbers.UnionWith([2, 3]);
        var client = new RazerClient(transport);

        FanControlApplyResult result = client.ApplyFanControlProfile(
            FanControlProfile.Fixed(new FanRpm(3000), new FanRpm(3000)));

        Assert.AreEqual(FanControlApplyOutcome.AutoRestorationFailed, result.Outcome);
        StringAssert.Contains(result.AutoRecovery!.Message, "FAN AUTO RESTORATION FAILED");
        Assert.AreEqual(3, transport.WriteCount);
        Assert.AreEqual(1, result.AutoRecovery.Operations.Count);
    }

    [DataTestMethod]
    [DataRow(1, "returned zone")]
    [DataRow(2, "response argument echo")]
    [DataRow(3, "checksum mismatch")]
    [DataRow(4, "Failure")]
    [DataRow(5, "transaction ID")]
    [DataRow(6, "command ID")]
    [DataRow(7, "data size")]
    public void FanSetResponseValidationRejectsEveryMalformedResponse(
        int failureKind,
        string expectedMessage)
    {
        using var transport = CreateManualTransport(2000, 2000);
        ConfigureWriteFailure(transport, failureKind);
        var client = new RazerClient(transport);

        FanControlApplyResult result = client.ApplyFanControlProfile(
            FanControlProfile.Fixed(new FanRpm(3000), new FanRpm(2000)));

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(
            result.FailedOperation!.FailureReason!,
            expectedMessage);
        Assert.AreEqual(1, transport.WriteRequests.Count(IsFanRpmSet));
    }

    [TestMethod]
    public void ImmediatePhysicalTargetNeedsNoObservationDelay()
    {
        using var transport = CreateManualTransport(2000, 2000);
        var delay = new RecordingFanObservationDelay();
        var client = CreateClient(transport, delay);

        FanControlApplyResult result = client.ApplyFanControlProfile(
            FanControlProfile.Fixed(new FanRpm(3000), new FanRpm(3000)));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0, delay.Waits.Count);
        Assert.AreEqual(0, result.ObservationExchanges.Count);
    }

    [TestMethod]
    public void SettlingReachesTargetsAfterSeveralReadOnlyObservations()
    {
        using var transport = CreateManualTransport(2000, 2000);
        transport.IgnoreWriteNumbers.UnionWith([1, 2]);
        transport.Fan1ReadSequence.Enqueue(2000);
        transport.Fan1ReadSequence.Enqueue(2000);
        transport.Fan1ReadSequence.Enqueue(2500);
        transport.Fan1ReadSequence.Enqueue(2900);
        transport.Fan2ReadSequence.Enqueue(2000);
        transport.Fan2ReadSequence.Enqueue(2000);
        transport.Fan2ReadSequence.Enqueue(2600);
        transport.Fan2ReadSequence.Enqueue(3100);
        var delay = new RecordingFanObservationDelay();
        var client = CreateClient(transport, delay);

        FanControlApplyResult result = client.ApplyFanControlProfile(
            FanControlProfile.Fixed(new FanRpm(3000), new FanRpm(3000)));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(2, delay.Waits.Count);
        Assert.AreEqual(4, result.ObservationExchanges.Count);
        Assert.AreEqual(2, transport.WriteRequests.Count(IsFanRpmSet));
    }

    [TestMethod]
    public void SettlingTimeoutIsBoundedAndNeverRepeatsFanSet()
    {
        using var transport = CreateManualTransport(2000, 2000);
        transport.IgnoreWriteNumbers.UnionWith([1, 2]);
        var delay = new RecordingFanObservationDelay();
        var client = CreateClient(transport, delay);

        FanControlApplyResult result = client.ApplyFanControlProfile(
            FanControlProfile.Fixed(new FanRpm(4000), new FanRpm(4000)));

        Assert.AreEqual(FanControlApplyOutcome.AutoRestored, result.Outcome);
        StringAssert.Contains(result.Verification.Message, "FAN RPM VERIFICATION TIMEOUT");
        Assert.AreEqual(10, delay.Waits.Count);
        Assert.AreEqual(20, result.ObservationExchanges.Count);
        Assert.AreEqual(2, transport.WriteRequests.Count(IsFanRpmSet));
        Assert.IsTrue(result.ObservationExchanges.All(exchange =>
            exchange.CombinedCommand == 0x0D81));
    }

    [TestMethod]
    public void UnsafeCurrentModeCombinationStopsBeforeSet()
    {
        using var transport = CreateAutoTransport(RazerPerformanceMode.Custom);
        transport.Zone1FanMode = RazerFanMode.Manual;
        transport.Zone2FanMode = RazerFanMode.Manual;
        var client = new RazerClient(transport);

        Assert.ThrowsException<FanControlStateException>(() =>
            client.ApplyFanControlProfile(FanControlProfile.Auto));
        Assert.AreEqual(0, transport.WriteCount);
    }

    private static RazerClient CreateClient(
        StatefulPerformanceTransport transport,
        RecordingFanObservationDelay delay) => new(
            transport,
            new SequentialTransactionIdSource(),
            delay);

    private static StatefulPerformanceTransport CreateAutoTransport(
        RazerPerformanceMode mode) => new(
            mode,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);

    private static StatefulPerformanceTransport CreateManualTransport(
        int fan1Rpm,
        int fan2Rpm)
    {
        var transport = new StatefulPerformanceTransport(
            RazerPerformanceMode.Balanced,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low,
            fan1Rpm,
            fan2Rpm)
        {
            Zone1FanMode = RazerFanMode.Manual,
            Zone2FanMode = RazerFanMode.Manual
        };
        return transport;
    }

    private static void ConfigureWriteFailure(
        StatefulPerformanceTransport transport,
        int failureKind)
    {
        switch (failureKind)
        {
            case 1:
                transport.WrongZoneWriteNumbers.Add(1);
                break;
            case 2:
                transport.WrongEchoWriteNumbers.Add(1);
                break;
            case 3:
                transport.CorruptWriteResponseNumbers.Add(1);
                break;
            case 4:
                transport.FailWriteNumbers.Add(1);
                break;
            case 5:
                transport.WrongTransactionWriteNumbers.Add(1);
                break;
            case 6:
                transport.WrongCommandWriteNumbers.Add(1);
                break;
            case 7:
                transport.ShortWriteResponseNumbers.Add(1);
                break;
            default:
                Assert.Fail("Unknown test failure kind.");
                break;
        }
    }

    private static bool IsManualZone2(RazerPacket packet) =>
        packet.CommandId == RazerCommands.WriteBackPerformanceAndFanModeCommandId &&
        packet.Arguments[1] == (byte)RazerZone.Zone2 &&
        packet.Arguments[3] == RazerFanMode.Manual.Value;

    private static ushort Combined(RazerPacket packet) =>
        (ushort)((packet.CommandClass << 8) | packet.CommandId);

    private static bool IsFanRpmSet(RazerPacket packet) =>
        packet.CommandId == RazerCommands.SetFanRpmCommandId;

    private static void AssertWriteSequence(
        StatefulPerformanceTransport transport,
        params (byte Command, byte Selector, byte Value, byte FanMode)[] expected)
    {
        (byte Command, byte Selector, byte Value, byte FanMode)[] actual =
            transport.WriteRequests
                .Select(packet => (
                    packet.CommandId,
                    packet.Arguments[1],
                    packet.Arguments[2],
                    packet.CommandId ==
                        RazerCommands.WriteBackPerformanceAndFanModeCommandId
                            ? packet.Arguments[3]
                            : (byte)0))
                .ToArray();
        CollectionAssert.AreEqual(expected, actual);
    }
}
