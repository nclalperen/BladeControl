using BladeControl.Razer.Protocol;

namespace BladeControl.Razer.Tests;

[TestClass]
public sealed class RazerCommandsTests
{
    [DataTestMethod]
    [DataRow(RazerZone.Zone1, (byte)0x01)]
    [DataRow(RazerZone.Zone2, (byte)0x02)]
    public void FanGetFactoryCreatesOnlyExactAllowedShape(
        RazerZone zone,
        byte expectedZone)
    {
        RazerPacket packet = RazerCommands.CreateGetFanRpm(0x2A, zone);

        RazerCommands.EnsureAllowed(packet);
        Assert.AreEqual(0x0D, packet.CommandClass);
        Assert.AreEqual(0x81, packet.CommandId);
        Assert.AreEqual(3, packet.DataSize);
        CollectionAssert.AreEqual(
            new byte[] { 0x00, expectedZone, 0x00 },
            packet.Arguments[..3].ToArray());
        Assert.IsTrue(packet.Arguments[3..].ToArray().All(value => value == 0));
        Assert.AreEqual(0x00, packet.Status);
        Assert.AreEqual(0, packet.RemainingPackets);
        Assert.AreEqual(0x00, packet.ProtocolType);
        Assert.AreEqual(0x00, packet.Crc);
        Assert.AreEqual(0x00, packet.Reserved);
    }

    [DataTestMethod]
    [DataRow(RazerZone.Zone1, (byte)0x01)]
    [DataRow(RazerZone.Zone2, (byte)0x02)]
    public void ModeGetFactoryCreatesOnlyExactAllowedShape(
        RazerZone zone,
        byte expectedZone)
    {
        RazerPacket packet = RazerCommands.CreateGetPerformanceAndFanMode(0x2B, zone);

        RazerCommands.EnsureAllowed(packet);
        Assert.AreEqual(0x0D, packet.CommandClass);
        Assert.AreEqual(0x82, packet.CommandId);
        Assert.AreEqual(4, packet.DataSize);
        CollectionAssert.AreEqual(
            new byte[] { 0x00, expectedZone, 0x00, 0x00 },
            packet.Arguments[..4].ToArray());
        Assert.IsTrue(packet.Arguments[4..].ToArray().All(value => value == 0));
    }

    [DataTestMethod]
    [DataRow((byte)RazerPerformanceCluster.Cpu, (byte)0x01)]
    [DataRow((byte)RazerPerformanceCluster.Gpu, (byte)0x02)]
    public void PerformanceBoostGetFactoryCreatesOnlyExactAllowedShape(
        byte cluster,
        byte expectedCluster)
    {
        RazerPacket packet = RazerCommands.CreateGetPerformanceBoostLevel(
            0x2C,
            (RazerPerformanceCluster)cluster);

        RazerCommands.EnsureAllowed(packet);
        Assert.AreEqual(0x0D, packet.CommandClass);
        Assert.AreEqual(0x87, packet.CommandId);
        Assert.AreEqual(3, packet.DataSize);
        CollectionAssert.AreEqual(
            new byte[] { 0x00, expectedCluster, 0x00 },
            packet.Arguments[..3].ToArray());
        Assert.IsTrue(packet.Arguments[3..].ToArray().All(value => value == 0));
    }

    [DataTestMethod]
    [DataRow(RazerZone.Zone1, (byte)0x01, (byte)0x0F)]
    [DataRow(RazerZone.Zone2, (byte)0x02, (byte)0x0C)]
    public void CustomAutoWriteBackFactoryCreatesOnlyExactAllowedShape(
        RazerZone zone,
        byte expectedZone,
        byte expectedChecksum)
    {
        RazerPacket packet = RazerCommands.CreateCustomAutoModeWriteBack(0x2D, zone);

        RazerCommands.EnsureAllowed(packet);
        Assert.AreEqual(0x0D, packet.CommandClass);
        Assert.AreEqual(0x02, packet.CommandId);
        Assert.AreEqual(4, packet.DataSize);
        CollectionAssert.AreEqual(
            new byte[] { 0x01, expectedZone, 0x04, 0x00 },
            packet.Arguments[..4].ToArray());
        Assert.IsTrue(packet.Arguments[4..].ToArray().All(value => value == 0));
        Assert.AreEqual(expectedChecksum, RazerPacketCodec.Encode(packet)[88]);
    }

