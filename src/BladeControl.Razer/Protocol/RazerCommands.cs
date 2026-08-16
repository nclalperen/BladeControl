namespace BladeControl.Razer.Protocol;

internal static class RazerCommands
{
    internal const byte SystemCommandClass = 0x0D;
    internal const byte GetFanRpmCommandId = 0x81;
    internal const byte GetPerformanceAndFanModeCommandId = 0x82;
    internal const byte GetPerformanceBoostLevelCommandId = 0x87;

    internal static RazerPacket CreateGetFanRpm(byte transactionId, RazerZone zone)
    {
        byte zoneValue = ValidateZone(zone);
        byte[] arguments = [0x00, zoneValue, 0x00];
        return CreateRequest(
            transactionId,
            GetFanRpmCommandId,
            arguments);
    }

    internal static RazerPacket CreateGetPerformanceAndFanMode(
        byte transactionId,
        RazerZone zone)
    {
        byte zoneValue = ValidateZone(zone);
        byte[] arguments = [0x00, zoneValue, 0x00, 0x00];
        return CreateRequest(
            transactionId,
            GetPerformanceAndFanModeCommandId,
            arguments);
    }

    internal static RazerPacket CreateGetPerformanceBoostLevel(
        byte transactionId,
        RazerPerformanceCluster cluster)
    {
        byte clusterValue = ValidateCluster(cluster);
        byte[] arguments = [0x00, clusterValue, 0x00];
        return CreateRequest(
            transactionId,
            GetPerformanceBoostLevelCommandId,
            arguments);
    }

    internal static void EnsureAllowed(RazerPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        bool fixedFieldsAreValid =
            packet.Status == 0x00 &&
            packet.TransactionId != 0x00 &&
            packet.RemainingPackets == 0 &&
            packet.ProtocolType == 0x00 &&
            packet.CommandClass == SystemCommandClass &&
            packet.Crc == 0x00 &&
            packet.Reserved == 0x00;

        bool isAllowed = fixedFieldsAreValid && packet.CommandId switch
        {
            GetFanRpmCommandId =>
                packet.DataSize == 3 &&
                HasAllowedZoneArguments(packet, expectedDataSize: 3),
            GetPerformanceAndFanModeCommandId =>
                packet.DataSize == 4 &&
                HasAllowedZoneArguments(packet, expectedDataSize: 4),
            GetPerformanceBoostLevelCommandId =>
                packet.DataSize == 3 &&
                HasAllowedClusterArguments(packet),
            _ => false
        };

        if (!isAllowed)
        {
            throw new InvalidOperationException(
                $"Razer command 0x{packet.CombinedCommand:X4} is not on the Milestone 2 GET whitelist.");
        }
    }

    private static RazerPacket CreateRequest(
        byte transactionId,
        byte commandId,
        ReadOnlySpan<byte> arguments)
    {
        if (transactionId == 0x00)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transactionId),
                "Razer transaction ID 0x00 is not valid.");
        }

        var packet = new RazerPacket(
            status: 0x00,
            transactionId,
            remainingPackets: 0,
            protocolType: 0x00,
            dataSize: checked((byte)arguments.Length),
            commandClass: SystemCommandClass,
            commandId,
            arguments,
            crc: 0x00,
            reserved: 0x00);

        EnsureAllowed(packet);
        return packet;
    }

    private static bool HasAllowedZoneArguments(
        RazerPacket packet,
        int expectedDataSize)
    {
        ReadOnlySpan<byte> arguments = packet.Arguments;
        bool allowedPrefix =
            arguments[0] == 0x00 &&
            (arguments[1] == (byte)RazerZone.Zone1 ||
             arguments[1] == (byte)RazerZone.Zone2) &&
            arguments[2] == 0x00 &&
            (expectedDataSize == 3 || arguments[3] == 0x00);

        return allowedPrefix && IsAllZero(arguments[expectedDataSize..]);
    }

    private static bool HasAllowedClusterArguments(RazerPacket packet)
    {
        ReadOnlySpan<byte> arguments = packet.Arguments;
        bool allowedPrefix =
            arguments[0] == 0x00 &&
            (arguments[1] == (byte)RazerPerformanceCluster.Cpu ||
             arguments[1] == (byte)RazerPerformanceCluster.Gpu) &&
            arguments[2] == 0x00;

        return allowedPrefix && IsAllZero(arguments[3..]);
    }

    private static bool IsAllZero(ReadOnlySpan<byte> values)
    {
        foreach (byte value in values)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static byte ValidateZone(RazerZone zone)
    {
        return zone switch
        {
            RazerZone.Zone1 => (byte)RazerZone.Zone1,
            RazerZone.Zone2 => (byte)RazerZone.Zone2,
            _ => throw new ArgumentOutOfRangeException(
                nameof(zone),
                zone,
                "Only Razer zones 1 and 2 are supported.")
        };
    }

    private static byte ValidateCluster(RazerPerformanceCluster cluster)
    {
        return cluster switch
        {
            RazerPerformanceCluster.Cpu => (byte)RazerPerformanceCluster.Cpu,
            RazerPerformanceCluster.Gpu => (byte)RazerPerformanceCluster.Gpu,
            _ => throw new ArgumentOutOfRangeException(
                nameof(cluster),
                cluster,
                "Only CPU and GPU performance clusters are supported.")
        };
    }
}
