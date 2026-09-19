using System.Buffers.Binary;

namespace UdpGame.Protocol;

public static class ProtocolSerializer
{
    public static byte[] SerializeMovement(ushort sequenceNumber, Movement movement)
    {
        var bytes = new byte[ProtocolConstants.HeaderSize + ProtocolConstants.MovementPayloadSize];
        WriteHeader(bytes, PacketType.Movement, sequenceNumber, ProtocolConstants.MovementPayloadSize);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(5, 4), movement.X);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(9, 4), movement.Y);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(13, 4), movement.Z);
        return bytes;
    }

    public static byte[] SerializeShoot(ushort sequenceNumber, Shoot shoot)
    {
        var bytes = new byte[ProtocolConstants.HeaderSize + ProtocolConstants.ShootPayloadSize];
        WriteHeader(bytes, PacketType.Shoot, sequenceNumber, ProtocolConstants.ShootPayloadSize);
        bytes[5] = shoot.WeaponId;
        return bytes;
    }

    public static byte[] SerializeStateUpdate(ushort sequenceNumber, StateUpdate state)
    {
        if (state.AcknowledgedType == PacketType.StateUpdate)
        {
            throw new ProtocolException("STATE_UPDATE cannot acknowledge itself");
        }

        var bytes = new byte[ProtocolConstants.HeaderSize + ProtocolConstants.StateUpdatePayloadSize];
        WriteHeader(bytes, PacketType.StateUpdate, sequenceNumber, ProtocolConstants.StateUpdatePayloadSize);
        bytes[5] = (byte)state.AcknowledgedType;
        bytes[6] = (byte)state.Status;
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(7, 4), state.X);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(11, 4), state.Y);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(15, 4), state.Z);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(19, 4), state.ShotsFired);
        bytes[23] = state.LastWeaponId;
        return bytes;
    }

    public static Packet Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < ProtocolConstants.HeaderSize)
        {
            throw new ProtocolException("Packet is shorter than the 5-byte header");
        }

        if (data.Length > ProtocolConstants.MaxPacketSize)
        {
            throw new ProtocolException("Packet exceeds the protocol size limit");
        }

        PacketType packetType = DecodePacketType(data[0]);
        ushort sequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(1, 2));
        ushort payloadSize = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(3, 2));

        if (payloadSize != data.Length - ProtocolConstants.HeaderSize)
        {
            throw new ProtocolException("PayloadSize does not match the UDP datagram length");
        }

        var header = new PacketHeader(packetType, sequenceNumber, payloadSize);
        return packetType switch
        {
            PacketType.Movement => new Packet(header, ReadMovement(data, payloadSize)),
            PacketType.Shoot => new Packet(header, ReadShoot(data, payloadSize)),
            PacketType.StateUpdate => new Packet(header, ReadStateUpdate(data, payloadSize)),
            _ => throw new ProtocolException("Unknown packet type"),
        };
    }

    private static Movement ReadMovement(ReadOnlySpan<byte> data, ushort payloadSize)
    {
        RequirePayloadSize(PacketType.Movement, payloadSize, ProtocolConstants.MovementPayloadSize);
        float x = BinaryPrimitives.ReadSingleBigEndian(data.Slice(5, 4));
        float y = BinaryPrimitives.ReadSingleBigEndian(data.Slice(9, 4));
        float z = BinaryPrimitives.ReadSingleBigEndian(data.Slice(13, 4));

        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            throw new ProtocolException("MOVEMENT contains a non-finite coordinate");
        }

        return new Movement(x, y, z);
    }

    private static Shoot ReadShoot(ReadOnlySpan<byte> data, ushort payloadSize)
    {
        RequirePayloadSize(PacketType.Shoot, payloadSize, ProtocolConstants.ShootPayloadSize);
        return new Shoot(data[5]);
    }

    private static StateUpdate ReadStateUpdate(ReadOnlySpan<byte> data, ushort payloadSize)
    {
        RequirePayloadSize(PacketType.StateUpdate, payloadSize, ProtocolConstants.StateUpdatePayloadSize);

        PacketType acknowledgedType = DecodePacketType(data[5]);
        if (acknowledgedType == PacketType.StateUpdate)
        {
            throw new ProtocolException("STATE_UPDATE cannot acknowledge itself");
        }

        StatusCode status = DecodeStatus(data[6]);
        float x = BinaryPrimitives.ReadSingleBigEndian(data.Slice(7, 4));
        float y = BinaryPrimitives.ReadSingleBigEndian(data.Slice(11, 4));
        float z = BinaryPrimitives.ReadSingleBigEndian(data.Slice(15, 4));
        uint shotsFired = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(19, 4));
        byte lastWeaponId = data[23];

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

    private static void WriteHeader(
        Span<byte> destination,
        PacketType packetType,
        ushort sequenceNumber,
        int payloadSize)
    {
        destination[0] = (byte)packetType;
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(1, 2), sequenceNumber);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(3, 2), checked((ushort)payloadSize));
    }

    private static PacketType DecodePacketType(byte value) => value switch
    {
        (byte)PacketType.Movement => PacketType.Movement,
        (byte)PacketType.Shoot => PacketType.Shoot,
        (byte)PacketType.StateUpdate => PacketType.StateUpdate,
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
}
