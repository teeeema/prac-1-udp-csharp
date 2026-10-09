using System.Net;
using System.Net.Sockets;
using UdpGame.Protocol;
using UdpGame.Reliability;
using UdpGame.Telemetry;
using UdpGame.Transport;

namespace UdpGame.Client;

public readonly record struct ShootDeliveryResult(
    ushort Sequence, ulong FirstSentAtUs, ulong? AckReceivedAtUs, uint Attempts,
    double? TimeToAckMs, bool SampleUsedForRto, StateUpdate? State)
{
    public bool Delivered => AckReceivedAtUs.HasValue;
}

/// <summary>The ordinary client and PR3 runner share this real UDP retransmission/ACK loop.</summary>
public static class ReliableShootSender
{
    public static ShootDeliveryResult Send(
        UdpTransport transport, ClientDeliveryTracker tracker, ushort sequence, byte weaponId,
        bool waitForState = false, int stateTimeoutMs = 2000, Action<string>? log = null)
    {
        byte[] bytes = ClientDeliveryTracker.CreateShoot(sequence, weaponId);
        ulong firstSentUs = MonotonicClock.NowMicroseconds();
        transport.Send(bytes);
        tracker.OnSent(sequence, bytes, firstSentUs);
        DeliveryAcknowledgment? acknowledged = null;
        StateUpdate? state = null;
        ulong? stateDeadlineUs = null;
        int failedBefore = tracker.Channel.FailedCount;

        while (true)
        {
            ulong nowUs = MonotonicClock.NowMicroseconds();
            foreach (PendingPacket retry in tracker.CollectForRetransmission(nowUs))
            {
                transport.Send(retry.RawBytes);
                log?.Invoke($"SHOOT seq={retry.SequenceNumber} retransmission attempt={retry.Attempts}");
            }

            if (tracker.Channel.FailedCount > failedBefore)
            {
                FailedPacket failed = tracker.Channel.FailedPackets[^1];
                log?.Invoke($"SHOOT seq={failed.SequenceNumber} delivery failed after {failed.Attempts} attempts");
                return new ShootDeliveryResult(sequence, firstSentUs, null, failed.Attempts, null, false, state);
            }

            if (acknowledged is not null &&
                (!waitForState || state.HasValue || nowUs >= stateDeadlineUs!.Value))
            {
                var ack = acknowledged.Value;
                return new ShootDeliveryResult(sequence, firstSentUs, ack.ReceivedAtUs,
                    ack.Packet.Attempts, ack.TimeToAckMs, ack.SampleUsedForRto, state);
            }

            try
            {
                var sender = new IPEndPoint(IPAddress.Any, 0);
                Packet packet = ProtocolSerializer.Deserialize(transport.Receive(ref sender));
                ulong receivedUs = MonotonicClock.NowMicroseconds();
                switch (packet.Payload)
                {
                    case AckPayload ack:
                        DeliveryAcknowledgment? result = tracker.OnAck(ack, receivedUs);
                        if (result is not null && result.Value.Packet.SequenceNumber == sequence)
                        {
                            acknowledged = result;
                            stateDeadlineUs = receivedUs + checked((ulong)stateTimeoutMs * 1000UL);
                            log?.Invoke($"ACK seq={sequence} attempts={result.Value.Packet.Attempts} " +
                                $"Karn_sample={result.Value.SampleUsedForRto} RTO={tracker.Timeout.RtoMs:F3} ms");
                        }
                        break;
                    case StateUpdate update when packet.Header.SequenceNumber == sequence:
                        state ??= update;
                        break;
                    case Pong pong:
                        tracker.OnPong(packet.Header.SequenceNumber, pong, receivedUs);
                        break;
                }
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
            {
            }
            catch (ProtocolException error)
            {
                log?.Invoke($"Malformed response ignored: {error.Message}");
            }
        }
    }
}
