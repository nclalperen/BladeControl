using BladeControl.Razer.Protocol;

namespace BladeControl.Razer.Tests;

[TestClass]
public sealed class RazerPacketCodecTests
{
    [TestMethod]
    public void EncodeProducesExactDeterministicNinetyByteLayout()
    {
        byte[] arguments = [0xAA, 0xBB, 0xCC];
        var packet = new RazerPacket(
            status: 0x02,
            transactionId: 0x2A,
            remainingPackets: 0x1234,
            protocolType: 0x00,
            dataSize: 0x03,
            commandClass: 0x0D,
            commandId: 0x81,
            arguments,
            crc: 0x00,
            reserved: 0x00);

        byte[] encoded = RazerPacketCodec.Encode(packet);

        Assert.AreEqual(90, encoded.Length);
        Assert.AreEqual(0x02, encoded[0]);
        Assert.AreEqual(0x2A, encoded[1]);
        Assert.AreEqual(0x34, encoded[2]);
        Assert.AreEqual(0x12, encoded[3]);
        Assert.AreEqual(0x00, encoded[4]);
        Assert.AreEqual(0x03, encoded[5]);
        Assert.AreEqual(0x0D, encoded[6]);
        Assert.AreEqual(0x81, encoded[7]);
        CollectionAssert.AreEqual(arguments, encoded[8..11]);
        Assert.IsTrue(encoded[11..88].All(value => value == 0));
        Assert.AreEqual(0x74, encoded[88]);
        Assert.AreEqual(0x00, encoded[89]);
    }

    [TestMethod]
    public void EncodeHidFeatureReportAddsZeroReportIdAndIsNinetyOneBytes()
    {
        RazerPacket packet = RazerCommands.CreateGetFanRpm(0x35, RazerZone.Zone2);

        byte[] report = RazerPacketCodec.EncodeHidFeatureReport(packet);

        Assert.AreEqual(91, report.Length);
        Assert.AreEqual(0x00, report[0]);
        Assert.AreEqual(0x35, report[2]);
        Assert.AreEqual(0x0D, report[7]);
        Assert.AreEqual(0x81, report[8]);
        CollectionAssert.AreEqual(
            new byte[] { 0x00, 0x02, 0x00 },
            report[9..12]);
    }

    [TestMethod]
    public void DecodeParsesEveryResponseFieldAndArguments()
    {
        var response = new RazerPacket(
            status: 0x02,
            transactionId: 0x44,
            remainingPackets: 0x1234,
            protocolType: 0,
            dataSize: 4,
            commandClass: 0x0D,
            commandId: 0x82,
            arguments: new byte[] { 0x00, 0x01, 0x05, 0x01 },
            crc: 0x7A,
            reserved: 0);
        byte[] report = RazerPacketCodec.EncodeHidFeatureReport(response);

        RazerPacket decoded = RazerPacketCodec.DecodeHidFeatureReport(report);

        Assert.AreEqual(0x02, decoded.Status);
        Assert.AreEqual(0x44, decoded.TransactionId);
        Assert.AreEqual(0x1234, decoded.RemainingPackets);
        Assert.AreEqual(0x00, decoded.ProtocolType);
        Assert.AreEqual(4, decoded.DataSize);
        Assert.AreEqual(0x0D, decoded.CommandClass);
        Assert.AreEqual(0x82, decoded.CommandId);
        CollectionAssert.AreEqual(
            new byte[] { 0x00, 0x01, 0x05, 0x01 },
            decoded.Arguments[..4].ToArray());
        Assert.AreEqual(0xA8, decoded.Crc);
        Assert.AreEqual(0x00, decoded.Reserved);
    }

    [TestMethod]
    public void DecodeRejectsWrongHidReportLength()
    {
        Assert.ThrowsException<ArgumentException>(
            () => RazerPacketCodec.DecodeHidFeatureReport(new byte[90]));
    }

    [TestMethod]
    public void DecodeRejectsNonzeroHidReportId()
    {
        var report = new byte[91];
        report[0] = 0x01;

        Assert.ThrowsException<ArgumentException>(
            () => RazerPacketCodec.DecodeHidFeatureReport(report));
    }

    [TestMethod]
    public void DecodeRejectsOversizedDataSize()
    {
        var report = new byte[91];
        report[6] = 81;
        report[89] = RazerPacketCodec.CalculateChecksum(report.AsSpan(1));

        Assert.ThrowsException<ArgumentException>(
            () => RazerPacketCodec.DecodeHidFeatureReport(report));
    }

    [TestMethod]
    public void DecodeRejectsChecksumMismatchWithExpectedAndActualValues()
    {
        RazerPacket packet = RazerCommands.CreateGetFanRpm(0x01, RazerZone.Zone1);
        byte[] report = RazerPacketCodec.EncodeHidFeatureReport(packet);
        report[89] = 0x00;

        ArgumentException exception = Assert.ThrowsException<ArgumentException>(
            () => RazerPacketCodec.DecodeHidFeatureReport(report));

        StringAssert.Contains(exception.Message, "expected 0x8E");
        StringAssert.Contains(exception.Message, "received 0x00");
    }
}
