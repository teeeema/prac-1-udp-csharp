using System.Net;
using System.Net.Sockets;
using UdpGame.Client;
using UdpGame.Protocol;
using UdpGame.Server;
using UdpGame.Transport;

namespace UdpGame.Integration.Tests;

[TestClass]
public sealed class DeliveryIntegrationTests
{
    private static Packet ShootPacket(ushort sequence = 42, byte weapon = 1) =>
        ProtocolSerializer.Deserialize(ClientDeliveryTracker.CreateShoot(sequence, weapon));

    [TestMethod]
    public void ReliableShootRequiresAckAndRetryPreservesRawBytesAndSequence()
    {
        var client = new ClientDeliveryTracker();
        byte[] bytes = ClientDeliveryTracker.CreateShoot(42, 1);
        client.OnSent(42, bytes, 0);
        var retry = client.CollectForRetransmission(1_000_000)[0];
        CollectionAssert.AreEqual(bytes, retry.RawBytes);
        Packet packet = ProtocolSerializer.Deserialize(retry.RawBytes);
        Assert.AreEqual((ushort)42, packet.Header.SequenceNumber);
        Assert.IsTrue(packet.Header.RequiresAck);
    }

    [TestMethod]
    public void FirstAttemptAckUpdatesAdaptiveTimeoutAndRemovesPending()
    {
        var client = new ClientDeliveryTracker();
        client.OnSent(42, ClientDeliveryTracker.CreateShoot(42, 1), 0);
        DeliveryAcknowledgment ack = client.OnAck(new AckPayload(42), 200_000)!.Value;
        Assert.AreEqual(0, client.Channel.PendingCount);
        Assert.IsTrue(ack.SampleUsedForRto);
        Assert.AreEqual(200d, ack.TimeToAckMs);
        Assert.AreEqual(600d, client.Timeout.RtoMs);
    }

    [TestMethod]
    public void DuplicateAndUnknownAckDoNotUpdateRto()
    {
        var client = new ClientDeliveryTracker();
        client.OnSent(42, [1], 0);
        client.OnAck(new AckPayload(42), 200_000);
        Assert.IsNull(client.OnAck(new AckPayload(42), 900_000));
        Assert.IsNull(client.OnAck(new AckPayload(99), 900_000));
        Assert.AreEqual(600d, client.Timeout.RtoMs);
    }

    [TestMethod]
    public void RetransmittedAckDoesNotUpdateAdaptiveTimeout()
    {
        var client = new ClientDeliveryTracker();
        client.OnSent(42, [1], 0);
        client.CollectForRetransmission(1_000_000);
        DeliveryAcknowledgment ack = client.OnAck(new AckPayload(42), 1_100_000)!.Value;
        Assert.IsFalse(ack.SampleUsedForRto);
        Assert.AreEqual(2u, ack.Packet.Attempts);
        Assert.AreEqual(1100d, ack.TimeToAckMs);
        Assert.IsFalse(client.Timeout.IsInitialized);
        Assert.AreEqual(1000d, client.Timeout.RtoMs);
    }

    [TestMethod]
    public void SuccessfulPingUpdatesAdaptiveRtoWithoutRequiresAck()
    {
        var client = new ClientDeliveryTracker();
        Packet ping = ProtocolSerializer.Deserialize(client.CreatePing(7, 10_000));
        Assert.IsFalse(ping.Header.RequiresAck);
        Assert.AreEqual(150d, client.OnPong(7, new Pong(10_000, 0, 0), 160_000));
        Assert.AreEqual(450d, client.Timeout.RtoMs);
        Assert.IsNull(client.OnPong(7, new Pong(10_000, 0, 0), 170_000));
        Assert.AreEqual(450d, client.Timeout.RtoMs);
    }

    [TestMethod]
    public void WrongOrExpiredPongCannotUpdateAdaptiveRto()
    {
        var client = new ClientDeliveryTracker();
        client.CreatePing(7, 10_000);
        Assert.IsNull(client.OnPong(7, new Pong(11_000, 0, 0), 160_000));
        Assert.IsNull(client.OnPong(7, new Pong(10_000, 0, 0), 1_010_000));
        Assert.IsFalse(client.Timeout.IsInitialized);
    }

    [TestMethod]
    public void ReliableShootSendsAckBeforeGameEffect()
    {
        var session = new GameSession();
        var packets = new List<Packet>();
        session.Handle(ShootPacket(), bytes =>
        {
            Packet packet = ProtocolSerializer.Deserialize(bytes);
            if (packet.Payload is AckPayload ack)
            {
                Assert.AreEqual((ushort)42, ack.AcknowledgedSequence);
                Assert.AreEqual(0u, session.ShotsFired);
                Assert.IsFalse(packet.Header.RequiresAck);
            }
            packets.Add(packet);
        });
        Assert.AreEqual(PacketType.Ack, packets[0].Header.PacketType);
        Assert.AreEqual(PacketType.StateUpdate, packets[1].Header.PacketType);
        Assert.IsFalse(packets[1].Header.RequiresAck);
        Assert.AreEqual(1u, session.ShotsFired);
    }

