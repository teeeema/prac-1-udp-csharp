using System.Net;
using System.Net.Sockets;
using UdpGame.Protocol;

namespace UdpGame.Client;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            ClientOptions options = ParseOptions(args);
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = options.TimeoutMilliseconds;
            udp.Connect(options.Host, options.Port);

            int responsesReceived = 0;
            Console.WriteLine(
                $"UDP client sending {options.Count} commands to {options.Host}:{options.Port}");

            for (int index = 0; index < options.Count; index++)
            {
                ushort sequenceNumber = checked((ushort)(index + 1));
                byte[] datagram;

                if (index % 2 == 0)
                {
                    float step = index / 2 + 1;
                    var movement = new Movement(step, step * 2f, step * -0.5f);
                    datagram = ProtocolSerializer.SerializeMovement(sequenceNumber, movement);
                    Console.WriteLine(
                        $"Sent MOVEMENT seq={sequenceNumber} " +
                        $"position=({movement.X}, {movement.Y}, {movement.Z})");
                }
                else
                {
                    byte weaponId = (byte)(((index / 2) % 3) + 1);
                    datagram = ProtocolSerializer.SerializeShoot(
                        sequenceNumber,
                        new Shoot(weaponId));
                    Console.WriteLine($"Sent SHOOT seq={sequenceNumber} weaponId={weaponId}");
                }

                udp.Send(datagram, datagram.Length);

                try
                {
                    var sender = new IPEndPoint(IPAddress.Any, 0);
                    byte[] responseBytes = udp.Receive(ref sender);
                    Packet response = ProtocolSerializer.Deserialize(responseBytes);

                    if (response.Header.SequenceNumber != sequenceNumber)
                    {
                        throw new ProtocolException(
                            "Response SequenceNumber does not match the request");
                    }

                    PrintResponse(response);
                    responsesReceived++;
                }
                catch (SocketException error) when (
                    error.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
                {
                    Console.Error.WriteLine($"Timeout waiting for response to seq={sequenceNumber}");
                }

                if (index + 1 < options.Count)
                {
                    Thread.Sleep(options.IntervalMilliseconds);
                }
            }

            Console.WriteLine($"Completed: {responsesReceived}/{options.Count} responses received");
            return responsesReceived == options.Count ? 0 : 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Client error: {error.Message}");
            return 1;
        }
    }

    private static void PrintResponse(Packet packet)
    {
        if (packet.Header.PacketType != PacketType.StateUpdate || packet.Payload is not StateUpdate state)
        {
            throw new ProtocolException("Server response is not STATE_UPDATE");
        }

        Console.WriteLine(
            $"Received STATE_UPDATE seq={packet.Header.SequenceNumber} " +
            $"ack={PacketTypeName(state.AcknowledgedType)} status={StatusName(state.Status)} " +
            $"position=({state.X}, {state.Y}, {state.Z}) " +
            $"shots={state.ShotsFired} lastWeapon={state.LastWeaponId}");
    }

    private static ClientOptions ParseOptions(string[] args)
    {
        var options = new ClientOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host" when i + 1 < args.Length:
                    options.Host = args[++i];
                    break;
                case "--port" when i + 1 < args.Length:
                    options.Port = ParsePositiveInt(args[++i], "port", 65535);
                    break;
                case "--count" when i + 1 < args.Length:
                    options.Count = ParsePositiveInt(args[++i], "count", ushort.MaxValue);
                    break;
                case "--interval-ms" when i + 1 < args.Length:
                    options.IntervalMilliseconds = ParseNonNegativeInt(args[++i], "interval-ms");
                    break;
                case "--timeout-ms" when i + 1 < args.Length:
                    options.TimeoutMilliseconds = ParsePositiveInt(
                        args[++i],
                        "timeout-ms",
                        int.MaxValue);
                    break;
                case "--help":
                    Console.WriteLine(
                        "Usage: UdpGame.Client [--host 127.0.0.1] [--port 27015] " +
                        "[--count 6] [--interval-ms 500] [--timeout-ms 2000]");
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete argument: {args[i]}");
            }
        }

        return options;
    }

    private static int ParsePositiveInt(string text, string name, int maxValue)
    {
        if (!int.TryParse(text, out int value) || value <= 0 || value > maxValue)
        {
            throw new ArgumentException($"{name} must be in range 1..{maxValue}");
        }

        return value;
    }

    private static int ParseNonNegativeInt(string text, string name)
    {
        if (!int.TryParse(text, out int value) || value < 0)
        {
            throw new ArgumentException($"{name} must be zero or positive");
        }

        return value;
    }

    private static string PacketTypeName(PacketType type) => type switch
    {
        PacketType.Movement => "MOVEMENT",
        PacketType.Shoot => "SHOOT",
        PacketType.StateUpdate => "STATE_UPDATE",
        _ => "UNKNOWN",
    };

    private static string StatusName(StatusCode status) => status switch
    {
        StatusCode.Accepted => "ACCEPTED",
        StatusCode.OutOfRange => "OUT_OF_RANGE",
        _ => "UNKNOWN",
    };

    private sealed class ClientOptions
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 27015;
        public int Count { get; set; } = 6;
        public int IntervalMilliseconds { get; set; } = 500;
        public int TimeoutMilliseconds { get; set; } = 2000;
    }
}
