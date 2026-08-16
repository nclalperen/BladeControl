namespace BladeControl.Razer.Protocol;

internal static class RazerCommands
{
    internal const byte SystemCommandClass = 0x0D;
    internal const byte SetFanRpmCommandId = 0x01;
    internal const byte WriteBackPerformanceAndFanModeCommandId = 0x02;
    internal const byte WriteBackPerformanceLevelCommandId = 0x07;
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

    internal static RazerPacket CreateSetFanRpm(
        byte transactionId,
        RazerZone zone,
        FanRpm rpm)
    {
        byte zoneValue = ValidateZone(zone);
        byte[] arguments = [0x00, zoneValue, rpm.EncodedValue];
        return CreateRequest(transactionId, SetFanRpmCommandId, arguments);
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

    internal static RazerPacket CreateCustomAutoModeWriteBack(
        byte transactionId,
        RazerZone zone)
    {
        return CreateSetPerformanceAndFanMode(
            transactionId,
            zone,
            RazerPerformanceMode.Custom,
            RazerFanMode.Auto);
    }

    internal static RazerPacket CreateSetPerformanceAndFanMode(
        byte transactionId,
        RazerZone zone,
        RazerPerformanceMode performanceMode,
        RazerFanMode fanMode)
    {
        byte zoneValue = ValidateZone(zone);
        ValidateKnownPerformanceMode(performanceMode);
        ValidateKnownFanMode(fanMode);
        byte[] arguments =
            [0x01, zoneValue, performanceMode.Value, fanMode.Value];
        return CreateRequest(
            transactionId,
            WriteBackPerformanceAndFanModeCommandId,
            arguments);
    }

    internal static RazerPacket CreateExpectedPerformanceLevelWriteBack(
        byte transactionId,
        RazerPerformanceCluster cluster)
    {
        return cluster switch
        {
            RazerPerformanceCluster.Cpu => CreateSetCpuPerformanceLevel(
                transactionId,
                RazerCpuPerformanceLevel.Medium),
            RazerPerformanceCluster.Gpu => CreateSetGpuPerformanceLevel(
                transactionId,
                RazerGpuPerformanceLevel.Low),
            _ => throw new ArgumentOutOfRangeException(
                nameof(cluster),
                cluster,
                "Only the expected CPU and GPU performance levels are supported.")
        };
    }

    internal static RazerPacket CreateSetCpuPerformanceLevel(
        byte transactionId,
        RazerCpuPerformanceLevel level)
    {
        ValidateKnownCpuLevel(level);
        byte[] arguments =
            [0x00, (byte)RazerPerformanceCluster.Cpu, level.Value];
        return CreateRequest(
            transactionId,
            WriteBackPerformanceLevelCommandId,
            arguments);
    }

    internal static RazerPacket CreateSetGpuPerformanceLevel(
        byte transactionId,
        RazerGpuPerformanceLevel level)
    {
        ValidateKnownGpuLevel(level);
        byte[] arguments =
            [0x00, (byte)RazerPerformanceCluster.Gpu, level.Value];
        return CreateRequest(
            transactionId,
            WriteBackPerformanceLevelCommandId,
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
            SetFanRpmCommandId =>
                packet.DataSize == 3 && HasPolicyAllowedFanRpmArguments(packet),
            WriteBackPerformanceAndFanModeCommandId =>
                packet.DataSize == 4 &&
                HasPolicyAllowedModeWriteArguments(packet),
            WriteBackPerformanceLevelCommandId =>
                packet.DataSize == 3 &&
                HasPolicyAllowedLevelWriteArguments(packet),
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
                $"Razer command 0x{packet.CombinedCommand:X4} is not on the strict BladeControl whitelist.");
        }
    }

    internal static void EnsureProtocolShape(RazerPacket packet)
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

        bool isKnownShape = fixedFieldsAreValid && packet.CommandId switch
        {
            SetFanRpmCommandId =>
                packet.DataSize == 3 && HasKnownFanRpmArguments(packet),
            WriteBackPerformanceAndFanModeCommandId =>
                packet.DataSize == 4 && HasKnownModeWriteArguments(packet),
            WriteBackPerformanceLevelCommandId =>
                packet.DataSize == 3 && HasKnownLevelWriteArguments(packet),
            GetFanRpmCommandId =>
                packet.DataSize == 3 && HasAllowedZoneArguments(packet, 3),
            GetPerformanceAndFanModeCommandId =>
                packet.DataSize == 4 && HasAllowedZoneArguments(packet, 4),
            GetPerformanceBoostLevelCommandId =>
                packet.DataSize == 3 && HasAllowedClusterArguments(packet),
            _ => false
        };

