using System.Net;
using System.Net.Sockets;
using UdpGame.Protocol;

namespace UdpGame.Server;

internal static class Program
{
    private const int DefaultPort = 27015;
    private const float WorldLimit = 1000f;

    private static int Main(string[] args)
    {
        try
        {
            ServerOptions options = ParseOptions(args);
            using var udp = new UdpClient(options.Port);
            using var logFile = new StreamWriter(options.LogFile, append: true) { AutoFlush = true };
            var clients = new Dictionary<string, ClientState>();

            Log(logFile, $"authoritative UDP server listening on 0.0.0.0:{options.Port}");

            int processedPackets = 0;
            while (options.MaxPackets == 0 || processedPackets < options.MaxPackets)
            {
                var sender = new IPEndPoint(IPAddress.Any, 0);
                byte[] datagram = udp.Receive(ref sender);
                string clientKey = sender.ToString();

                try
                {
                    Packet packet = ProtocolSerializer.Deserialize(datagram);

                    if (packet.Header.PacketType == PacketType.StateUpdate)
                    {
                        Log(logFile, $"REJECTED from={clientKey} reason=client sent server-only STATE_UPDATE");
                        continue;
                    }

                    if (!clients.TryGetValue(clientKey, out ClientState? state))
                    {
                        state = new ClientState();
                        clients[clientKey] = state;
                    }

                    StatusCode status = StatusCode.Accepted;
                    string details;

                    if (packet.Payload is Movement movement)
                    {
                        details = $"x={movement.X} y={movement.Y} z={movement.Z}";
                        if (CoordinateInWorld(movement.X) &&
                            CoordinateInWorld(movement.Y) &&
                            CoordinateInWorld(movement.Z))
                        {
                            state.X = movement.X;
                            state.Y = movement.Y;
                            state.Z = movement.Z;
                        }
                        else
                        {
                            status = StatusCode.OutOfRange;
                        }
                    }
                    else if (packet.Payload is Shoot shoot)
                    {
                        details = $"weaponId={shoot.WeaponId}";
                        if (shoot.WeaponId is >= 1 and <= 3)
                        {
                            state.ShotsFired++;
                            state.LastWeaponId = shoot.WeaponId;
                        }
                        else
                        {
                            status = StatusCode.OutOfRange;
                        }
                    }
                    else
                    {
                        throw new ProtocolException("Unsupported client payload");
                    }

                    var response = new StateUpdate(
                        packet.Header.PacketType,
                        status,
                        state.X,
                        state.Y,
                        state.Z,
                        state.ShotsFired,
                        state.LastWeaponId);

                    byte[] responseBytes = ProtocolSerializer.SerializeStateUpdate(
                        packet.Header.SequenceNumber,
                        response);
                    udp.Send(responseBytes, responseBytes.Length, sender);

                    Log(
                        logFile,
                        $"command={PacketTypeName(packet.Header.PacketType)} " +
                        $"seq={packet.Header.SequenceNumber} from={clientKey} {details} " +
                        $"result={StatusName(status)} " +
                        $"state=({state.X},{state.Y},{state.Z}) shots={state.ShotsFired}");

                    processedPackets++;
                }
                catch (ProtocolException error)
                {
                    Log(
                        logFile,
                        $"MALFORMED from={clientKey} bytes={datagram.Length} reason={error.Message}");
                }
            }

            Log(logFile, $"server stopped after processing {processedPackets} commands");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Server error: {error.Message}");
            return 1;
        }
    }

    private static bool CoordinateInWorld(float value) =>
        float.IsFinite(value) && value >= -WorldLimit && value <= WorldLimit;

    private static void Log(StreamWriter file, string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Console.WriteLine(line);
        file.WriteLine(line);
    }

    private static ServerOptions ParseOptions(string[] args)
    {
        var options = new ServerOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length:
                    options.Port = ParsePositiveInt(args[++i], "port", 65535);
                    break;
                case "--max-packets" when i + 1 < args.Length:
                    options.MaxPackets = ParsePositiveInt(args[++i], "max-packets", int.MaxValue);
                    break;
                case "--log" when i + 1 < args.Length:
                    options.LogFile = args[++i];
                    break;
                case "--help":
                    Console.WriteLine("Usage: UdpGame.Server [--port 27015] [--max-packets N] [--log server.log]");
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

    private sealed class ServerOptions
    {
        public int Port { get; set; } = DefaultPort;
        public int MaxPackets { get; set; }
        public string LogFile { get; set; } = "server.log";
    }

    private sealed class ClientState
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public uint ShotsFired { get; set; }
        public byte LastWeaponId { get; set; }
    }
}
