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
            0x01, 0x12, 0x34, 0x00, 0x0C,
            0x3F, 0x80, 0x00, 0x00,
            0xC0, 0x20, 0x00, 0x00,
            0x3E, 0x80, 0x00, 0x00,
        ];

        CollectionAssert.AreEqual(expected, bytes);

        Packet packet = ProtocolSerializer.Deserialize(bytes);
        Assert.AreEqual(PacketType.Movement, packet.Header.PacketType);
        Assert.AreEqual((ushort)0x1234, packet.Header.SequenceNumber);
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
        byte[] expected = [0x02, 0x00, 0x07, 0x00, 0x01, 0x03];

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
}
