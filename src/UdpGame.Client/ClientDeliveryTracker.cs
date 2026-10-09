using UdpGame.Protocol;
using UdpGame.Reliability;
using UdpGame.Telemetry;

namespace UdpGame.Client;

public readonly record struct DeliveryAcknowledgment(
    PendingPacket Packet, ulong ReceivedAtUs, double TimeToAckMs, bool SampleUsedForRto);

/// <summary>Client orchestration of delivery and telemetry; no socket ownership.</summary>
public sealed class ClientDeliveryTracker
{
    public ReliableChannel Channel { get; } = new();
    public AdaptiveTimeout Timeout { get; } = new();
    private readonly TelemetryTracker _telemetry = new();

    public static byte[] CreateShoot(ushort sequence, byte weaponId) =>
        ProtocolSerializer.SerializeShoot(sequence, new Shoot(weaponId), requiresAck: true);

    public void OnSent(ushort sequence, byte[] bytes, ulong nowUs) => Channel.OnSent(sequence, bytes, nowUs);

    public DeliveryAcknowledgment? OnAck(AckPayload ack, ulong nowUs)
    {
        if (!Channel.OnAckReceived(ack.AcknowledgedSequence, out PendingPacket? packet))
        {
            return null;
        }
        if (nowUs < packet!.FirstSentAtUs)
        {
            throw new ArgumentOutOfRangeException(nameof(nowUs), "ACK time precedes send time");
        }
        double elapsedMs = (nowUs - packet.FirstSentAtUs) / 1000d;
        bool useSample = packet.Attempts == 1;
        if (useSample)
        {
            Timeout.OnSample(elapsedMs);
        }
        return new DeliveryAcknowledgment(packet, nowUs, elapsedMs, useSample);
    }

    public IReadOnlyList<PendingPacket> CollectForRetransmission(ulong nowUs) =>
        Channel.CollectForRetransmission(nowUs, checked((ulong)Math.Ceiling(Timeout.RtoMs * 1000d)));

    public byte[] CreatePing(ushort sequence, ulong nowUs)
    {
        _telemetry.RegisterPing(sequence, nowUs, "reliability-rto");
        return ProtocolSerializer.SerializePing(sequence, new Ping(nowUs));
    }

    public double? OnPong(ushort sequence, Pong pong, ulong nowUs)
    {
        _telemetry.Expire(nowUs);
        InFlightMeasurement? measurement = _telemetry.Find(sequence);
        if (measurement is null || measurement.SendTimeUs != pong.ClientSendTimeUs)
        {
            return null;
        }
        PongResult result = _telemetry.RecordPong(sequence, nowUs);
        if (result.Kind != PongResultKind.Received)
        {
            return null;
        }
        double rtt = result.Measurement!.RttMs!.Value;
        Timeout.OnSample(rtt);
        return rtt;
    }
}