    [TestMethod]
    public void DuplicateShootGetsAckAndCachedStateWithoutAnotherEffect()
    {
        var session = new GameSession();
        var packets = new List<Packet>();
        CommandResult first = session.Handle(ShootPacket(), _ => { });
        CommandResult duplicate = session.Handle(ShootPacket(), bytes => packets.Add(ProtocolSerializer.Deserialize(bytes)));
        Assert.IsTrue(duplicate.Duplicate);
        Assert.AreEqual(first.State, duplicate.State);
        Assert.HasCount(2, packets);
        Assert.AreEqual(PacketType.Ack, packets[0].Header.PacketType);
        Assert.AreEqual(1u, session.ShotsFired);
    }

    [TestMethod]
    public void DifferentShootsApplyTwiceAndSessionsAreIndependent()
    {
        var first = new GameSession();
        var second = new GameSession();
        first.Handle(ShootPacket(1), _ => { });
        first.Handle(ShootPacket(2), _ => { });
        second.Handle(ShootPacket(1), _ => { });
        Assert.AreEqual(2u, first.ShotsFired);
        Assert.AreEqual(1u, second.ShotsFired);
    }

    [TestMethod]
    public void DedupWindowHasBoundedCapacityAndEvictsOldest()
    {
        var session = new GameSession(dedupCapacity: 2);
        foreach (ushort sequence in new ushort[] { 1, 2, 3 }) session.Handle(ShootPacket(sequence), _ => { });
        Assert.AreEqual(2, session.RecentCount);
        Assert.IsTrue(session.Handle(ShootPacket(2), _ => { }).Duplicate);
        Assert.IsFalse(session.Handle(ShootPacket(1), _ => { }).Duplicate);
        Assert.AreEqual(4u, session.ShotsFired);
        Assert.AreEqual(2, session.RecentCount);
    }

    [TestMethod]
    public void RolloverFrom65535ToZeroDoesNotSuppressNewCommand()
    {
        var session = new GameSession();
        session.Handle(ShootPacket(65535), _ => { });
        session.Handle(ShootPacket(0), _ => { });
        Assert.IsTrue(session.Handle(ShootPacket(65535), _ => { }).Duplicate);
        Assert.AreEqual(2u, session.ShotsFired);
    }

    [TestMethod]
    public void InvalidWeaponIsAckedButDoesNotApplyAndItsResultIsCached()
    {
        var session = new GameSession();
        var packets = new List<Packet>();
        CommandResult first = session.Handle(ShootPacket(42, 0), bytes => packets.Add(ProtocolSerializer.Deserialize(bytes)));
        Assert.AreEqual(PacketType.Ack, packets[0].Header.PacketType);
        Assert.AreEqual(StatusCode.OutOfRange, first.State.Status);
        CommandResult duplicate = session.Handle(ShootPacket(42, 1), _ => { });
        Assert.AreEqual(first.State, duplicate.State);
        Assert.AreEqual(0u, session.ShotsFired);
    }

    [TestMethod]
    public void MovementRemainsUnreliableAndDoesNotCreateAckOrDedupEntry()
    {
        var session = new GameSession();
        Packet packet = ProtocolSerializer.Deserialize(ProtocolSerializer.SerializeMovement(1, new Movement(1, 2, 3)));
        var responses = new List<Packet>();
        session.Handle(packet, bytes => responses.Add(ProtocolSerializer.Deserialize(bytes)));
        Assert.HasCount(1, responses);
        Assert.AreEqual(PacketType.StateUpdate, responses[0].Header.PacketType);
        Assert.AreEqual(0, session.RecentCount);
    }

    [TestMethod]
    public void IncomingAckCannotCreateAckLoopOrGameEffect()
    {
        var session = new GameSession();
        int sent = 0;
        Packet ack = ProtocolSerializer.Deserialize(ProtocolSerializer.SerializeAck(1, new AckPayload(42)));
        Assert.Throws<ProtocolException>(() => session.Handle(ack, _ => sent++));
        Assert.AreEqual(0, sent);
        Assert.AreEqual(0u, session.ShotsFired);
    }

