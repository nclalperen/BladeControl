using BladeControl.Razer.Protocol;

namespace BladeControl.Razer.Tests;

[TestClass]
public sealed class PerformancePacketFactoryTests
{
    [DataTestMethod]
    [DataRow((byte)0x01, (byte)0x00)]
    [DataRow((byte)0x02, (byte)0x00)]
    [DataRow((byte)0x01, (byte)0x04)]
    [DataRow((byte)0x02, (byte)0x04)]
    [DataRow((byte)0x01, (byte)0x05)]
    [DataRow((byte)0x02, (byte)0x05)]
    public void AllModeAutoPacketsAreModeled(byte zone, byte mode)
    {
        RazerPacket packet = RazerCommands.CreateSetPerformanceAndFanMode(
            1,
            (RazerZone)zone,
            new RazerPerformanceMode(mode),
            RazerFanMode.Auto);

        RazerCommands.EnsureProtocolShape(packet);
        RazerCommands.EnsureAllowed(packet);
        CollectionAssert.AreEqual(
            new byte[] { 0x01, zone, mode, 0x00 },
            packet.Arguments[..4].ToArray());
        AssertValidEncoding(packet);
    }

    [DataTestMethod]
    [DataRow((byte)0x00, true)]
    [DataRow((byte)0x01, true)]
    [DataRow((byte)0x02, false)]
    [DataRow((byte)0x03, false)]
    [DataRow((byte)0x04, false)]
    public void AllCpuLevelPacketsAreModeledAndPolicySeparated(
        byte level,
        bool policyAllowed)
    {
        RazerPacket packet = RazerCommands.CreateSetCpuPerformanceLevel(
            1,
            new RazerCpuPerformanceLevel(level));

        RazerCommands.EnsureProtocolShape(packet);
        AssertPolicy(packet, policyAllowed);
        CollectionAssert.AreEqual(
            new byte[] { 0x00, 0x01, level },
            packet.Arguments[..3].ToArray());
        AssertValidEncoding(packet);
    }

    [DataTestMethod]
    [DataRow((byte)0x00, true)]
    [DataRow((byte)0x01, false)]
    [DataRow((byte)0x02, false)]
    public void AllGpuLevelPacketsAreModeledAndPolicySeparated(
        byte level,
        bool policyAllowed)
    {
        RazerPacket packet = RazerCommands.CreateSetGpuPerformanceLevel(
            1,
            new RazerGpuPerformanceLevel(level));

        RazerCommands.EnsureProtocolShape(packet);
        AssertPolicy(packet, policyAllowed);
        CollectionAssert.AreEqual(
            new byte[] { 0x00, 0x02, level },
            packet.Arguments[..3].ToArray());
        AssertValidEncoding(packet);
    }

    [TestMethod]
    public void ManualFanPacketIsModeledButBlockedAtTransportPolicy()
    {
        RazerPacket packet = RazerCommands.CreateSetPerformanceAndFanMode(
            1,
            RazerZone.Zone1,
            RazerPerformanceMode.Custom,
            RazerFanMode.Manual);

        RazerCommands.EnsureProtocolShape(packet);
        Assert.ThrowsException<InvalidOperationException>(
            () => RazerCommands.EnsureAllowed(packet));
        Assert.ThrowsException<InvalidOperationException>(
            () => new RazerTransportRequest(packet));
    }

    private static void AssertPolicy(RazerPacket packet, bool allowed)
    {
        if (allowed)
        {
            RazerCommands.EnsureAllowed(packet);
        }
        else
        {
            Assert.ThrowsException<InvalidOperationException>(
                () => RazerCommands.EnsureAllowed(packet));
            Assert.ThrowsException<InvalidOperationException>(
                () => new RazerTransportRequest(packet));
        }
    }

    private static void AssertValidEncoding(RazerPacket packet)
    {
        byte[] bytes = RazerPacketCodec.Encode(packet);
        Assert.AreEqual(RazerPacketCodec.CalculateChecksum(bytes), bytes[88]);
        Assert.AreEqual(0, bytes[89]);
        Assert.IsTrue(packet.Arguments[packet.DataSize..].ToArray().All(value => value == 0));
    }
}
