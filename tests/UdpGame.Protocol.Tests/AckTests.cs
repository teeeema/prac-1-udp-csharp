using UdpGame.Protocol;

namespace UdpGame.Protocol.Tests;

[TestClass]
public sealed class AckTests
{
    [TestMethod]
    public void AckSerializeDeserializeRoundTrip()
    {
        var expected = new AckPayload(0x1234);
        Packet packet = ProtocolSerializer.Deserialize(ProtocolSerializer.SerializeAck(9, expected));
        Assert.AreEqual(PacketType.Ack, packet.Header.PacketType);
        Assert.AreEqual((ushort)9, packet.Header.SequenceNumber);
        Assert.AreEqual((ushort)2, packet.Header.PayloadSize);
        Assert.AreEqual(ProtocolConstants.ProtocolVersion, packet.Header.ProtocolVersion);
        Assert.IsFalse(packet.Header.RequiresAck);
        Assert.AreEqual(expected, (AckPayload)packet.Payload);
    }

    [TestMethod]
    public void AckPayloadIsBigEndian()
    {
        byte[] bytes = ProtocolSerializer.SerializeAck(0x5678, new AckPayload(0x1234));
        CollectionAssert.AreEqual(new byte[] { 6, 0x56, 0x78, 0, 2, 0, 2, 0, 0x12, 0x34 }, bytes);
    }

    [TestMethod]
    public void RequiresAckSerializeDeserializeRoundTrip()
    {
        foreach (bool requiresAck in new[] { false, true })
        {
            byte[][] packets =
            [
                ProtocolSerializer.SerializeMovement(1, new Movement(1, 2, 3), requiresAck),
                ProtocolSerializer.SerializeShoot(2, new Shoot(3), requiresAck),
                ProtocolSerializer.SerializeStateUpdate(3,
                    new StateUpdate(PacketType.Shoot, StatusCode.Accepted, 1, 2, 3, 4, 2), requiresAck),
            ];
            foreach (byte[] bytes in packets)
            {
                Assert.AreEqual(requiresAck ? (byte)1 : (byte)0, bytes[7]);
                Assert.AreEqual(requiresAck, ProtocolSerializer.Deserialize(bytes).Header.RequiresAck);
            }
        }
    }

    [TestMethod]
    public void AckCannotRequireAck()
    {
        byte[] bytes = ProtocolSerializer.SerializeAck(1, new AckPayload(2));
        Assert.AreEqual((byte)0, bytes[7]);
        bytes[7] = 1;
        Assert.Throws<ProtocolException>(() => ProtocolSerializer.Deserialize(bytes));
    }

    [TestMethod]
    public void InvalidRequiresAckIsRejected()
    {
        foreach (byte invalid in new byte[] { 2, 127, 255 })
        {
            byte[] bytes = ProtocolSerializer.SerializeShoot(1, new Shoot(1));
            bytes[7] = invalid;
            Assert.Throws<ProtocolException>(() => ProtocolSerializer.Deserialize(bytes));
        }
    }

    [TestMethod]
    public void InvalidAckPayloadSizeIsRejected()
    {
        foreach (ushort size in new ushort[] { 0, 1, 3 })
        {
            // Match declared datagram length: exercise ACK type validation, not just outer length checks.
            var bytes = new byte[ProtocolConstants.HeaderSize + size];
            ProtocolSerializer.SerializeAck(1, new AckPayload(2)).AsSpan(0, ProtocolConstants.HeaderSize).CopyTo(bytes);
            ProtocolSerializer.WriteU16(bytes, 3, size);
            Assert.Throws<ProtocolException>(() => ProtocolSerializer.Deserialize(bytes));
        }
    }

    [TestMethod]
    public void ExistingPacketTypesStillRoundTrip()
    {
        (byte[] Bytes, PacketType Type, object Payload)[] cases =
        [
            (ProtocolSerializer.SerializeMovement(1, new Movement(1, 2, 3)), PacketType.Movement, new Movement(1, 2, 3)),
            (ProtocolSerializer.SerializeShoot(1, new Shoot(3)), PacketType.Shoot, new Shoot(3)),
            (ProtocolSerializer.SerializeStateUpdate(1, new StateUpdate(PacketType.Shoot, StatusCode.Accepted, 1, 2, 3, 4, 2)),
                PacketType.StateUpdate, new StateUpdate(PacketType.Shoot, StatusCode.Accepted, 1, 2, 3, 4, 2)),
            (ProtocolSerializer.SerializePing(1, new Ping(10)), PacketType.Ping, new Ping(10)),
            (ProtocolSerializer.SerializePong(1, new Pong(10, 20, 30)), PacketType.Pong, new Pong(10, 20, 30)),
        ];
        for (int index = 0; index < cases.Length; index++)
        {
            var test = cases[index];
            Packet packet = ProtocolSerializer.Deserialize(test.Bytes);
            Assert.AreEqual((byte)(index + 1), test.Bytes[0]);
            Assert.AreEqual(test.Type, packet.Header.PacketType);
            Assert.AreEqual(test.Payload, packet.Payload);
            Assert.IsFalse(packet.Header.RequiresAck);
        }
    }

    [TestMethod]
    public void TruncatedAckAndHeaderAreRejectedBeforePayloadRead()
    {
        byte[] bytes = ProtocolSerializer.SerializeAck(1, new AckPayload(2));
        for (int length = 0; length < bytes.Length; length++)
        {
            byte[] truncated = bytes[..length];
            Assert.Throws<ProtocolException>(() => ProtocolSerializer.Deserialize(truncated));
        }
    }

    [TestMethod]
    public void VersionOneWirePacketIsRejected()
    {
        byte[] oldShoot = [2, 0, 1, 0, 1, 0, 1, 3];
        Assert.Throws<ProtocolException>(() => ProtocolSerializer.Deserialize(oldShoot));
    }

    [TestMethod]
    public void OversizedPacketIsRejected()
    {
        Assert.Throws<ProtocolException>(() => ProtocolSerializer.Deserialize(new byte[1025]));
    }
}