    [TestMethod]
    public void InvalidDedupCapacityIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentCommandWindow(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentCommandWindow(65536));
    }

    [TestMethod]
    public void LostAckCausesRetransmissionWithoutDuplicateShootEffect()
    {
        // Real UDP + production client loop + production GameSession; deterministically discard first ACK.
        using var server = new UdpClient(0);
        server.Client.ReceiveTimeout = 3000;
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var transport = UdpTransport.Connect("127.0.0.1", port, 20);
        var client = new ClientDeliveryTracker();
        client.Timeout.OnSample(1); // RTO = 100 ms for this test.
        var session = new GameSession();
        int ackCount = 0;
        byte[]? original = null;
        Task worker = Task.Run(() =>
        {
            for (int i = 0; i < 2; i++)
            {
                var remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] bytes = server.Receive(ref remote);
                if (original is null) original = bytes;
                else CollectionAssert.AreEqual(original, bytes);
                session.Handle(ProtocolSerializer.Deserialize(bytes), response =>
                {
                    if (ProtocolSerializer.Deserialize(response).Payload is AckPayload)
                    {
                        ackCount++;
                        if (ackCount == 1) return;
                    }
                    server.Send(response, response.Length, remote);
                });
            }
        });
        ShootDeliveryResult result = ReliableShootSender.Send(transport, client, 42, 1, waitForState: true);
        Assert.IsTrue(worker.Wait(5000));
        worker.GetAwaiter().GetResult();
        Assert.IsTrue(result.Delivered);
        Assert.AreEqual(2u, result.Attempts);
        Assert.IsFalse(result.SampleUsedForRto);
        Assert.AreEqual(1u, session.ShotsFired);
        Assert.AreEqual(2, ackCount);
        Assert.AreEqual(0, client.Channel.PendingCount);
        Assert.AreEqual(100d, client.Timeout.RtoMs);
    }

    [TestMethod]
    public void TotalShootLossBecomesFailedWithoutAnException()
    {
        using var server = new UdpClient(0);
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var loss = new NetworkProfile("all-lost", 0, 0, 0, 100, 1);
        using var transport = UdpTransport.Connect("127.0.0.1", port, 20, loss);
        var client = new ClientDeliveryTracker();
        client.Timeout.OnSample(1);
        var logs = new List<string>();
        ShootDeliveryResult result = ReliableShootSender.Send(transport, client, 42, 1, log: logs.Add);
        Assert.IsFalse(result.Delivered);
        Assert.AreEqual(5u, result.Attempts);
        Assert.IsNull(result.TimeToAckMs);
        Assert.AreEqual(1, client.Channel.FailedCount);
        Assert.AreEqual(0, client.Channel.PendingCount);
        Assert.AreEqual(5, transport.DroppedSendCount);
        Assert.IsTrue(logs.Any(text => text.Contains("delivery failed after 5 attempts")));
    }

    [TestMethod]
    public void IncomingEmulatorCanDropRealUdpAck()
    {
        using var server = new UdpClient(0);
        server.Client.ReceiveTimeout = 2000;
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var loss = new NetworkProfile("ack-loss", 0, 0, 0, 100, 2);
        using var transport = UdpTransport.Connect("127.0.0.1", port, 20, receiveProfile: loss);
        transport.Send(ClientDeliveryTracker.CreateShoot(42, 1));
        var remote = new IPEndPoint(IPAddress.Any, 0);
        server.Receive(ref remote);
        byte[] ack = ProtocolSerializer.SerializeAck(42, new AckPayload(42));
        server.Send(ack, ack.Length, remote);
        Assert.Throws<SocketException>(() => transport.Receive(ref remote));
        Assert.AreEqual(1, transport.DroppedReceiveCount);
    }

    [TestMethod]
    public void FixedSeedReproducesEmulatorDecisionsAndProfilesAreCorrect()
    {
        Assert.HasCount(6, NetworkProfiles.ReliabilityProfiles);
        foreach (NetworkProfile profile in NetworkProfiles.ReliabilityProfiles)
        {
            var a = new NetworkEmulator(profile);
            var b = new NetworkEmulator(profile);
            for (int i = 0; i < 100; i++) Assert.AreEqual(a.Next(), b.Next());
        }
        Assert.AreEqual(20d, NetworkProfiles.ReliabilityProfiles.Single(p => p.Id == "loss_20").LossPercent);
        Assert.AreEqual(100, NetworkProfiles.ReliabilityProfiles.Single(p => p.Id == "delay_100_loss_5").BaseDelayMs);
    }
    [TestMethod]
    public void ReceiveDelayAllowsRetransmissionPollingAndEventuallyDelivers()
    {
        using var server = new UdpClient(0);
        server.Client.ReceiveTimeout = 2000;
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var delay = new NetworkProfile("delayed-ack", 150, 0, 0, 0, 2);
        using var transport = UdpTransport.Connect("127.0.0.1", port, 20, receiveProfile: delay);
        transport.Send(ClientDeliveryTracker.CreateShoot(42, 1));
        var remote = new IPEndPoint(IPAddress.Any, 0);
        server.Receive(ref remote);
        byte[] ack = ProtocolSerializer.SerializeAck(42, new AckPayload(42));
        server.Send(ack, ack.Length, remote);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<SocketException>(() => transport.Receive(ref remote));
        Assert.IsLessThan(140L, clock.ElapsedMilliseconds, "Emulated delay must not block the client until ACK release");
        Packet? received = null;
        int ticks = 1;
        while (received is null && clock.ElapsedMilliseconds < 2000)
        {
            try { received = ProtocolSerializer.Deserialize(transport.Receive(ref remote)); }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut) { ticks++; }
        }
        Assert.IsNotNull(received);
        Assert.AreEqual(new AckPayload(42), (AckPayload)received.Payload);
        Assert.IsGreaterThanOrEqualTo(150L, clock.ElapsedMilliseconds);
        Assert.IsGreaterThan(1, ticks);
    }

}
