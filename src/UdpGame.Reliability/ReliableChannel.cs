namespace UdpGame.Reliability;

/// <summary>Delivery state for one peer, owned by a single event loop; performs no network IO.</summary>
public sealed class ReliableChannel
{
    private readonly Dictionary<ushort, PendingPacket> _pending = [];
    private readonly List<FailedPacket> _failed = [];
    private readonly uint _maxAttempts;

    public ReliableChannel(uint maxAttempts = 5)
    {
        if (maxAttempts == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        _maxAttempts = maxAttempts;
    }

    public int PendingCount => _pending.Count;
    public int FailedCount => _failed.Count;
    public IReadOnlyList<FailedPacket> FailedPackets => Array.AsReadOnly(_failed.ToArray());

    public void OnSent(ushort sequence, byte[] rawBytes, ulong nowUs)
    {
        ArgumentNullException.ThrowIfNull(rawBytes);
        if (rawBytes.Length == 0)
        {
            throw new ArgumentException("Packet bytes must not be empty", nameof(rawBytes));
        }

        if (_pending.ContainsKey(sequence))
        {
            throw new InvalidOperationException($"Sequence {sequence} is already pending");
        }

        _pending.Add(sequence, new PendingPacket(sequence, rawBytes, nowUs, nowUs, 1));
    }

    public bool OnAckReceived(ushort sequence) => OnAckReceived(sequence, out _);

    /// <summary>Returns delivery metadata so callers can sample ACK RTT only when Attempts == 1 (Karn).</summary>
    public bool OnAckReceived(ushort sequence, out PendingPacket? acknowledgedPacket) =>
        _pending.Remove(sequence, out acknowledgedPacket);

    /// <summary>Reserves due retries and counts them as attempts; the caller must dispatch returned bytes.</summary>
    public IReadOnlyList<PendingPacket> CollectForRetransmission(ulong nowUs, ulong adaptiveTimeoutUs)
    {
        if (adaptiveTimeoutUs == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(adaptiveTimeoutUs));
        }

        var retransmissions = new List<PendingPacket>();
        foreach (PendingPacket packet in _pending.Values.ToArray())
        {
            // Subtract only after checking order: ulong timestamps must not underflow.
            if (nowUs < packet.LastSentAtUs || nowUs - packet.LastSentAtUs < adaptiveTimeoutUs)
            {
                continue;
            }

            if (packet.Attempts >= _maxAttempts)
            {
                _pending.Remove(packet.SequenceNumber);
                _failed.Add(new FailedPacket(
                    packet.SequenceNumber, packet.Attempts, packet.FirstSentAtUs, packet.LastSentAtUs));
                continue;
            }

            PendingPacket retry = packet.RetransmittedAt(nowUs);
            _pending[packet.SequenceNumber] = retry;
            retransmissions.Add(retry);
        }

        return retransmissions.AsReadOnly();
    }
}
