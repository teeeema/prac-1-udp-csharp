namespace UdpGame.Protocol;

public static class ProtocolConstants
{
    public const int HeaderSize = 5;
    public const int MovementPayloadSize = 12;
    public const int ShootPayloadSize = 1;
    public const int StateUpdatePayloadSize = 19;
    public const int MaxPacketSize = 1024;
}
