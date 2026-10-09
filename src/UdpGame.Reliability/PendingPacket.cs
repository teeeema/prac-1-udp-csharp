namespace UdpGame.Reliability;

public sealed class PendingPacket
{
    private readonly byte[] _rawBytes;

    internal PendingPacket(
        ushort sequenceNumber,
        byte[] rawBytes,
        ulong firstSentAtUs,
        ulong lastSentAtUs,
        uint attempts)
    {
        SequenceNumber = sequenceNumber;
        _rawBytes = (byte[])rawBytes.Clone();
        FirstSentAtUs = firstSentAtUs;
        LastSentAtUs = lastSentAtUs;
        Attempts = attempts;
    }

    public ushort SequenceNumber { get; }
    public byte[] RawBytes => (byte[])_rawBytes.Clone();
    public ulong FirstSentAtUs { get; }
    public ulong LastSentAtUs { get; }
    public uint Attempts { get; }

    internal PendingPacket RetransmittedAt(ulong nowUs) =>
        new(SequenceNumber, _rawBytes, FirstSentAtUs, nowUs, Attempts + 1);
}

public readonly record struct FailedPacket(
    ushort SequenceNumber,
    uint Attempts,
    ulong FirstSentAtUs,
    ulong LastSentAtUs);
