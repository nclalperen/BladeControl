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

    /// <summary>
    /// Manual is constructible with every known performance mode, not only Balanced.
    /// </summary>
    /// <remarks>
    /// <para>This test previously asserted the opposite. The restriction it pinned arrived with
    /// the original Fan Control V1 hardware validation as a scope decision — Balanced + Manual
    /// was the one ownership state that had been exercised — and never as a finding that the
    /// controller rejects Manual elsewhere. Nothing was known either way, because the pair had
    /// never been sent.</para>
    /// <para>It has now been sent. On a Razer Blade 16 (RZ09-0483), each of the three known
    /// modes accepted Manual, took a distinct fan target, and still held it four seconds later:
    /// Balanced 3200, Silent 3500, Custom 4100, each moving the target from the previous mode's
    /// value. Neither Silent's nor Custom's own curve reclaimed the fans.</para>
    /// <para>The evidence is about the firmware-reported commanded target from <c>0x0D81</c>,
    /// which is what the controller says it is aiming for — not a tachometer reading, and not
    /// proof of blade speed. It also covers seconds on an idle machine, not sustained behaviour
    /// under load.</para>
    /// </remarks>
    [TestMethod]
    public void ManualIsConstructibleWithEveryKnownPerformanceMode()
    {
        foreach (RazerPerformanceMode mode in new[]
        {
            RazerPerformanceMode.Balanced,
            RazerPerformanceMode.Custom,
            RazerPerformanceMode.Silent
        })
        {
            RazerPacket packet = RazerCommands.CreateSetPerformanceAndFanMode(
                1,
                RazerZone.Zone1,
                mode,
                RazerFanMode.Manual);
            RazerCommands.EnsureProtocolShape(packet);
            RazerCommands.EnsureAllowed(packet);
        }
    }

    /// <summary>An unknown mode is still refused, with Manual as with Auto.</summary>
    [TestMethod]
    public void ManualWithAnUnknownPerformanceModeIsStillRejected()
    {
        foreach (byte unknown in new byte[] { 0x01, 0x02, 0x03, 0x06, 0xFF })
        {
            var packet = new RazerPacket(
                status: 0,
                transactionId: 1,
                remainingPackets: 0,
                protocolType: 0,
                dataSize: 4,
                commandClass: 0x0D,
                commandId: 0x02,
                arguments: new byte[] { 0x01, 0x01, unknown, 0x01 },
                crc: 0,
                reserved: 0);

            Assert.ThrowsException<InvalidOperationException>(
                () => RazerCommands.EnsureProtocolShape(packet),
                $"Performance mode 0x{unknown:X2} is not a known mode.");
        }
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
