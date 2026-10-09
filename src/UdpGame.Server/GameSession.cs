using UdpGame.Protocol;

namespace UdpGame.Server;

public readonly record struct CommandResult(StateUpdate State, bool Duplicate);

/// <summary>One endpoint's game state. Sends ACK before looking up duplicate command results.</summary>
public sealed class GameSession(int dedupCapacity = 1024)
{
    private readonly RecentCommandWindow _recent = new(dedupCapacity);
    private float _x, _y, _z;
    private uint _shots;
    private byte _lastWeapon;

    public uint ShotsFired => _shots;
    public int RecentCount => _recent.Count;

    public CommandResult Handle(Packet packet, Action<byte[]> send)
    {
        if (packet.Payload is not (Movement or Shoot))
        {
            throw new ProtocolException("Unsupported client game payload");
        }

        if (packet.Header.RequiresAck)
        {
            send(ProtocolSerializer.SerializeAck(packet.Header.SequenceNumber,
                new AckPayload(packet.Header.SequenceNumber)));
        }

        if (packet.Header.RequiresAck && _recent.TryGet(packet.Header.SequenceNumber, out StateUpdate previous))
        {
            send(ProtocolSerializer.SerializeStateUpdate(packet.Header.SequenceNumber, previous));
            return new CommandResult(previous, true);
        }

        StatusCode status = StatusCode.Accepted;
        switch (packet.Payload)
        {
            case Movement movement:
                if (InWorld(movement.X) && InWorld(movement.Y) && InWorld(movement.Z))
                {
                    (_x, _y, _z) = (movement.X, movement.Y, movement.Z);
                }
                else
                {
                    status = StatusCode.OutOfRange;
                }
                break;
            case Shoot shoot:
                if (shoot.WeaponId is >= 1 and <= 3)
                {
                    _shots++;
                    _lastWeapon = shoot.WeaponId;
                }
                else
                {
                    status = StatusCode.OutOfRange;
                }
                break;
        }

        var state = new StateUpdate(packet.Header.PacketType, status, _x, _y, _z, _shots, _lastWeapon);
        if (packet.Header.RequiresAck)
        {
            _recent.Remember(packet.Header.SequenceNumber, state);
        }
        send(ProtocolSerializer.SerializeStateUpdate(packet.Header.SequenceNumber, state));
        return new CommandResult(state, false);
    }

    private static bool InWorld(float value) => float.IsFinite(value) && value is >= -1000 and <= 1000;
}
