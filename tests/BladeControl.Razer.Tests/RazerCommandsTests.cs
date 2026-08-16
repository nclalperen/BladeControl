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
    [DataRow((byte)0x0D, (byte)0x01, (byte)3)]
    [DataRow((byte)0x0D, (byte)0x02, (byte)4)]
    [DataRow((byte)0x0D, (byte)0x07, (byte)3)]
    [DataRow((byte)0x07, (byte)0x0F, (byte)1)]
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
}
