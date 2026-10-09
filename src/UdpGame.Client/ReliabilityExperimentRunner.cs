using System.Globalization;
using System.Net;
using System.Net.Sockets;
using UdpGame.Protocol;
using UdpGame.Telemetry;
using UdpGame.Transport;

namespace UdpGame.Client;

internal static class ReliabilityExperimentRunner
{
    private const int CommandsPerSeries = 50;
    private const int IntervalMs = 50; // Quiet time after each completed command, no overlapping SHOOT.

    public static int Run(string host, int port, string csvPath)
    {
        ulong startedUs = MonotonicClock.NowMicroseconds();
        ushort nextSequence = 1;
        var allRows = new List<Sample>();
        foreach (NetworkProfile profile in NetworkProfiles.ReliabilityProfiles)
        {
            // Independent random streams in the two directions, fresh transport/tracker per series.
            using var transport = UdpTransport.Connect(host, port, 20, profile, profile with { Seed = profile.Seed + 1 });
            var tracker = new ClientDeliveryTracker();
            var rows = new List<Sample>();
            for (int index = 1; index <= CommandsPerSeries; index++)
            {
                ushort probeSequence = nextSequence++;
                double? probeRtt = MeasurePing(transport, tracker, probeSequence);
                double initialRto = tracker.Timeout.RtoMs;
                ushort sequence = nextSequence++;
                ShootDeliveryResult result = ReliableShootSender.Send(transport, tracker, sequence,
                    (byte)((index - 1) % 3 + 1), log: text => Console.WriteLine($"{profile.Id}: {text}"));
                rows.Add(new Sample(profile, index, result,
                    (result.FirstSentAtUs - startedUs) / 1000d,
                    result.AckReceivedAtUs is ulong ackUs ? (ackUs - startedUs) / 1000d : null,
                    tracker.Timeout.RtoMs, initialRto, probeRtt, transport.DroppedSendCount, transport.DroppedReceiveCount));
                if (index < CommandsPerSeries)
                {
                    Thread.Sleep(IntervalMs);
                }
            }
            allRows.AddRange(rows);
            PrintStatistics(profile.Id, rows);
        }

        WriteCsv(csvPath, allRows);
        Console.WriteLine($"CSV written: {csvPath} ({allRows.Count} reliable commands)");
        return allRows.Count == NetworkProfiles.ReliabilityProfiles.Count * CommandsPerSeries ? 0 : 1;
    }

    private static double? MeasurePing(UdpTransport transport, ClientDeliveryTracker tracker, ushort sequence)
    {
        ulong sentUs = MonotonicClock.NowMicroseconds();
        transport.Send(tracker.CreatePing(sequence, sentUs));
        while (MonotonicClock.NowMicroseconds() - sentUs < 1_000_000UL)
        {
            try
            {
                var sender = new IPEndPoint(IPAddress.Any, 0);
                Packet packet = ProtocolSerializer.Deserialize(transport.Receive(ref sender));
                ulong receivedUs = MonotonicClock.NowMicroseconds();
                if (packet.Payload is AckPayload ack)
                {
                    tracker.OnAck(ack, receivedUs); // Late/duplicate ACK never produces another sample.
                }
                else if (packet.Payload is Pong pong)
                {
                    double? rtt = tracker.OnPong(packet.Header.SequenceNumber, pong, receivedUs);
                    if (packet.Header.SequenceNumber == sequence && rtt.HasValue)
                    {
                        return rtt;
                    }
                }
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
            {
            }
            catch (ProtocolException error)
            {
                Console.WriteLine($"Malformed probe response ignored: {error.Message}");
            }
        }
        return null;
    }

    private static void PrintStatistics(string series, List<Sample> rows)
    {
        int delivered = rows.Count(row => row.Delivery.Delivered);
        int first = rows.Count(row => row.Delivery.Delivered && row.Delivery.Attempts == 1);
        int failed = rows.Count - delivered;
        double? meanAck = rows.Where(row => row.Delivery.Delivered).Select(row => row.Delivery.TimeToAckMs).Average();
        Console.WriteLine(FormattableString.Invariant($"RESULT {series}: sent={rows.Count} delivered={delivered} first_attempt={first} first_attempt_percent={first * 100d / rows.Count:F6} retransmit_total={rows.Sum(row => row.Delivery.Attempts - 1)} failed={failed} failed_percent={failed * 100d / rows.Count:F6} avg_attempts={rows.Average(row => row.Delivery.Attempts):F6} avg_time_to_ack_ms={meanAck:F6} final_rto_ms={rows[^1].FinalRtoMs:F6} max_attempts={rows.Max(row => row.Delivery.Attempts)}"));
    }

    private static void WriteCsv(string path, List<Sample> rows)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var writer = new StreamWriter(path, append: false);
        writer.WriteLine("series,command_index,sequence_number,first_sent_at_ms,ack_received_at_ms,attempts,retransmissions,delivered,failed,time_to_ack_ms,final_rto_ms,ack_sample_used_for_rto,loss_percent,delay_ms,jitter_min_ms,jitter_max_ms,seed,receive_seed,rto_before_send_ms,probe_rtt_ms,dropped_send_total,dropped_receive_total");
        foreach (Sample row in rows)
        {
            ShootDeliveryResult d = row.Delivery;
            writer.WriteLine(string.Join(",", row.Profile.Id, row.Index, d.Sequence,
                Number(row.FirstSentAtMs), Number(row.AckReceivedAtMs), d.Attempts, d.Attempts - 1,
                d.Delivered ? 1 : 0, d.Delivered ? 0 : 1, Number(d.TimeToAckMs), Number(row.FinalRtoMs),
                d.SampleUsedForRto ? 1 : 0, Number(row.Profile.LossPercent), row.Profile.BaseDelayMs,
                row.Profile.JitterMinMs, row.Profile.JitterMaxMs, row.Profile.Seed, row.Profile.Seed + 1,
                Number(row.RtoBeforeSendMs), Number(row.ProbeRttMs), row.DroppedSend, row.DroppedReceive));
        }
    }

    private static string Number(double? value) => value?.ToString("F6", CultureInfo.InvariantCulture) ?? "";

    private sealed record Sample(NetworkProfile Profile, int Index, ShootDeliveryResult Delivery,
        double FirstSentAtMs, double? AckReceivedAtMs, double FinalRtoMs, double RtoBeforeSendMs,
        double? ProbeRttMs, int DroppedSend, int DroppedReceive);
}
