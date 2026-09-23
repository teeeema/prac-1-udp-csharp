namespace UdpGame.Protocol;

public enum PacketType : byte
{
    Movement = 1,
    Shoot = 2,
    StateUpdate = 3,
}
