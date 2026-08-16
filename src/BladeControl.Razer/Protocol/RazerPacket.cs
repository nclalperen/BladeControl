namespace BladeControl.Razer.Protocol;

internal sealed class RazerPacket
{
    private readonly byte[] _arguments;

    internal RazerPacket(
        byte status,
        byte transactionId,
        ushort remainingPackets,
        byte protocolType,
        byte dataSize,
        byte commandClass,
        byte commandId,
        ReadOnlySpan<byte> arguments,
        byte crc,
        byte reserved)
    {
        if (dataSize > RazerPacketCodec.ArgumentLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dataSize),
                dataSize,
                $"Razer packet data size cannot exceed {RazerPacketCodec.ArgumentLength} bytes.");
        }

        if (arguments.Length > RazerPacketCodec.ArgumentLength)
        {
            throw new ArgumentException(
                $"Razer packet arguments cannot exceed {RazerPacketCodec.ArgumentLength} bytes.",
                nameof(arguments));
        }

        Status = status;
        TransactionId = transactionId;
        RemainingPackets = remainingPackets;
        ProtocolType = protocolType;
        DataSize = dataSize;
        CommandClass = commandClass;
        CommandId = commandId;
        Crc = crc;
        Reserved = reserved;

        _arguments = new byte[RazerPacketCodec.ArgumentLength];
        arguments.CopyTo(_arguments);
    }

    internal byte Status { get; }

    internal byte TransactionId { get; }

    internal ushort RemainingPackets { get; }

    internal byte ProtocolType { get; }

    internal byte DataSize { get; }

    internal byte CommandClass { get; }

    internal byte CommandId { get; }

    internal ReadOnlySpan<byte> Arguments => _arguments;

    internal byte Crc { get; }

    internal byte Reserved { get; }

    internal ushort CombinedCommand => (ushort)((CommandClass << 8) | CommandId);
}
