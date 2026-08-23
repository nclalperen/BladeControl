using BladeControl.Razer.Protocol;

namespace BladeControl.Razer.Tests;

[TestClass]
public sealed class PerformanceControlTests
{
    /// <summary>
    /// Changing performance mode leaves the fan mode exactly as it was.
    /// </summary>
    /// <remarks>
    /// <para>Performance and cooling are independent. <c>0x0D02</c> carries both in one write, so
    /// changing one means restating the other — and this restated it as Auto unconditionally,
    /// which made every performance change silently hand the fans back to firmware. A user with
    /// a fixed fan target, or a running Dynamic session, lost it by touching a power setting.</para>
    /// <para>It was the exact mirror of the bug fan control had in the other direction, where
    /// taking the fans forced Balanced.</para>
    /// </remarks>
    [DataTestMethod]
    [DataRow((byte)0x00)]
    [DataRow((byte)0x04)]
    [DataRow((byte)0x05)]
    public void ChangingPerformanceModeDoesNotTakeTheFansBack(byte targetMode)
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Balanced,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);

        // The machine is under a manual fan target: BladeControl owns cooling.
        transport.Zone1FanMode = RazerFanMode.Manual;
        transport.Zone2FanMode = RazerFanMode.Manual;
        var client = new RazerClient(transport);

        var mode = new RazerPerformanceMode(targetMode);
        PerformanceProfile profile = targetMode switch
        {
            0x04 => PerformanceProfile.Custom(
                RazerCpuPerformanceLevel.Medium,
                RazerGpuPerformanceLevel.Low),
            0x05 => PerformanceProfile.Silent,
            _ => PerformanceProfile.Balanced
        };

        PerformanceApplyResult result = client.ApplyPerformanceProfile(profile);

        Assert.IsTrue(result.Succeeded, result.Verification.Message);
        Assert.AreEqual(mode, result.FinalState!.Zone1Mode.PerformanceMode);
        Assert.AreEqual(
            RazerFanMode.Manual,
            result.FinalState.Zone1Mode.FanMode,
            "A power-mode change must not take the fans back to firmware.");
        Assert.AreEqual(RazerFanMode.Manual, result.FinalState.Zone2Mode.FanMode);
    }

    /// <summary>Performance control acts while the fans are Manual rather than refusing.</summary>
    /// <remarks>
    /// It used to throw "Performance Control V1 supports Auto only", so the only way to change
    /// power mode during a Dynamic session was to give up cooling first.
    /// </remarks>
    [TestMethod]
    public void PerformanceControlIsUsableWhileBladeControlOwnsTheFans()
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Balanced,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        transport.Zone1FanMode = RazerFanMode.Manual;
        transport.Zone2FanMode = RazerFanMode.Manual;
        var client = new RazerClient(transport);

        PerformanceApplyResult result = client.ApplyPerformanceProfile(
            PerformanceProfile.Silent);

        Assert.IsTrue(result.Succeeded, result.Verification.Message);
        Assert.AreEqual(RazerPerformanceMode.Silent, result.FinalState!.Zone1Mode.PerformanceMode);
    }

    [TestMethod]
    public void AlreadyAtTargetProducesNoSetAndVerifiedNoOp()
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Custom,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        var client = new RazerClient(transport);

        PerformanceApplyResult result = client.ApplyPerformanceProfile(
            PerformanceProfile.Custom(
                RazerCpuPerformanceLevel.Medium,
                RazerGpuPerformanceLevel.Low));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(PerformanceApplyOutcome.NoChangesRequired, result.Outcome);
        Assert.IsTrue(result.Plan.IsNoOp);
        Assert.AreEqual(0, transport.WriteCount);
    }

    [TestMethod]
    public void CustomMediumLowToCustomLowLowWritesOnlyCpu()
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Custom,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        var client = new RazerClient(transport);

        PerformanceApplyResult result = client.ApplyPerformanceProfile(
            PerformanceProfile.Custom(
                RazerCpuPerformanceLevel.Low,
                RazerGpuPerformanceLevel.Low));

        Assert.IsTrue(result.Succeeded);
        AssertWriteSequence(transport, (0x07, 0x01, 0x00));
    }

    [TestMethod]
    public void CustomToBalancedUsesOrderedTwoZoneWritesOnly()
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Custom,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        var client = new RazerClient(transport);

        PerformanceApplyResult result = client.ApplyPerformanceProfile(
            PerformanceProfile.Balanced);

        Assert.IsTrue(result.Succeeded);
        AssertWriteSequence(
            transport,
            (0x02, 0x01, 0x00),
            (0x02, 0x02, 0x00));
    }

    [TestMethod]
    public void BalancedToSilentUsesOrderedTwoZoneWritesOnly()
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Balanced,
            RazerCpuPerformanceLevel.Low,
            RazerGpuPerformanceLevel.Low);
        var client = new RazerClient(transport);

        PerformanceApplyResult result = client.ApplyPerformanceProfile(
            PerformanceProfile.Silent);

        Assert.IsTrue(result.Succeeded);
        AssertWriteSequence(
            transport,
            (0x02, 0x01, 0x05),
            (0x02, 0x02, 0x05));
    }

    [TestMethod]
    public void SilentToCustomOrdersModesBeforeCpuAndGpu()
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Silent,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Medium);
        var client = new RazerClient(transport);

        PerformanceApplyResult result = client.ApplyPerformanceProfile(
            PerformanceProfile.Custom(
                RazerCpuPerformanceLevel.Low,
                RazerGpuPerformanceLevel.Low));

        Assert.IsTrue(result.Succeeded);
        AssertWriteSequence(
            transport,
            (0x02, 0x01, 0x04),
            (0x02, 0x02, 0x04),
            (0x07, 0x01, 0x00),
            (0x07, 0x02, 0x00));
    }

    [TestMethod]
    public void ZoneTwoIsNotSentAfterZoneOneFailureAndNoRetryOccurs()
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Custom,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        transport.FailWriteNumbers.Add(1);
        var client = new RazerClient(transport);

        PerformanceApplyResult result = client.ApplyPerformanceProfile(
            PerformanceProfile.Balanced);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(1, transport.WriteCount);
        Assert.AreEqual(
            PerformanceApplyOperationKind.SetModeZone1,
            result.FailedOperation!.Operation.Kind);
    }

    [TestMethod]
    public void GpuIsNotSentAfterCpuFailure()
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Custom,
            RazerCpuPerformanceLevel.Low,
            RazerGpuPerformanceLevel.Medium);
        transport.FailWriteNumbers.Add(1);
        var client = new RazerClient(transport);

        PerformanceApplyResult result = client.ApplyPerformanceProfile(
            PerformanceProfile.Custom(
                RazerCpuPerformanceLevel.Medium,
                RazerGpuPerformanceLevel.Low));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(1, transport.WriteCount);
        Assert.AreEqual(
            PerformanceApplyOperationKind.SetCpuLevel,
            result.FailedOperation!.Operation.Kind);
    }

    [TestMethod]
    public void ZoneDisagreementStopsBeforeAnySet()
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Custom,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        transport.Zone2Mode = RazerPerformanceMode.Silent;
        var client = new RazerClient(transport);

        Assert.ThrowsException<PerformanceStateException>(
            () => client.ApplyPerformanceProfile(PerformanceProfile.Balanced));
        Assert.AreEqual(0, transport.WriteCount);
    }

    [TestMethod]
    public void CorruptChecksumIsRejectedAndRestorationIsBounded()
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Custom,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        transport.CorruptWriteResponseNumbers.Add(1);
        var client = new RazerClient(transport);

        PerformanceApplyResult result = client.ApplyPerformanceProfile(
            PerformanceProfile.Balanced);

        Assert.AreEqual(PerformanceApplyOutcome.Restored, result.Outcome);
        StringAssert.Contains(
            result.FailedOperation!.FailureReason!,
            "checksum mismatch");
        Assert.AreEqual(3, transport.WriteCount);
        Assert.IsTrue(result.Restoration!.Succeeded);
    }

    [TestMethod]
    public void WrongResponseEchoIsRejected()
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Custom,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        transport.WrongEchoWriteNumbers.Add(1);
        var client = new RazerClient(transport);

        PerformanceApplyResult result = client.ApplyPerformanceProfile(
            PerformanceProfile.Balanced);

        StringAssert.Contains(
            result.FailedOperation!.FailureReason!,
            "response argument echo");
        Assert.AreEqual(PerformanceApplyOutcome.Restored, result.Outcome);
    }

    [TestMethod]
    public void PostReadVerificationMismatchIsExplicit()
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Custom,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        transport.IgnoreWriteNumbers.UnionWith([1, 2]);
        var client = new RazerClient(transport);

        PerformanceApplyResult result = client.ApplyPerformanceProfile(
            PerformanceProfile.Balanced);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.Verification.Succeeded);
        StringAssert.Contains(result.Verification.Message, "Verification mismatch");
        Assert.AreEqual(PerformanceApplyOutcome.Restored, result.Outcome);
        Assert.AreEqual(2, transport.WriteCount);
    }

    [TestMethod]
    public void FirmwareFailureStatusStopsWithoutRetry()
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Custom,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        transport.FailWriteNumbers.Add(1);
        var client = new RazerClient(transport);

        PerformanceApplyResult result = client.ApplyPerformanceProfile(
            PerformanceProfile.Silent);

        StringAssert.Contains(result.FailedOperation!.FailureReason!, "Failure");
        Assert.AreEqual(1, transport.WriteCount);
    }

    [DataTestMethod]
    [DataRow((byte)0x02)]
    [DataRow((byte)0x03)]
    [DataRow((byte)0x04)]
    public void UnverifiedCpuLevelsAreRejectedBeforeTransport(byte level)
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Custom,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        var client = new RazerClient(transport);

        Assert.ThrowsException<PerformanceCapabilityException>(() =>
            client.ApplyPerformanceProfile(PerformanceProfile.Custom(
                new RazerCpuPerformanceLevel(level),
                RazerGpuPerformanceLevel.Low)));
        Assert.AreEqual(0, transport.Requests.Count);
    }

    [DataTestMethod]
    [DataRow((byte)0x01)]
    [DataRow((byte)0x02)]
    public void UnverifiedGpuLevelsAreRejectedBeforeTransport(byte level)
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Custom,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        var client = new RazerClient(transport);

        Assert.ThrowsException<PerformanceCapabilityException>(() =>
            client.ApplyPerformanceProfile(PerformanceProfile.Custom(
                RazerCpuPerformanceLevel.Medium,
                new RazerGpuPerformanceLevel(level))));
        Assert.AreEqual(0, transport.Requests.Count);
    }

    [TestMethod]
    public void PartialApplyPerformsOneBoundedRestoration()
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Custom,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        transport.FailWriteNumbers.Add(2);
        var client = new RazerClient(transport);

        PerformanceApplyResult result = client.ApplyPerformanceProfile(
            PerformanceProfile.Balanced);

        Assert.AreEqual(PerformanceApplyOutcome.Restored, result.Outcome);
        Assert.IsTrue(result.Restoration!.Succeeded);
        Assert.AreEqual(4, transport.WriteCount);
        Assert.AreEqual(RazerPerformanceMode.Custom, transport.Zone1Mode);
        Assert.AreEqual(RazerPerformanceMode.Custom, transport.Zone2Mode);
    }

    [TestMethod]
    public void RestorationFailureIsReportedAndNeverRetried()
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Custom,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        transport.FailWriteNumbers.UnionWith([2, 3]);
        var client = new RazerClient(transport);

        PerformanceApplyResult result = client.ApplyPerformanceProfile(
            PerformanceProfile.Balanced);

        Assert.AreEqual(PerformanceApplyOutcome.RestorationFailed, result.Outcome);
        Assert.IsFalse(result.Restoration!.Succeeded);
        StringAssert.Contains(result.Restoration.Message, "RESTORATION FAILED");
        Assert.AreEqual(3, transport.WriteCount);
    }

    [TestMethod]
    public void CompleteSelfTestUsesExpectedOrderedStateTransitions()
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Custom,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        var client = new RazerClient(transport);

        PerformanceSelfTestResult result = client.RunPerformanceSelfTest();

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(4, result.Stages.Count);
        AssertWriteSequence(
            transport,
            (0x07, 0x01, 0x00),
            (0x02, 0x01, 0x00),
            (0x02, 0x02, 0x00),
            (0x02, 0x01, 0x05),
            (0x02, 0x02, 0x05),
            (0x02, 0x01, 0x04),
            (0x02, 0x02, 0x04),
            (0x07, 0x01, 0x01));
    }

    [TestMethod]
    public void SelfTestPreconditionFailureSendsNoSet()
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Balanced,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        var client = new RazerClient(transport);

        Assert.ThrowsException<PerformanceSelfTestPreconditionException>(
            () => client.RunPerformanceSelfTest());
        Assert.AreEqual(0, transport.WriteCount);
    }

    [TestMethod]
    public void SelfTestLaterReadFailureMakesOneRestorationAttempt()
    {
        using var transport = CreateTransport(
            RazerPerformanceMode.Custom,
            RazerCpuPerformanceLevel.Medium,
            RazerGpuPerformanceLevel.Low);
        transport.FailCallNumbers.Add(20);
        var client = new RazerClient(transport);

        PerformanceSelfTestResult result = client.RunPerformanceSelfTest();

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(2, result.Stages.Count);
        Assert.AreEqual(
            PerformanceApplyOutcome.Restored,
            result.Stages[^1].ApplyResult.Outcome);
        Assert.AreEqual(2, transport.WriteCount);
        Assert.AreEqual(RazerCpuPerformanceLevel.Medium, transport.CpuLevel);
    }

    private static StatefulPerformanceTransport CreateTransport(
        RazerPerformanceMode mode,
        RazerCpuPerformanceLevel cpu,
        RazerGpuPerformanceLevel gpu) => new(mode, cpu, gpu);

    private static void AssertWriteSequence(
        StatefulPerformanceTransport transport,
        params (byte Command, byte Selector, byte Value)[] expected)
    {
        (byte Command, byte Selector, byte Value)[] actual = transport.WriteRequests
            .Select(packet =>
                (packet.CommandId, packet.Arguments[1], packet.Arguments[2]))
            .ToArray();
        CollectionAssert.AreEqual(expected, actual);
    }
}
