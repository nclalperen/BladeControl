using System.Buffers.Binary;

namespace BladeControl.Razer.Protocol;

internal static class RazerPacketCodec
{
    internal const int PacketLength = 90;
    internal const int HidFeatureReportLength = 91;
    internal const int ArgumentLength = 80;
    internal const byte HidReportId = 0;
    private const int ChecksumStartOffset = 2;
    private const int ChecksumEndOffset = 88;
    private const int ChecksumOffset = 88;

    internal static byte[] Encode(RazerPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        var encoded = new byte[PacketLength];
        encoded[0] = packet.Status;
        encoded[1] = packet.TransactionId;
        BinaryPrimitives.WriteUInt16LittleEndian(
            encoded.AsSpan(2, sizeof(ushort)),
            packet.RemainingPackets);
        encoded[4] = packet.ProtocolType;
        encoded[5] = packet.DataSize;
        encoded[6] = packet.CommandClass;
        encoded[7] = packet.CommandId;
        packet.Arguments.CopyTo(encoded.AsSpan(8, ArgumentLength));
        encoded[ChecksumOffset] = CalculateChecksum(encoded);
        return encoded;
    }

    internal static RazerPacket Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != PacketLength)
        {
            throw new ArgumentException(
                $"A Razer packet must be exactly {PacketLength} bytes; received {encoded.Length}.",
                nameof(encoded));
        }

        byte expectedChecksum = CalculateChecksum(encoded);
        byte actualChecksum = encoded[ChecksumOffset];
        if (actualChecksum != expectedChecksum)
        {
            throw new ArgumentException(
                $"Razer packet checksum mismatch: expected 0x{expectedChecksum:X2}, " +
                $"received 0x{actualChecksum:X2}.",
                nameof(encoded));
        }

        byte dataSize = encoded[5];
        if (dataSize > ArgumentLength)
        {
            throw new ArgumentException(
                $"Razer packet data size {dataSize} exceeds the {ArgumentLength}-byte argument area.",
                nameof(encoded));
        }

        return new RazerPacket(
            encoded[0],
            encoded[1],
            BinaryPrimitives.ReadUInt16LittleEndian(encoded.Slice(2, sizeof(ushort))),
            encoded[4],
            dataSize,
            encoded[6],
            encoded[7],
            encoded.Slice(8, ArgumentLength),
            actualChecksum,
            encoded[89]);
    }

    internal static byte CalculateChecksum(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != PacketLength)
        {
            throw new ArgumentException(
                $"A Razer packet must be exactly {PacketLength} bytes; received {encoded.Length}.",
                nameof(encoded));
        }

        byte checksum = 0;
        for (int index = ChecksumStartOffset; index < ChecksumEndOffset; index++)
        {
            checksum ^= encoded[index];
        }

        return checksum;
    }

    internal static byte[] EncodeHidFeatureReport(RazerPacket packet)
    {
        byte[] packetBytes = Encode(packet);
        var report = new byte[HidFeatureReportLength];
        report[0] = HidReportId;
        packetBytes.CopyTo(report, 1);
        return report;
    }

    internal static RazerPacket DecodeHidFeatureReport(ReadOnlySpan<byte> report)
    {
        if (report.Length != HidFeatureReportLength)
        {
            throw new ArgumentException(
                $"A Razer HID feature report must be exactly {HidFeatureReportLength} bytes; " +
                $"received {report.Length}.",
                nameof(report));
        }

        if (report[0] != HidReportId)
        {
            throw new ArgumentException(
                $"Expected HID report ID 0x{HidReportId:X2}, received 0x{report[0]:X2}.",
                nameof(report));
        }

        return Decode(report[1..]);
    }
}
