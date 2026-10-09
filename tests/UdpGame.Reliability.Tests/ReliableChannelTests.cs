using UdpGame.Reliability;

namespace UdpGame.Reliability.Tests;

[TestClass]
public sealed class ReliableChannelTests
{
    private static ReliableChannel Sent(uint maxAttempts = 5)
    {
        var channel = new ReliableChannel(maxAttempts);
        channel.OnSent(1, [1, 2, 3], 1000);
        return channel;
    }

    [TestMethod]
    public void PacketIsNotRetransmittedBeforeRto()
    {
        Assert.HasCount(0, Sent().CollectForRetransmission(1099, 100));
    }

    [TestMethod]
    public void PacketIsRetransmittedAfterRto()
    {
        var channel = Sent();
        IReadOnlyList<PendingPacket> packets = channel.CollectForRetransmission(1100, 100);
        Assert.HasCount(1, packets);
        Assert.AreEqual((ushort)1, packets[0].SequenceNumber);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, packets[0].RawBytes);
    }

    [TestMethod]
    public void RetransmissionIncrementsAttempts()
    {
        var channel = Sent();
        Assert.AreEqual(2u, channel.CollectForRetransmission(1100, 100)[0].Attempts);
        Assert.AreEqual(3u, channel.CollectForRetransmission(1200, 100)[0].Attempts);
    }

    [TestMethod]
    public void RetransmissionUpdatesLastSentTime()
    {
        PendingPacket retry = Sent().CollectForRetransmission(1100, 100)[0];
        Assert.AreEqual(1100UL, retry.LastSentAtUs);
        Assert.AreEqual(1000UL, retry.FirstSentAtUs);
    }

    [TestMethod]
    public void AckRemovesPendingPacket()
    {
        var channel = Sent();
        Assert.IsTrue(channel.OnAckReceived(1));
        Assert.AreEqual(0, channel.PendingCount);
        Assert.HasCount(0, channel.CollectForRetransmission(1100, 100));
    }

    [TestMethod]
    public void DuplicateAckIsIgnored()
    {
        var channel = Sent();
        Assert.IsTrue(channel.OnAckReceived(1));
        Assert.IsFalse(channel.OnAckReceived(1));
        Assert.AreEqual(0, channel.FailedCount);
    }

    [TestMethod]
    public void UnknownAckIsIgnored()
    {
        var channel = Sent();
        Assert.IsFalse(channel.OnAckReceived(99, out PendingPacket? packet));
        Assert.IsNull(packet);
        Assert.AreEqual(1, channel.PendingCount);
    }

    [TestMethod]
    public void PacketMovesToFailedAfterMaxAttempts()
    {
        var channel = Sent();
        for (ulong attempt = 2; attempt <= 5; attempt++)
        {
            PendingPacket retry = channel.CollectForRetransmission(1000 + (attempt - 1) * 100, 100)[0];
            Assert.AreEqual((uint)attempt, retry.Attempts);
            Assert.AreEqual(0, channel.FailedCount);
        }

        Assert.HasCount(0, channel.CollectForRetransmission(1499, 100));
        Assert.AreEqual(0, channel.FailedCount);
        Assert.HasCount(0, channel.CollectForRetransmission(1500, 100));
        Assert.AreEqual(1, channel.FailedCount);
        Assert.AreEqual(new FailedPacket(1, 5, 1000, 1400), channel.FailedPackets[0]);
        Assert.HasCount(0, channel.CollectForRetransmission(1600, 100));
        Assert.AreEqual(1, channel.FailedCount);
    }

    [TestMethod]
    public void FailedPacketIsRemovedFromPending()
    {
        var channel = Sent(maxAttempts: 1);
        Assert.HasCount(0, channel.CollectForRetransmission(1100, 100));
        Assert.AreEqual(0, channel.PendingCount);
        Assert.AreEqual(1, channel.FailedCount);
        Assert.IsFalse(channel.OnAckReceived(1));
    }

    [TestMethod]
    public void SamePacketIsNotReturnedTwiceBeforeNextRto()
    {
        var channel = Sent();
        Assert.HasCount(1, channel.CollectForRetransmission(1100, 100));
        Assert.HasCount(0, channel.CollectForRetransmission(1100, 100));
        Assert.HasCount(0, channel.CollectForRetransmission(1199, 100));
        Assert.HasCount(1, channel.CollectForRetransmission(1200, 100));
    }

    [TestMethod]
    public void MultiplePendingPacketsAreHandledIndependently()
    {
        var channel = Sent(maxAttempts: 2);
        channel.OnSent(2, [4], 1050);
        Assert.AreEqual((ushort)1, channel.CollectForRetransmission(1100, 100)[0].SequenceNumber);
        Assert.AreEqual((ushort)2, channel.CollectForRetransmission(1150, 100)[0].SequenceNumber);
        Assert.IsTrue(channel.OnAckReceived(2));
        Assert.HasCount(0, channel.CollectForRetransmission(1200, 100));
        Assert.AreEqual(0, channel.PendingCount);
        Assert.AreEqual(1, channel.FailedCount);
        Assert.AreEqual((ushort)1, channel.FailedPackets[0].SequenceNumber);
    }

    [TestMethod]
    public void DuplicatePendingSequenceIsHandledDeterministically()
    {
        var channel = Sent();
        Assert.Throws<InvalidOperationException>(() => channel.OnSent(1, [9], 1050));
        Assert.IsTrue(channel.OnAckReceived(1, out PendingPacket? packet));
        Assert.AreEqual(1u, packet!.Attempts);
        Assert.AreEqual(1000UL, packet.FirstSentAtUs);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, packet.RawBytes);
    }

    [TestMethod]
    public void AckMetadataAllowsKarnFiltering()
    {
        var channel = Sent();
        Assert.IsTrue(channel.OnAckReceived(1, out PendingPacket? first));
        Assert.AreEqual(1u, first!.Attempts);
        Assert.AreEqual(1000UL, first.LastSentAtUs);
        channel.OnSent(2, [4], 2000);
        channel.CollectForRetransmission(2100, 100);
        Assert.IsTrue(channel.OnAckReceived(2, out PendingPacket? retransmitted));
        Assert.AreEqual(2u, retransmitted!.Attempts);
        Assert.AreEqual(2000UL, retransmitted.FirstSentAtUs);
    }

    [TestMethod]
    public void PacketBytesAndMetadataAreStableSnapshots()
    {
        var channel = new ReliableChannel();
        byte[] bytes = [1, 2];
        channel.OnSent(1, bytes, 0);
        bytes[0] = 9;
        PendingPacket first = channel.CollectForRetransmission(100, 100)[0];
        first.RawBytes[0] = 8;
        PendingPacket second = channel.CollectForRetransmission(200, 100)[0];
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, second.RawBytes);
        Assert.AreEqual(2u, first.Attempts);
        Assert.AreEqual(100UL, first.LastSentAtUs);
    }

    [TestMethod]
    public void FailedPacketsAreReadOnlySnapshots()
    {
        var channel = Sent(maxAttempts: 1);
        IReadOnlyList<FailedPacket> before = channel.FailedPackets;
        channel.CollectForRetransmission(1100, 100);
        Assert.HasCount(0, before);
        var failed = (IList<FailedPacket>)channel.FailedPackets;
        Assert.Throws<NotSupportedException>(() => failed.Clear());
        Assert.AreEqual(1, channel.FailedCount);
    }

    [TestMethod]
    public void EarlierTimeDoesNotUnderflowOrRetransmit()
    {
        Assert.HasCount(0, Sent().CollectForRetransmission(999, 100));
    }

    [TestMethod]
    public void InvalidArgumentsDoNotCreatePendingEntries()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReliableChannel(0));
        var channel = new ReliableChannel();
        Assert.Throws<ArgumentNullException>(() => channel.OnSent(1, null!, 0));
        Assert.Throws<ArgumentException>(() => channel.OnSent(1, [], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => channel.CollectForRetransmission(0, 0));
        Assert.AreEqual(0, channel.PendingCount);
    }

    [TestMethod]
    public void SequenceBoundaryAndLargeTimestampAreSupported()
    {
        var channel = new ReliableChannel();
        channel.OnSent(0, [1], ulong.MaxValue - 100);
        channel.OnSent(ushort.MaxValue, [2], ulong.MaxValue - 100);
        Assert.HasCount(2, channel.CollectForRetransmission(ulong.MaxValue, 100));
        Assert.IsTrue(channel.OnAckReceived(0));
        Assert.IsTrue(channel.OnAckReceived(ushort.MaxValue));
    }

    [TestMethod]
    public void ChangedAdaptiveTimeoutIsUsedOnNextCollection()
    {
        var channel = Sent();
        Assert.HasCount(0, channel.CollectForRetransmission(1150, 200));
        Assert.HasCount(1, channel.CollectForRetransmission(1150, 100));
    }
}