        if (!isKnownShape)
        {
            throw new InvalidOperationException(
                $"Razer command 0x{packet.CombinedCommand:X4} is not a modeled BladeControl packet shape.");
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

        EnsureProtocolShape(packet);
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

    private static bool HasKnownModeWriteArguments(RazerPacket packet)
    {
        ReadOnlySpan<byte> arguments = packet.Arguments;
        bool commonPrefix =
            arguments[0] == 0x01 &&
            (arguments[1] == (byte)RazerZone.Zone1 ||
             arguments[1] == (byte)RazerZone.Zone2) &&
            IsKnownPerformanceMode(arguments[2]) &&
            IsKnownFanMode(arguments[3]);
        bool allowedCombination =
            arguments[3] == RazerFanMode.Auto.Value ||
            (arguments[2] == RazerPerformanceMode.Balanced.Value &&
             arguments[3] == RazerFanMode.Manual.Value);

        return commonPrefix && allowedCombination && IsAllZero(arguments[4..]);
    }

    private static bool HasKnownFanRpmArguments(RazerPacket packet)
    {
        ReadOnlySpan<byte> arguments = packet.Arguments;
        bool allowedPrefix =
            arguments[0] == 0x00 &&
            (arguments[1] == (byte)RazerZone.Zone1 ||
             arguments[1] == (byte)RazerZone.Zone2) &&
            arguments[2] >= FanRpm.Minimum.EncodedValue &&
            arguments[2] <= FanRpm.Maximum.EncodedValue;
        return allowedPrefix && IsAllZero(arguments[3..]);
    }

    private static bool HasPolicyAllowedFanRpmArguments(RazerPacket packet) =>
        HasKnownFanRpmArguments(packet);

    private static bool HasKnownLevelWriteArguments(
        RazerPacket packet)
    {
        ReadOnlySpan<byte> arguments = packet.Arguments;
        bool isKnownCpuLevel =
            arguments[0] == 0x00 &&
            arguments[1] == (byte)RazerPerformanceCluster.Cpu &&
            arguments[2] <= RazerCpuPerformanceLevel.Overclock.Value;
        bool isKnownGpuLevel =
            arguments[0] == 0x00 &&
            arguments[1] == (byte)RazerPerformanceCluster.Gpu &&
            arguments[2] <= RazerGpuPerformanceLevel.High.Value;

        return (isKnownCpuLevel || isKnownGpuLevel) &&
            IsAllZero(arguments[3..]);
    }

    private static bool HasPolicyAllowedModeWriteArguments(RazerPacket packet)
    {
        return HasKnownModeWriteArguments(packet);
    }

    private static bool HasPolicyAllowedLevelWriteArguments(RazerPacket packet)
    {
        ReadOnlySpan<byte> arguments = packet.Arguments;
        bool isAllowedCpu =
            arguments[1] == (byte)RazerPerformanceCluster.Cpu &&
            (arguments[2] == RazerCpuPerformanceLevel.Low.Value ||
             arguments[2] == RazerCpuPerformanceLevel.Medium.Value);
        bool isAllowedGpu =
            arguments[1] == (byte)RazerPerformanceCluster.Gpu &&
            arguments[2] == RazerGpuPerformanceLevel.Low.Value;

        return HasKnownLevelWriteArguments(packet) &&
            (isAllowedCpu || isAllowedGpu);
    }

    private static bool IsKnownPerformanceMode(byte value) =>
        value == RazerPerformanceMode.Balanced.Value ||
        value == RazerPerformanceMode.Custom.Value ||
        value == RazerPerformanceMode.Silent.Value;

    private static bool IsKnownFanMode(byte value) =>
        value == RazerFanMode.Auto.Value || value == RazerFanMode.Manual.Value;

    private static void ValidateKnownPerformanceMode(RazerPerformanceMode mode)
    {
        if (!IsKnownPerformanceMode(mode.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown performance mode.");
        }
    }

    private static void ValidateKnownFanMode(RazerFanMode mode)
    {
        if (!IsKnownFanMode(mode.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown fan mode.");
        }
    }

    private static void ValidateKnownCpuLevel(RazerCpuPerformanceLevel level)
    {
        if (level.Value > RazerCpuPerformanceLevel.Overclock.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown CPU performance level.");
        }
    }

    private static void ValidateKnownGpuLevel(RazerGpuPerformanceLevel level)
    {
        if (level.Value > RazerGpuPerformanceLevel.High.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown GPU performance level.");
        }
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
