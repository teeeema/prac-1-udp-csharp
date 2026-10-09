using System.Buffers.Binary;

namespace UdpGame.Protocol;

public static class ProtocolSerializer
{
    public static byte[] SerializeMovement(ushort sequenceNumber, Movement movement, bool requiresAck = false)
    {
        var bytes = CreatePacket(PacketType.Movement, sequenceNumber, ProtocolConstants.MovementPayloadSize, requiresAck);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(ProtocolConstants.HeaderSize, 4), movement.X);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(ProtocolConstants.HeaderSize + 4, 4), movement.Y);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(ProtocolConstants.HeaderSize + 8, 4), movement.Z);
        return bytes;
    }

    public static byte[] SerializeShoot(ushort sequenceNumber, Shoot shoot, bool requiresAck = false)
    {
        var bytes = CreatePacket(PacketType.Shoot, sequenceNumber, ProtocolConstants.ShootPayloadSize, requiresAck);
        bytes[ProtocolConstants.HeaderSize] = shoot.WeaponId;
        return bytes;
    }

    public static byte[] SerializeStateUpdate(ushort sequenceNumber, StateUpdate state, bool requiresAck = false)
    {
        if (state.AcknowledgedType is not (PacketType.Movement or PacketType.Shoot))
        {
            throw new ProtocolException("STATE_UPDATE can acknowledge only MOVEMENT or SHOOT");
        }

        var bytes = CreatePacket(PacketType.StateUpdate, sequenceNumber, ProtocolConstants.StateUpdatePayloadSize, requiresAck);
        bytes[ProtocolConstants.HeaderSize] = (byte)state.AcknowledgedType;
        bytes[ProtocolConstants.HeaderSize + 1] = (byte)state.Status;
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(ProtocolConstants.HeaderSize + 2, 4), state.X);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(ProtocolConstants.HeaderSize + 6, 4), state.Y);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(ProtocolConstants.HeaderSize + 10, 4), state.Z);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(ProtocolConstants.HeaderSize + 14, 4), state.ShotsFired);
        bytes[ProtocolConstants.HeaderSize + 18] = state.LastWeaponId;
        return bytes;
    }

    public static byte[] SerializePing(ushort sequenceNumber, Ping ping)
    {
        var bytes = CreatePacket(PacketType.Ping, sequenceNumber, ProtocolConstants.PingPayloadSize);
        WriteU64(bytes, ProtocolConstants.HeaderSize, ping.ClientSendTimeUs);
        return bytes;
    }

    public static byte[] SerializePong(ushort sequenceNumber, Pong pong)
    {
        var bytes = CreatePacket(PacketType.Pong, sequenceNumber, ProtocolConstants.PongPayloadSize);
        WriteU64(bytes, ProtocolConstants.HeaderSize, pong.ClientSendTimeUs);
        WriteU64(bytes, ProtocolConstants.HeaderSize + 8, pong.ServerReceiveTimeUs);
        WriteU64(bytes, ProtocolConstants.HeaderSize + 16, pong.ServerSendTimeUs);
        return bytes;
    }

    public static byte[] SerializeAck(ushort sequenceNumber, AckPayload ack)
    {
        var bytes = CreatePacket(PacketType.Ack, sequenceNumber, ProtocolConstants.AckPayloadSize);
        WriteU16(bytes, ProtocolConstants.HeaderSize, ack.AcknowledgedSequence);
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

        byte requiresAckByte = data[7];
        if (requiresAckByte > 1)
        {
            throw new ProtocolException($"Invalid RequiresAck byte: {requiresAckByte}");
        }

        bool requiresAck = requiresAckByte == 1;
        if (packetType == PacketType.Ack && requiresAck)
        {
            throw new ProtocolException("ACK cannot require ACK");
        }

        if (payloadSize != data.Length - ProtocolConstants.HeaderSize)
        {
            throw new ProtocolException("PayloadSize does not match the UDP datagram length");
        }

        var header = new PacketHeader(packetType, sequenceNumber, payloadSize, protocolVersion, requiresAck);
        return packetType switch
        {
            PacketType.Movement => new Packet(header, ReadMovement(data, payloadSize)),
            PacketType.Shoot => new Packet(header, ReadShoot(data, payloadSize)),
            PacketType.StateUpdate => new Packet(header, ReadStateUpdate(data, payloadSize)),
            PacketType.Ping => new Packet(header, ReadPing(data, payloadSize)),
            PacketType.Pong => new Packet(header, ReadPong(data, payloadSize)),
            PacketType.Ack => new Packet(header, ReadAck(data, payloadSize)),
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

    private static byte[] CreatePacket(PacketType packetType, ushort sequenceNumber, int payloadSize, bool requiresAck = false)
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
        bytes[7] = requiresAck ? (byte)1 : (byte)0;
        return bytes;
    }

    private static Movement ReadMovement(ReadOnlySpan<byte> data, ushort payloadSize)
    {
        RequirePayloadSize(PacketType.Movement, payloadSize, ProtocolConstants.MovementPayloadSize);
        float x = BinaryPrimitives.ReadSingleBigEndian(data.Slice(ProtocolConstants.HeaderSize, 4));
        float y = BinaryPrimitives.ReadSingleBigEndian(data.Slice(ProtocolConstants.HeaderSize + 4, 4));
        float z = BinaryPrimitives.ReadSingleBigEndian(data.Slice(ProtocolConstants.HeaderSize + 8, 4));

        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            throw new ProtocolException("MOVEMENT contains a non-finite coordinate");
        }

        return new Movement(x, y, z);
    }

    private static Shoot ReadShoot(ReadOnlySpan<byte> data, ushort payloadSize)
    {
        RequirePayloadSize(PacketType.Shoot, payloadSize, ProtocolConstants.ShootPayloadSize);
        return new Shoot(data[ProtocolConstants.HeaderSize]);
    }

    private static StateUpdate ReadStateUpdate(ReadOnlySpan<byte> data, ushort payloadSize)
    {
        RequirePayloadSize(PacketType.StateUpdate, payloadSize, ProtocolConstants.StateUpdatePayloadSize);

        PacketType acknowledgedType = DecodePacketType(data[ProtocolConstants.HeaderSize]);
        if (acknowledgedType is not (PacketType.Movement or PacketType.Shoot))
        {
            throw new ProtocolException("STATE_UPDATE can acknowledge only MOVEMENT or SHOOT");
        }

        StatusCode status = DecodeStatus(data[ProtocolConstants.HeaderSize + 1]);
        float x = BinaryPrimitives.ReadSingleBigEndian(data.Slice(ProtocolConstants.HeaderSize + 2, 4));
        float y = BinaryPrimitives.ReadSingleBigEndian(data.Slice(ProtocolConstants.HeaderSize + 6, 4));
        float z = BinaryPrimitives.ReadSingleBigEndian(data.Slice(ProtocolConstants.HeaderSize + 10, 4));
        uint shotsFired = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(ProtocolConstants.HeaderSize + 14, 4));
        byte lastWeaponId = data[ProtocolConstants.HeaderSize + 18];

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
        return new Ping(ReadU64(data, ProtocolConstants.HeaderSize));
    }

    private static Pong ReadPong(ReadOnlySpan<byte> data, ushort payloadSize)
    {
        RequirePayloadSize(PacketType.Pong, payloadSize, ProtocolConstants.PongPayloadSize);
        return new Pong(
            ReadU64(data, ProtocolConstants.HeaderSize),
            ReadU64(data, ProtocolConstants.HeaderSize + 8),
            ReadU64(data, ProtocolConstants.HeaderSize + 16));
    }

    private static AckPayload ReadAck(ReadOnlySpan<byte> data, ushort payloadSize)
    {
        RequirePayloadSize(PacketType.Ack, payloadSize, ProtocolConstants.AckPayloadSize);
        return new AckPayload(ReadU16(data, ProtocolConstants.HeaderSize));
    }

    private static PacketType DecodePacketType(byte value) => value switch
    {
        (byte)PacketType.Movement => PacketType.Movement,
        (byte)PacketType.Shoot => PacketType.Shoot,
        (byte)PacketType.StateUpdate => PacketType.StateUpdate,
        (byte)PacketType.Ping => PacketType.Ping,
        (byte)PacketType.Pong => PacketType.Pong,
        (byte)PacketType.Ack => PacketType.Ack,
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
