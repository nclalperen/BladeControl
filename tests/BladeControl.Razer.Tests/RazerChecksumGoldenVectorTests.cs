using BladeControl.Razer.Protocol;

namespace BladeControl.Razer.Tests;

[TestClass]
public sealed class RazerChecksumGoldenVectorTests
{
    [DataTestMethod]
    [DataRow((byte)0x81, (byte)0x01, (byte)0x01, (byte)0x8E)]
    [DataRow((byte)0x81, (byte)0x02, (byte)0x02, (byte)0x8D)]
    [DataRow((byte)0x82, (byte)0x01, (byte)0x03, (byte)0x8A)]
    [DataRow((byte)0x82, (byte)0x02, (byte)0x04, (byte)0x89)]
    [DataRow((byte)0x87, (byte)0x01, (byte)0x05, (byte)0x88)]
    [DataRow((byte)0x87, (byte)0x02, (byte)0x06, (byte)0x8B)]
    [DataRow((byte)0x02, (byte)0x01, (byte)0x07, (byte)0x0F)]
    [DataRow((byte)0x02, (byte)0x02, (byte)0x08, (byte)0x0C)]
    public void OutgoingRequestsMatchKnownChecksums(
        byte commandId,
        byte selector,
        byte transactionId,
        byte expectedChecksum)
    {
        RazerPacket request = commandId switch
        {
            RazerCommands.WriteBackPerformanceAndFanModeCommandId =>
                RazerCommands.CreateCustomAutoModeWriteBack(
                    transactionId,
                    (RazerZone)selector),
            RazerCommands.GetFanRpmCommandId =>
                RazerCommands.CreateGetFanRpm(transactionId, (RazerZone)selector),
            RazerCommands.GetPerformanceAndFanModeCommandId =>
                RazerCommands.CreateGetPerformanceAndFanMode(
                    transactionId,
                    (RazerZone)selector),
            RazerCommands.GetPerformanceBoostLevelCommandId =>
                RazerCommands.CreateGetPerformanceBoostLevel(
                    transactionId,
                    (RazerPerformanceCluster)selector),
            _ => throw new AssertFailedException($"Unexpected command ID 0x{commandId:X2}.")
        };

        byte[] encoded = RazerPacketCodec.Encode(request);

        Assert.AreEqual(expectedChecksum, encoded[88]);
        Assert.AreEqual(0x00, encoded[89]);
    }

    [TestMethod]
    public void TransactionIdIsOutsideChecksumRange()
    {
        byte[] transactionOne = RazerPacketCodec.Encode(
            RazerCommands.CreateGetFanRpm(0x01, RazerZone.Zone1));
        byte[] transactionFe = RazerPacketCodec.Encode(
            RazerCommands.CreateGetFanRpm(0xFE, RazerZone.Zone1));

        Assert.AreNotEqual(transactionOne[1], transactionFe[1]);
        Assert.AreEqual(0x8E, transactionOne[88]);
        Assert.AreEqual(transactionOne[88], transactionFe[88]);
    }

    [TestMethod]
    public void WriteBackChecksumDoesNotIncludeTransactionId()
    {
        byte[] transactionOne = RazerPacketCodec.Encode(
            RazerCommands.CreateCustomAutoModeWriteBack(0x01, RazerZone.Zone1));
        byte[] transactionFe = RazerPacketCodec.Encode(
            RazerCommands.CreateCustomAutoModeWriteBack(0xFE, RazerZone.Zone1));

        Assert.AreNotEqual(transactionOne[1], transactionFe[1]);
        Assert.AreEqual(0x0F, transactionOne[88]);
        Assert.AreEqual(transactionOne[88], transactionFe[88]);
    }

    [DataTestMethod]
    [DataRow((byte)0x81, RazerZone.Zone1, (byte)0x01, (byte)0x14, (byte)0x00, (byte)0x9A)]
    [DataRow((byte)0x81, RazerZone.Zone2, (byte)0x02, (byte)0x14, (byte)0x00, (byte)0x99)]
    [DataRow((byte)0x82, RazerZone.Zone1, (byte)0x03, (byte)0x04, (byte)0x00, (byte)0x8E)]
    [DataRow((byte)0x82, RazerZone.Zone2, (byte)0x04, (byte)0x04, (byte)0x00, (byte)0x8D)]
    public void CapturedHardwareResponsesMatchKnownChecksums(
        byte commandId,
        RazerZone zone,
        byte transactionId,
        byte payloadValue,
        byte modeValue,
        byte expectedChecksum)
    {
        byte dataSize = commandId == RazerCommands.GetFanRpmCommandId
            ? (byte)3
            : (byte)4;
        var report = new byte[RazerPacketCodec.HidFeatureReportLength];
        Span<byte> packet = report.AsSpan(1);
        packet[0] = (byte)RazerResponseStatus.Success;
        packet[1] = transactionId;
        packet[5] = dataSize;
        packet[6] = RazerCommands.SystemCommandClass;
        packet[7] = commandId;
        packet[9] = (byte)zone;
        packet[10] = payloadValue;
        packet[11] = modeValue;
        packet[88] = expectedChecksum;

        RazerPacket decoded = RazerPacketCodec.DecodeHidFeatureReport(report);

        Assert.AreEqual(expectedChecksum, decoded.Crc);
        Assert.AreEqual(commandId, decoded.CommandId);
        Assert.AreEqual((byte)zone, decoded.Arguments[1]);
        Assert.AreEqual(payloadValue, decoded.Arguments[2]);
        Assert.AreEqual(modeValue, decoded.Arguments[3]);
    }
}