    [DataTestMethod]
    [DataRow((byte)RazerPerformanceCluster.Cpu, (byte)0x01, (byte)0x01, (byte)0x09)]
    [DataRow((byte)RazerPerformanceCluster.Gpu, (byte)0x02, (byte)0x00, (byte)0x0B)]
    public void ExpectedPerformanceLevelWriteBackFactoryCreatesOnlyExactAllowedShape(
        byte cluster,
        byte expectedCluster,
        byte expectedLevel,
        byte expectedChecksum)
    {
        RazerPacket packet = RazerCommands.CreateExpectedPerformanceLevelWriteBack(
            0x2E,
            (RazerPerformanceCluster)cluster);

        RazerCommands.EnsureAllowed(packet);
        Assert.AreEqual(0x0D, packet.CommandClass);
        Assert.AreEqual(0x07, packet.CommandId);
        Assert.AreEqual(3, packet.DataSize);
        CollectionAssert.AreEqual(
            new byte[] { 0x00, expectedCluster, expectedLevel },
            packet.Arguments[..3].ToArray());
        Assert.IsTrue(packet.Arguments[3..].ToArray().All(value => value == 0));
        Assert.AreEqual(expectedChecksum, RazerPacketCodec.Encode(packet)[88]);
    }

    [DataTestMethod]
    [DataRow((byte)0x07, (byte)0x0F, (byte)1)]
    [DataRow((byte)0x07, (byte)0x8F, (byte)1)]
    public void ExplicitlyBlockedCommandsAreRejected(
        byte commandClass,
        byte commandId,
        byte dataSize)
    {
        var packet = new RazerPacket(
            status: 0,
            transactionId: 1,
            remainingPackets: 0,
            protocolType: 0,
            dataSize,
            commandClass,
            commandId,
            arguments: new byte[dataSize],
            crc: 0,
            reserved: 0);

        Assert.ThrowsException<InvalidOperationException>(
            () => RazerCommands.EnsureAllowed(packet));
    }

    [DataTestMethod]
    [DataRow((byte)0x00, (byte)0x01, (byte)0x04, (byte)0x00)]
    [DataRow((byte)0x01, (byte)0x03, (byte)0x04, (byte)0x00)]
    // Custom + Manual (0x04, 0x01) and Silent + Manual (0x05, 0x01) were rows here. Both are
    // now permitted, validated on hardware - see
    // PerformancePacketFactoryTests.ManualIsConstructibleWithEveryKnownPerformanceMode. What
    // remains rejected is what was always genuinely wrong: a bad selector, a bad zone, and any
    // unknown mode byte.
    [DataRow((byte)0x01, (byte)0x01, (byte)0x02, (byte)0x01)]
    [DataRow((byte)0x01, (byte)0x01, (byte)0xFF, (byte)0x00)]
    public void EveryNonPolicyModeWriteShapeIsRejected(
        byte selector,
        byte zone,
        byte performanceMode,
        byte fanMode)
    {
        var packet = new RazerPacket(
            status: 0,
            transactionId: 1,
            remainingPackets: 0,
            protocolType: 0,
            dataSize: 4,
            commandClass: 0x0D,
            commandId: 0x02,
            arguments: new byte[] { selector, zone, performanceMode, fanMode },
            crc: 0,
            reserved: 0);

        Assert.ThrowsException<InvalidOperationException>(
            () => RazerCommands.EnsureAllowed(packet));
    }

