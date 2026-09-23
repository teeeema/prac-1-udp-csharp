namespace UdpGame.Protocol;

public readonly record struct PacketHeader(
    PacketType PacketType,
    ushort SequenceNumber,
    ushort PayloadSize);

public readonly record struct Movement(float X, float Y, float Z);

public readonly record struct Shoot(byte WeaponId);

public readonly record struct StateUpdate(
    PacketType AcknowledgedType,
    StatusCode Status,
    float X,
    float Y,
    float Z,
    uint ShotsFired,
    byte LastWeaponId);

public sealed record Packet(PacketHeader Header, object Payload);
