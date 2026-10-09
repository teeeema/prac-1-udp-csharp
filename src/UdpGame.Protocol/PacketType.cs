namespace UdpGame.Protocol;

public enum PacketType : byte
{
    Movement = 1,
    Shoot = 2,
    StateUpdate = 3,
    Ping = 4,
    Pong = 5,
    Ack = 6,
}
