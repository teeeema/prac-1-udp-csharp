using System.Net;
using System.Net.Sockets;
using UdpGame.Protocol;
using UdpGame.Transport;
using UdpGame.Telemetry;

namespace UdpGame.Client;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            ClientOptions options = ParseOptions(args);
            if (options.ReliabilityExperiment)
            {
                return ReliabilityExperimentRunner.Run(options.Host, options.Port,
                    options.CsvPath ?? "docs/reliability_samples.csv");
            }
            if (options.Experiment)
            {
                return ExperimentRunner.Run(options.Host, options.Port, options.CsvPath ?? "docs/latency_samples.csv");
            }

            using var transport = UdpTransport.Connect(options.Host, options.Port, 20);
            var delivery = new ClientDeliveryTracker();

            int responsesReceived = 0;
            Console.WriteLine(
                $"UDP client sending {options.Count} commands to {options.Host}:{options.Port}");

            for (int index = 0; index < options.Count; index++)
            {
                ushort sequenceNumber = checked((ushort)(index + 1));
                if (index % 2 == 0)
                {
                    float step = index / 2 + 1;
                    var movement = new Movement(step, step * 2f, step * -0.5f);
                    transport.Send(ProtocolSerializer.SerializeMovement(sequenceNumber, movement));
                    Console.WriteLine($"Sent MOVEMENT seq={sequenceNumber} position=({movement.X}, {movement.Y}, {movement.Z})");
                    ulong deadline = MonotonicClock.NowMicroseconds() + (ulong)options.TimeoutMilliseconds * 1000UL;
                    bool received = false;
                    while (MonotonicClock.NowMicroseconds() < deadline && !received)
                    {
                        try
                        {
                            var sender = new IPEndPoint(IPAddress.Any, 0);
                            Packet response = ProtocolSerializer.Deserialize(transport.Receive(ref sender));
                            if (response.Payload is AckPayload ack)
                            {
                                delivery.OnAck(ack, MonotonicClock.NowMicroseconds());
                            }
                            else if (response.Header.SequenceNumber == sequenceNumber && response.Payload is StateUpdate)
                            {
                                PrintResponse(response);
                                responsesReceived++;
                                received = true;
                            }
                        }
                        catch (SocketException error) when (error.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
                        {
                        }
                        catch (ProtocolException error)
                        {
                            Console.Error.WriteLine($"Malformed response ignored: {error.Message}");
                        }
                    }
                    if (!received)
                    {
                        Console.Error.WriteLine($"Timeout waiting for response to seq={sequenceNumber}");
                    }
                }
                else
                {
                    byte weaponId = (byte)(((index / 2) % 3) + 1);
                    Console.WriteLine($"Sent SHOOT seq={sequenceNumber} weaponId={weaponId} requiresAck=true");
                    ShootDeliveryResult result = ReliableShootSender.Send(transport, delivery, sequenceNumber, weaponId,
                        waitForState: true, stateTimeoutMs: options.TimeoutMilliseconds, log: Console.WriteLine);
                    if (result.Delivered && result.State is StateUpdate state)
                    {
                        PrintResponse(new Packet(new PacketHeader(PacketType.StateUpdate, sequenceNumber,
                            ProtocolConstants.StateUpdatePayloadSize, ProtocolConstants.ProtocolVersion), state));
                        responsesReceived++;
                    }
                    else if (result.Delivered)
                    {
                        Console.Error.WriteLine($"SHOOT seq={sequenceNumber} delivered, but STATE_UPDATE not received");
                    }
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
                case "--experiment":
                    options.Experiment = true;
                    break;
                case "--reliability-experiment":
                    options.ReliabilityExperiment = true;
                    break;
                case "--csv" when i + 1 < args.Length:
                    options.CsvPath = args[++i];
                    break;
                case "--help":
                    Console.WriteLine(
                        "Usage: UdpGame.Client [--host 127.0.0.1] [--port 27015] " +
                        "[--count 6] [--interval-ms 500] [--timeout-ms 2000] " +
                        "[--experiment | --reliability-experiment] [--csv path]");
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete argument: {args[i]}");
            }
        }

        if (options.Experiment && options.ReliabilityExperiment)
        {
            throw new ArgumentException("Choose either --experiment or --reliability-experiment");
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
        public bool Experiment { get; set; }
        public bool ReliabilityExperiment { get; set; }
        public string? CsvPath { get; set; }
    }
}