    [TestMethod]
    public void CustomAutoWriteBackWithNonzeroTrailingArgumentIsRejected()
    {
        var arguments = new byte[RazerPacketCodec.ArgumentLength];
        arguments[0] = 0x01;
        arguments[1] = 0x01;
        arguments[2] = 0x04;
        arguments[4] = 0x01;
        var packet = new RazerPacket(
            status: 0,
            transactionId: 1,
            remainingPackets: 0,
            protocolType: 0,
            dataSize: 4,
            commandClass: 0x0D,
            commandId: 0x02,
            arguments,
            crc: 0,
            reserved: 0);

        Assert.ThrowsException<InvalidOperationException>(
            () => RazerCommands.EnsureAllowed(packet));
    }

    [DataTestMethod]
    [DataRow((byte)0x00, (byte)0x01, (byte)0x02)]
    [DataRow((byte)0x00, (byte)0x01, (byte)0x03)]
    [DataRow((byte)0x00, (byte)0x01, (byte)0x04)]
    [DataRow((byte)0x00, (byte)0x02, (byte)0x01)]
    [DataRow((byte)0x00, (byte)0x02, (byte)0x02)]
    [DataRow((byte)0x00, (byte)0x03, (byte)0x00)]
    [DataRow((byte)0x01, (byte)0x01, (byte)0x01)]
    public void EveryNonExpectedPerformanceLevelWriteBackShapeIsRejected(
        byte selector,
        byte cluster,
        byte level)
    {
        var packet = new RazerPacket(
            status: 0,
            transactionId: 1,
            remainingPackets: 0,
            protocolType: 0,
            dataSize: 3,
            commandClass: 0x0D,
            commandId: 0x07,
            arguments: new byte[] { selector, cluster, level },
            crc: 0,
            reserved: 0);

        Assert.ThrowsException<InvalidOperationException>(
            () => RazerCommands.EnsureAllowed(packet));
    }

    [TestMethod]
    public void PerformanceLevelWriteBackWithNonzeroTrailingArgumentIsRejected()
    {
        var arguments = new byte[RazerPacketCodec.ArgumentLength];
        arguments[1] = 0x01;
        arguments[2] = 0x01;
        arguments[3] = 0x01;
        var packet = new RazerPacket(
            status: 0,
            transactionId: 1,
            remainingPackets: 0,
            protocolType: 0,
            dataSize: 3,
            commandClass: 0x0D,
            commandId: 0x07,
            arguments,
            crc: 0,
            reserved: 0);

        Assert.ThrowsException<InvalidOperationException>(
            () => RazerCommands.EnsureAllowed(packet));
    }

    [TestMethod]
    public void MalformedArgumentsForAllowedCommandAreRejected()
    {
        var packet = new RazerPacket(
            status: 0,
            transactionId: 1,
            remainingPackets: 0,
            protocolType: 0,
            dataSize: 3,
            commandClass: 0x0D,
            commandId: 0x81,
            arguments: new byte[] { 0x01, 0x01, 0x00 },
            crc: 0,
            reserved: 0);

        Assert.ThrowsException<InvalidOperationException>(
            () => RazerCommands.EnsureAllowed(packet));
    }

    [TestMethod]
    public void UnsupportedZoneIsRejectedBeforeTransport()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => RazerCommands.CreateGetFanRpm(1, (RazerZone)3));
    }

    [TestMethod]
    public void UnsupportedPerformanceClusterIsRejectedBeforeTransport()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => RazerCommands.CreateGetPerformanceBoostLevel(
                1,
                (RazerPerformanceCluster)3));
    }

    [TestMethod]
    public void UnsupportedWriteBackPerformanceClusterIsRejectedBeforeTransport()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => RazerCommands.CreateExpectedPerformanceLevelWriteBack(
                1,
                (RazerPerformanceCluster)3));
    }
}
