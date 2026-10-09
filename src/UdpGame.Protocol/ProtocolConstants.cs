namespace UdpGame.Protocol;

public static class ProtocolConstants
{
    public const ushort ProtocolVersion = 2;
    public const int HeaderSize = 8;
    public const int MovementPayloadSize = 12;
    public const int ShootPayloadSize = 1;
    public const int StateUpdatePayloadSize = 19;
    public const int PingPayloadSize = 8;
    public const int PongPayloadSize = 24;
    public const int AckPayloadSize = 2;
    public const int MaxPacketSize = 1024;
}
