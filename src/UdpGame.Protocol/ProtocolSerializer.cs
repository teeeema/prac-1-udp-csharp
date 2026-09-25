using System.Buffers.Binary;

namespace UdpGame.Protocol;

public static class ProtocolSerializer
{
    public static byte[] SerializeMovement(ushort sequenceNumber, Movement movement)
    {
        var bytes = CreatePacket(PacketType.Movement, sequenceNumber, ProtocolConstants.MovementPayloadSize);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(7, 4), movement.X);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(11, 4), movement.Y);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(15, 4), movement.Z);
        return bytes;
    }

    public static byte[] SerializeShoot(ushort sequenceNumber, Shoot shoot)
    {
        var bytes = CreatePacket(PacketType.Shoot, sequenceNumber, ProtocolConstants.ShootPayloadSize);
        bytes[7] = shoot.WeaponId;
        return bytes;
    }

    public static byte[] SerializeStateUpdate(ushort sequenceNumber, StateUpdate state)
    {
        if (state.AcknowledgedType is not (PacketType.Movement or PacketType.Shoot))
        {
            throw new ProtocolException("STATE_UPDATE can acknowledge only MOVEMENT or SHOOT");
        }

        var bytes = CreatePacket(PacketType.StateUpdate, sequenceNumber, ProtocolConstants.StateUpdatePayloadSize);
        bytes[7] = (byte)state.AcknowledgedType;
        bytes[8] = (byte)state.Status;
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(9, 4), state.X);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(13, 4), state.Y);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(17, 4), state.Z);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(21, 4), state.ShotsFired);
        bytes[25] = state.LastWeaponId;
        return bytes;
    }

    public static byte[] SerializePing(ushort sequenceNumber, Ping ping)
    {
        var bytes = CreatePacket(PacketType.Ping, sequenceNumber, ProtocolConstants.PingPayloadSize);
        WriteU64(bytes, 7, ping.ClientSendTimeUs);
        return bytes;
    }

    public static byte[] SerializePong(ushort sequenceNumber, Pong pong)
    {
        var bytes = CreatePacket(PacketType.Pong, sequenceNumber, ProtocolConstants.PongPayloadSize);
        WriteU64(bytes, 7, pong.ClientSendTimeUs);
        WriteU64(bytes, 15, pong.ServerReceiveTimeUs);
        WriteU64(bytes, 23, pong.ServerSendTimeUs);
        return bytes;
    }

    public static Packet Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < ProtocolConstants.HeaderSize)
        {
            throw new ProtocolException($"Packet is shorter than the {ProtocolConstants.HeaderSize}-byte header");
        }

        if (data.Length > ProtocolConstants.MaxPacketSize)
        {
            throw new ProtocolException("Packet exceeds the protocol size limit");
        }

        PacketType packetType = DecodePacketType(data[0]);
        ushort sequenceNumber = ReadU16(data, 1);
        ushort payloadSize = ReadU16(data, 3);
        ushort protocolVersion = ReadU16(data, 5);

        if (protocolVersion != ProtocolConstants.ProtocolVersion)
        {
            throw new ProtocolException(
                $"Unsupported protocol version {protocolVersion}, expected {ProtocolConstants.ProtocolVersion}");
        }

        if (payloadSize != data.Length - ProtocolConstants.HeaderSize)
        {
            throw new ProtocolException("PayloadSize does not match the UDP datagram length");
        }

        var header = new PacketHeader(packetType, sequenceNumber, payloadSize, protocolVersion);
        return packetType switch
        {
            PacketType.Movement => new Packet(header, ReadMovement(data, payloadSize)),
            PacketType.Shoot => new Packet(header, ReadShoot(data, payloadSize)),
            PacketType.StateUpdate => new Packet(header, ReadStateUpdate(data, payloadSize)),
            PacketType.Ping => new Packet(header, ReadPing(data, payloadSize)),
            PacketType.Pong => new Packet(header, ReadPong(data, payloadSize)),
            _ => throw new ProtocolException("Unknown packet type"),
        };
    }

    public static void WriteU16(Span<byte> destination, int offset, ushort value)
    {
        EnsureRange(destination.Length, offset, sizeof(ushort));
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(offset, sizeof(ushort)), value);
    }

    public static void WriteU64(Span<byte> destination, int offset, ulong value)
    {
        EnsureRange(destination.Length, offset, sizeof(ulong));
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(offset, sizeof(ulong)), value);
    }

    public static ushort ReadU16(ReadOnlySpan<byte> source, int offset)
    {
        EnsureRange(source.Length, offset, sizeof(ushort));
        return BinaryPrimitives.ReadUInt16BigEndian(source.Slice(offset, sizeof(ushort)));
    }

    public static ulong ReadU64(ReadOnlySpan<byte> source, int offset)
    {
        EnsureRange(source.Length, offset, sizeof(ulong));
        return BinaryPrimitives.ReadUInt64BigEndian(source.Slice(offset, sizeof(ulong)));
    }

    private static byte[] CreatePacket(PacketType packetType, ushort sequenceNumber, int payloadSize)
    {
        int packetSize = ProtocolConstants.HeaderSize + payloadSize;
        if (packetSize > ProtocolConstants.MaxPacketSize)
        {
            throw new ProtocolException("Packet exceeds the protocol size limit");
        }

        var bytes = new byte[packetSize];
        bytes[0] = (byte)packetType;
        WriteU16(bytes, 1, sequenceNumber);
        WriteU16(bytes, 3, checked((ushort)payloadSize));
        WriteU16(bytes, 5, ProtocolConstants.ProtocolVersion);
        return bytes;
    }

    private static Movement ReadMovement(ReadOnlySpan<byte> data, ushort payloadSize)
    {
        RequirePayloadSize(PacketType.Movement, payloadSize, ProtocolConstants.MovementPayloadSize);
        float x = BinaryPrimitives.ReadSingleBigEndian(data.Slice(7, 4));
        float y = BinaryPrimitives.ReadSingleBigEndian(data.Slice(11, 4));
        float z = BinaryPrimitives.ReadSingleBigEndian(data.Slice(15, 4));

        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            throw new ProtocolException("MOVEMENT contains a non-finite coordinate");
        }

        return new Movement(x, y, z);
    }

    private static Shoot ReadShoot(ReadOnlySpan<byte> data, ushort payloadSize)
    {
        RequirePayloadSize(PacketType.Shoot, payloadSize, ProtocolConstants.ShootPayloadSize);
        return new Shoot(data[7]);
    }

    private static StateUpdate ReadStateUpdate(ReadOnlySpan<byte> data, ushort payloadSize)
    {
        RequirePayloadSize(PacketType.StateUpdate, payloadSize, ProtocolConstants.StateUpdatePayloadSize);

        PacketType acknowledgedType = DecodePacketType(data[7]);
        if (acknowledgedType is not (PacketType.Movement or PacketType.Shoot))
        {
            throw new ProtocolException("STATE_UPDATE can acknowledge only MOVEMENT or SHOOT");
        }

        StatusCode status = DecodeStatus(data[8]);
        float x = BinaryPrimitives.ReadSingleBigEndian(data.Slice(9, 4));
        float y = BinaryPrimitives.ReadSingleBigEndian(data.Slice(13, 4));
        float z = BinaryPrimitives.ReadSingleBigEndian(data.Slice(17, 4));
        uint shotsFired = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(21, 4));
        byte lastWeaponId = data[25];

        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            throw new ProtocolException("STATE_UPDATE contains a non-finite coordinate");
        }

        return new StateUpdate(
            acknowledgedType,
            status,
            x,
            y,
            z,
            shotsFired,
            lastWeaponId);
    }

    private static Ping ReadPing(ReadOnlySpan<byte> data, ushort payloadSize)
    {
        RequirePayloadSize(PacketType.Ping, payloadSize, ProtocolConstants.PingPayloadSize);
        return new Ping(ReadU64(data, 7));
    }

    private static Pong ReadPong(ReadOnlySpan<byte> data, ushort payloadSize)
    {
        RequirePayloadSize(PacketType.Pong, payloadSize, ProtocolConstants.PongPayloadSize);
        return new Pong(
            ReadU64(data, 7),
            ReadU64(data, 15),
            ReadU64(data, 23));
    }

    private static PacketType DecodePacketType(byte value) => value switch
    {
        (byte)PacketType.Movement => PacketType.Movement,
        (byte)PacketType.Shoot => PacketType.Shoot,
        (byte)PacketType.StateUpdate => PacketType.StateUpdate,
        (byte)PacketType.Ping => PacketType.Ping,
        (byte)PacketType.Pong => PacketType.Pong,
        _ => throw new ProtocolException($"Unknown packet type: {value}"),
    };

    private static StatusCode DecodeStatus(byte value) => value switch
    {
        (byte)StatusCode.Accepted => StatusCode.Accepted,
        (byte)StatusCode.OutOfRange => StatusCode.OutOfRange,
        _ => throw new ProtocolException($"Unknown status code: {value}"),
    };

    private static void RequirePayloadSize(PacketType packetType, ushort actual, int expected)
    {
        if (actual != expected)
        {
            throw new ProtocolException(
                $"{packetType} has invalid payload size {actual}, expected {expected}");
        }
    }

    private static void EnsureRange(int bufferLength, int offset, int size)
    {
        if (offset < 0 || size < 0 || offset > bufferLength - size)
        {
            throw new ProtocolException("Packet buffer boundary exceeded");
        }
    }
}
