using UdpGame.Protocol;

namespace UdpGame.Protocol.Tests;

[TestClass]
public sealed class ProtocolTests
{
    [TestMethod]
    public void Movement_HasExpectedBigEndianWireFormat()
    {
        byte[] bytes = ProtocolSerializer.SerializeMovement(
            0x1234,
            new Movement(1f, -2.5f, 0.25f));

        byte[] expected =
        [
            0x01, 0x12, 0x34, 0x00, 0x0C, 0x00, 0x02, 0x00,
            0x3F, 0x80, 0x00, 0x00,
            0xC0, 0x20, 0x00, 0x00,
            0x3E, 0x80, 0x00, 0x00,
        ];

        CollectionAssert.AreEqual(expected, bytes);

        Packet packet = ProtocolSerializer.Deserialize(bytes);
        Assert.AreEqual(PacketType.Movement, packet.Header.PacketType);
        Assert.AreEqual((ushort)0x1234, packet.Header.SequenceNumber);
        Assert.AreEqual(ProtocolConstants.ProtocolVersion, packet.Header.ProtocolVersion);
        Assert.IsTrue(packet.Payload is Movement);

        var movement = (Movement)packet.Payload;
        Assert.AreEqual(1f, movement.X, 0.0001f);
        Assert.AreEqual(-2.5f, movement.Y, 0.0001f);
        Assert.AreEqual(0.25f, movement.Z, 0.0001f);
    }

    [TestMethod]
    public void Shoot_HasExpectedWireFormat()
    {
        byte[] bytes = ProtocolSerializer.SerializeShoot(7, new Shoot(3));
        byte[] expected = [0x02, 0x00, 0x07, 0x00, 0x01, 0x00, 0x02, 0x00, 0x03];

        CollectionAssert.AreEqual(expected, bytes);

        Packet packet = ProtocolSerializer.Deserialize(bytes);
        Assert.IsTrue(packet.Payload is Shoot);
        Assert.AreEqual((byte)3, ((Shoot)packet.Payload).WeaponId);
    }

    [TestMethod]
    public void StateUpdate_RoundTripsAndKeepsSequenceNumber()
    {
        var expected = new StateUpdate(
            PacketType.Shoot,
            StatusCode.Accepted,
            1f,
            2f,
            3f,
            42,
            2);

        Packet packet = ProtocolSerializer.Deserialize(
            ProtocolSerializer.SerializeStateUpdate(9, expected));

        Assert.AreEqual(PacketType.StateUpdate, packet.Header.PacketType);
        Assert.AreEqual((ushort)9, packet.Header.SequenceNumber);
        Assert.IsTrue(packet.Payload is StateUpdate);
        Assert.AreEqual(expected, (StateUpdate)packet.Payload);
    }

    [TestMethod]
    public void InvalidPayloadSize_IsRejected()
    {
        byte[] bytes = ProtocolSerializer.SerializeShoot(1, new Shoot(1));
        bytes[4] = 2;

        bool rejected = false;
        try
        {
            ProtocolSerializer.Deserialize(bytes);
        }
        catch (ProtocolException)
        {
            rejected = true;
        }

        Assert.IsTrue(rejected);
    }

    [TestMethod]
    public void Ping_SerializeDeserialize_UsesBigEndianU64()
    {
        const ulong sendTime = 0x0102030405060708UL;
        byte[] bytes = ProtocolSerializer.SerializePing(0x1234, new Ping(sendTime));

        byte[] expected =
        [
            0x04, 0x12, 0x34, 0x00, 0x08, 0x00, 0x02, 0x00,
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        ];
        CollectionAssert.AreEqual(expected, bytes);

        Packet packet = ProtocolSerializer.Deserialize(bytes);
        Assert.AreEqual(PacketType.Ping, packet.Header.PacketType);
        Assert.AreEqual((ushort)0x1234, packet.Header.SequenceNumber);
        Assert.AreEqual(new Ping(sendTime), (Ping)packet.Payload);
    }

    [TestMethod]
    public void Pong_SerializeDeserialize_RoundTrips()
    {
        var pong = new Pong(
            0x0102030405060708UL,
            0x1112131415161718UL,
            0x2122232425262728UL);

        byte[] bytes = ProtocolSerializer.SerializePong(9, pong);
        Packet packet = ProtocolSerializer.Deserialize(bytes);

        Assert.AreEqual(PacketType.Pong, packet.Header.PacketType);
        Assert.AreEqual((ushort)9, packet.Header.SequenceNumber);
        Assert.AreEqual(pong, (Pong)packet.Payload);
    }

    [TestMethod]
    public void U16_ReadWrite_UsesBigEndian()
    {
        var bytes = new byte[2];
        ProtocolSerializer.WriteU16(bytes, 0, 0x1234);

        CollectionAssert.AreEqual(new byte[] { 0x12, 0x34 }, bytes);
        Assert.AreEqual((ushort)0x1234, ProtocolSerializer.ReadU16(bytes, 0));
    }

    [TestMethod]
    public void U64_ReadWrite_UsesBigEndian()
    {
        var bytes = new byte[8];
        ProtocolSerializer.WriteU64(bytes, 0, 0x0102030405060708UL);

        CollectionAssert.AreEqual(
            new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            bytes);
        Assert.AreEqual(0x0102030405060708UL, ProtocolSerializer.ReadU64(bytes, 0));
    }

    [TestMethod]
    public void WrongProtocolVersion_IsRejected()
    {
        byte[] bytes = ProtocolSerializer.SerializePing(1, new Ping(100));
        bytes[6] = 0xFF;

        Assert.Throws<ProtocolException>(() => ProtocolSerializer.Deserialize(bytes));
    }

    [TestMethod]
    public void TruncatedPacket_IsRejected()
    {
        byte[] bytes = ProtocolSerializer.SerializePong(1, new Pong(10, 20, 30));
        byte[] truncated = bytes[..^1];

        Assert.Throws<ProtocolException>(() => ProtocolSerializer.Deserialize(truncated));
    }

    [TestMethod]
    public void UnknownPacketType_IsRejected()
    {
        byte[] bytes = ProtocolSerializer.SerializePing(1, new Ping(100));
        bytes[0] = 0xFF;

        Assert.Throws<ProtocolException>(() => ProtocolSerializer.Deserialize(bytes));
    }
}
