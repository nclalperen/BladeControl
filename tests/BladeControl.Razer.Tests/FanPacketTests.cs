using BladeControl.Razer.Protocol;

namespace BladeControl.Razer.Tests;

[TestClass]
public sealed class FanPacketTests
{
    [DataTestMethod]
    [DataRow((byte)0x01, (byte)0x0A)]
    [DataRow((byte)0x02, (byte)0x09)]
    public void BalancedManualPacketsMatchGoldenVectors(byte zone, byte crc)
    {
        RazerPacket packet = RazerCommands.CreateSetPerformanceAndFanMode(
            1,
            (RazerZone)zone,
            RazerPerformanceMode.Balanced,
            RazerFanMode.Manual);

        RazerCommands.EnsureAllowed(packet);
        CollectionAssert.AreEqual(
            new byte[] { 0x01, zone, 0x00, 0x01 },
            packet.Arguments[..4].ToArray());
        AssertPacket(packet, crc);
    }

    [DataTestMethod]
    [DataRow((byte)0x01, 3000, (byte)0x10)]
    [DataRow((byte)0x02, 3000, (byte)0x13)]
    [DataRow((byte)0x01, 4000, (byte)0x26)]
    [DataRow((byte)0x02, 4000, (byte)0x25)]
    [DataRow((byte)0x01, 2000, (byte)0x1A)]
    [DataRow((byte)0x02, 5000, (byte)0x3F)]
    public void FanRpmPacketsMatchTypedFramingAndCrc(
        byte zone,
        int rpm,
        byte crc)
    {
        RazerPacket packet = RazerCommands.CreateSetFanRpm(
            1,
            (RazerZone)zone,
            new FanRpm(rpm));

        RazerCommands.EnsureAllowed(packet);
        CollectionAssert.AreEqual(
            new byte[] { 0x00, zone, checked((byte)(rpm / 100)) },
            packet.Arguments[..3].ToArray());
        AssertPacket(packet, crc);
    }

    [TestMethod]
    public void DefaultRpmAndUnsupportedZoneAreRejectedBeforeTransport()
    {
        Assert.ThrowsException<InvalidOperationException>(
            () => RazerCommands.CreateSetFanRpm(1, RazerZone.Zone1, default));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => RazerCommands.CreateSetFanRpm(1, (RazerZone)3, new FanRpm(3000)));
    }

    private static void AssertPacket(RazerPacket packet, byte expectedCrc)
    {
        byte[] encoded = RazerPacketCodec.Encode(packet);
        Assert.AreEqual(expectedCrc, encoded[88]);
        Assert.AreEqual(RazerPacketCodec.CalculateChecksum(encoded), encoded[88]);
        Assert.AreEqual(0, encoded[89]);
        Assert.IsTrue(packet.Arguments[packet.DataSize..].ToArray().All(value => value == 0));
    }
}
